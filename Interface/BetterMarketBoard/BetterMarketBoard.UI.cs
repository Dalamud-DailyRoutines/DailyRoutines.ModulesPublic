using System.Numerics;
using DailyRoutines.Extensions;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe partial class BetterMarketBoard
{
    private const ImGuiWindowFlags OVERLAY_CHILD_FLAGS = ImGuiWindowFlags.NoBackground |
                                                         ImGuiWindowFlags.NoScrollbar  |
                                                         ImGuiWindowFlags.NoScrollWithMouse;

    private static readonly ItemSelectorTab[] ItemSelectorTabs =
    [
        ItemSelectorTab.Search,
        ItemSelectorTab.Favorite,
        ItemSelectorTab.History
    ];

    private string                    itemSearchInput          = string.Empty;
    private Vector2                   marketDataTableImageSize = new(32);
    private List<MarketFavoriteItem>? favoriteItemsCache;
    private int                       favoriteItemsVersion;
    private int                       favoriteItemsCacheVersion = -1;
    private bool                      isAllWorldsPriceExpanded;

    private bool isItemListTooltip;
    private bool isHeaderTooltip;

    private ItemSelectorTab currentTab = ItemSelectorTab.Search;

    private bool   isConfirmClearHistory;
    private string historySearchInput = string.Empty;

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Command"));

        using (ImRaii.PushIndent())
            ImGui.TextUnformatted($"/pdr {COMMAND} → {Lang.Get("BetterMarketBoard-CommandHelp")}");

        ImGui.NewLine();

        if (ImGui.Checkbox(Lang.Get("BetterMarketBoard-Config-AppendMarketStatsTooltip"), ref config.AppendMarketStatsTooltip))
            config.Save(this);
        ImGuiOm.HelpMarker(Lang.Get("BetterMarketBoard-Config-AppendMarketStatsTooltip-Help"));
    }

    protected override void OverlayUI()
    {
        Overlay.CollapsedCondition = ImGuiCond.None;
        Overlay.Collapsed          = null;

        SyncItemWithGame();

        var frame = CreateFrameContext();

        using var table = ImRaii.Table("Table", 2, ImGuiTableFlags.Resizable);
        if (!table) return;

        ImGui.TableSetupColumn("Left",  ImGuiTableColumnFlags.WidthStretch, 0.25f);
        ImGui.TableSetupColumn("Right", ImGuiTableColumnFlags.WidthStretch, 1f);

        ImGui.TableNextRow();

        ImGui.TableNextColumn();

        using (var child = ImRaii.Child("###Left", new(-1f), true, OVERLAY_CHILD_FLAGS))
        {
            if (child)
                DrawLeftPanel(frame);
        }

        ImGui.TableNextColumn();

        using (var child = ImRaii.Child("###Right", new(-1f), true, OVERLAY_CHILD_FLAGS))
        {
            if (child)
                DrawRightContent(frame);
        }
    }

    private MarketBoardUIContext CreateFrameContext()
    {
        var itemID = provider.SelectedItemID;

        var   hasItem     = false;
        var   itemData    = default(Item);
        uint? npcGilPrice = null;

        if (itemID != 0 && LuminaGetter.TryGetRow<Item>(itemID, out var row))
        {
            hasItem     = true;
            itemData    = row;
            npcGilPrice = provider.GetNPCGilPrice(itemID);
        }

        return new
        (
            provider,
            this,
            itemID,
            hasItem,
            itemData,
            npcGilPrice,
            GameState.CurrentWorld,
            provider.SelectedWorldID,
            provider.HQOnly,
            IsAbleToSearchLocalMarket()
        );
    }

    private void SyncItemWithGame()
    {
        var infoProxy = InfoProxy;
        var proxyItemID = infoProxy == null ?
                              0u :
                              infoProxy->SearchItemId;
        if (proxyItemID == 0 || proxyItemID == provider.SelectedItemID) return;

        if (Environment.TickCount64 - provider.LastSelectTime < 500) return;

        if (!LuminaGetter.TryGetRow<Item>(proxyItemID, out var item) || item.ItemSearchCategory.RowId == 0)
            return;

        provider.SelectItem(proxyItemID);
    }

    private readonly struct MarketBoardUIContext
    (
        MarketDataProvider provider,
        BetterMarketBoard  owner,
        uint               itemID,
        bool               hasItem,
        Item               itemData,
        uint?              npcGilPrice,
        uint               currentWorldID,
        uint               selectedWorldID,
        bool               hqOnly,
        bool               isLocalMarketSearchable
    )
    {
        public MarketDataProvider Provider { get; } = provider;
        public BetterMarketBoard  Owner    { get; } = owner;

        public uint  ItemID                  { get; } = itemID;
        public bool  HasItem                 { get; } = hasItem;
        public Item  ItemData                { get; } = itemData;
        public uint? NPCGilPrice             { get; } = npcGilPrice;
        public uint  CurrentWorldID          { get; } = currentWorldID;
        public uint  SelectedWorldID         { get; } = selectedWorldID;
        public bool  HQOnly                  { get; } = hqOnly;
        public bool  IsLocalMarketSearchable { get; } = isLocalMarketSearchable;

        public bool IsViewingCurrentWorld =>
            SelectedWorldID == CurrentWorldID;
    }
}
