using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.RemoteInteraction.Universalis;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using OmenTools.Dalamud.Abstractions;
using OmenTools.Dalamud.Attributes;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe partial class BetterMarketBoard : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = Lang.Get("BetterMarketBoardTitle"),
        Description         = Lang.Get("BetterMarketBoardDescription", COMMAND),
        Category            = ModuleCategory.Interface,
        Author              = ["Fragile"],
        ModulesPrerequisite = ["FastWorldTravel", "AutoShowItemNPCShopInfo"],
        ModulesRecommend    = ["AutoRefreshMarketSearchResult"],
        PreviewImageURL =
        [
            "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/BetterMarketBoard/preview-1.png"
        ]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true, AllDefaultEnabled = true };

    private static InfoProxyItemSearch* InfoProxy => InfoProxyItemSearch.Instance();

    private MarketDataProvider   provider        = null!;
    private PriceMonitorProvider monitorProvider = null!;
    private Config               config          = null!;
    private LuminaSearcher<Item> searcher        = null!;
    private SearchInMarketMenu   contextMenu     = null!;

    // Region Name - DC Name - World ID - World Name
    private Dictionary<string, Dictionary<string, Dictionary<uint, string>>> allWorlds = [];

    private readonly Dictionary<uint, List<Item>> searchCategoryToItems = [];

    private readonly Dictionary<uint, uint> itemIDToPayloadID = [];
    
    private uint lastWorldID;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();
        if (config is { CurrentMonitorItem: not null })
        {
            var item = config.CurrentMonitorItem;

            if (!LuminaGetter.TryGetRow<Item>(item.ItemID, out var itemData))
            {
                config.CurrentMonitorItem = null;
                config.Save(this);
            }
            else if (item.HQOnly && !itemData.CanBeHq)
            {
                item.HQOnly = false;
                config.Save(this);
            }
        }
        
        if (config.AllWorlds is { Count: > 0 })
            allWorlds = config.AllWorlds;

        Overlay            ??= new(this);
        Overlay.Flags      =   WINDOW_FLAGS;
        Overlay.WindowName =   $"{LuminaWrapper.GetPlaceName(1281)}###BetterMarketBoard-Overlay";
        Overlay.SizeConstraints = new()
        {
            MaximumSize = new(float.MaxValue),
            MinimumSize = ScaledVector2(300f, 200f)
        };
        
        var itemsWithCategory =
            LuminaGetter.Get<Item>()
                        .Where(x => !string.IsNullOrEmpty(x.Name.ToString()) && x.ItemSearchCategory.RowId > 0)
                        .GroupBy(x => x.ItemSearchCategory.RowId)
                        .ToDictionary
                        (
                            g => g.Key,
                            g => g.OrderBy(x => x.LevelItem.RowId).ToList()
                        );
        foreach (var (catID, itemList) in itemsWithCategory)
            searchCategoryToItems[catID] = itemList;
        
        provider        = new(this);
        monitorProvider = new(this);
        
        provider.AnchorWorld();
        lastWorldID = GameState.CurrentWorld;

        searcher = new
        (
            [
                .. itemsWithCategory.Values
                                    .SelectMany(x => x)
                                    .GroupBy(x => x.Name.ToString())
                                    .Select(x => x.First())
            ],
            [x => x.Name.ToString(), x => x.RowId.ToString(), x => x.LevelItem.RowId.ToString()]
        );

        TaskHelper = new()
        {
            TaskIntervalMS = 500
        };

        TaskHelper.Enqueue
        (() =>
            {
                _ = RemoteUniversalisCatalog.GetDataCentersOrRequest();
                _ = RemoteUniversalisCatalog.GetWorldsOrRequest();

                if (!RemoteUniversalisCatalog.TryGetDataCenters(out var dataCenters) ||
                    !RemoteUniversalisCatalog.TryGetWorlds(out var worlds))
                    return false;

                var newAllWorlds =
                    dataCenters
                        .GroupBy(dc => dc.Region)
                        .ToDictionary
                        (
                            region => region.Key,
                            region => region
                                .ToDictionary
                                (
                                    dc => dc.Name,
                                    dc => dc.Worlds.ToDictionary
                                    (
                                        worldID => worldID,
                                        worldID => worlds.FirstOrDefault(w => w.ID == worldID)?.Name ?? string.Empty
                                    )
                                )
                        );

                allWorlds        = newAllWorlds;
                config.AllWorlds = newAllWorlds;
                config.Save(this);

                provider.AnchorRegion();

                if (provider.SelectedItemID != 0)
                    provider.SelectItem(provider.SelectedItemID, null, provider.HQOnly);

                return true;
            }
        );

        CommandManager.Instance().AddSubCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("BetterMarketBoard-CommandHelp") });
        
        if (IsAbleToSearchMarket()                                              &&
            InfoProxy               != null                                     &&
            InfoProxy->SearchItemId != 0                                        &&
            LuminaGetter.TryGetRow<Item>(InfoProxy->SearchItemId, out var data) &&
            data.ItemSearchCategory.RowId > 0)
        {
            var itemID = InfoProxy->SearchItemId;
            InfoProxy->SearchItemId = 0;
            provider.SelectItem(itemID);
        }

        FrameworkManager.Instance().Reg(OnMonitorUpdate, 60_000);
        FrameworkManager.Instance().Reg(OnWorldWatch,    1_000);

        TooltipManager.Instance().RegItem(OnItemTooltipUpdate);

        contextMenu = new(this);
        ContextMenuManager.Instance().Reg(contextMenu);
    }

    protected override void Uninit()
    {
        ContextMenuManager.Instance().Unreg(contextMenu);
        TooltipManager.Instance().Unreg(OnItemTooltipUpdate);
        FrameworkManager.Instance().Unreg(OnMonitorUpdate, OnWorldWatch);
        CommandManager.Instance().RemoveSubCommand(COMMAND);

        itemIDToPayloadID.ForEach(x => LinkPayloadManager.Instance().Unreg(x.Value));
        itemIDToPayloadID.Clear();

        searchCategoryToItems.Clear();

        provider.ClearAllData();
        monitorProvider = null!;
    }

    #region 事件

    private void OnCommand
    (
        string command,
        string args
    )
    {
        args = args.Trim();

        if (string.IsNullOrEmpty(args))
        {
            ToggleOverlay();
            return;
        }

        if (uint.TryParse(args, out var itemIDInput) && LuminaGetter.TryGetRow<Item>(itemIDInput, out _))
        {
            provider.SelectItem(itemIDInput);
            Overlay.IsOpen = true;
        }
        else
        {
            var firstFound = searcher.Data
                                     .Where(x => x.Name.ToString().Contains(args, StringComparison.OrdinalIgnoreCase))
                                     .OrderBy(x => x.Name.ToString().Length)
                                     .FirstOrDefault();
            if (firstFound.RowId == 0) return;

            provider.SelectItem(firstFound.RowId);
            ExecuteOverlaySearch(firstFound.Name.ToString());
            Overlay.IsOpen = true;
        }
    }

    private void OnWorldWatch
    (
        IFramework framework
    )
    {
        if (!IsAbleToSearchMarket())
            return;

        var worldID = GameState.CurrentWorld;
        if (worldID != lastWorldID )
        {
            lastWorldID = worldID;

            if (worldID != 0)
                provider.ResyncAfterWorldChange();
        }

        provider.RetryLocalSearchIfStale();
    }

    private void OnMonitorUpdate
    (
        IFramework framework
    )
    {
        if (!IsAbleToSearchMarket())
            return;
        
        monitorProvider.Update();
    }

    #endregion

    #region 工具

    private void ExecuteOverlaySearch
    (
        string searchInput
    )
    {
        itemSearchInput = searchInput;
        currentTab      = ItemSelectorTab.Search;
        searcher.Search(searchInput);
    }

    private void ToggleOverlay
    (
        bool? isOpen = null
    )
    {
        provider.EnsureAnchored();

        if (isOpen == null)
            Overlay.IsOpen ^= true;
        else
            Overlay.IsOpen = isOpen.Value;

        Overlay.Collapsed = false;
    }

    #endregion
    
    private class SearchInMarketMenu
    (
        BetterMarketBoard module
    ) : ContextMenuEntry
    {
        public override string Identifier => nameof(BetterMarketBoard);

        public override ContextMenuItem? Create
        (
            ContextMenuOpenedArgs args
        )
        {
            var itemID = args.TargetItemID;
            if (!LuminaGetter.TryGetRow(itemID, out Item itemRow))
            {
                if (args.TargetItem is not { } targetItem)
                    return null;

                itemID = targetItem.GetBaseItemId();
                if (!LuminaGetter.TryGetRow(itemID, out itemRow))
                    return null;
            }
            
            if (itemRow.ItemSearchCategory.RowId == 0)
                return null;

            return new()
            {
                Name = Lang.Get("BetterMarketBoard-SearchInMarket"),
                OnClicked = _ =>
                {
                    module.ToggleOverlay(true);
                    module.provider.SelectItem(itemID);
                }
            };
        }
    }

    #region 预置数据

    private const string COMMAND = "market";

    private const ImGuiWindowFlags WINDOW_FLAGS = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

    private static readonly List<ItemSearchCategory> ValidCategories =
    [
        .. LuminaGetter.Get<ItemSearchCategory>()
                       .Where(x => !string.IsNullOrEmpty(x.Name.ToString()) && x.Category > 0)
    ];

    #endregion

    #region IPC

    [IPCSubscriber("DailyRoutines.Modules.AutoShowItemNPCShopInfo.OpenByItemID")]
    private static IPCSubscriber<uint, bool> OpenShopListByItemIDIPC;

    [IPCProvider("DailyRoutines.Modules.BetterMarketBoard.SearchItem")]
    private bool SearchItemIPC
    (
        uint itemID
    )
    {
        if (itemID == 0) return false;

        provider.AnchorWorld();
        provider.SelectItem(itemID);
        return true;
    }

    [IPCProvider("DailyRoutines.Modules.BetterMarketBoard.ToggleOverlay")]
    private bool ToggleOverlayIPC(bool? isOpen)
    {
        if (Overlay == null) return false;
        
        ToggleOverlay(isOpen);
        return true;
    }

    #endregion
}
