using System.Runtime.InteropServices;
using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OmenTools.Dalamud.Attributes;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.Interop.Game.Models.Native;
using OmenTools.KamiToolKit.Addons;
using OmenTools.OmenService;
using OmenTools.Threading.TaskHelper.Enums;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe class FastGrandCompanyExchange : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = Lang.Get("FastGrandCompanyExchangeTitle"),
        Description = Lang.Get
        (
            "FastGrandCompanyExchangeDescription",
            new Dictionary<string, object>
            {
                ["command"] = COMMAND
            }
        ),
        Category = ModuleCategory.Interface
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private static readonly CompSig GCShopHandlerSig =
        new("48 8B 05 ?? ?? ?? ?? 33 C9 40 84 FF 48 0F 45 C1 48 89 05");
    private GCShopEventHandler** GCShopHandlerPtr;

    private static readonly CompSig GCShopExchangeSig =
        new("84 D2 0F 84 ?? ?? ?? ?? 4C 8B DC 49 89 7B");
    private delegate void GCShopExchangeDelegate
    (
        nint                               purchaseInterface,
        [MarshalAs(UnmanagedType.U1)] bool send
    );
    private GCShopExchangeDelegate? GCShopExchange;

    private static readonly CompSig GCShopReloadSig =
        new("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 48 8B 81 ?? ?? ?? ?? 48 8B F9 48 85 C0");
    private delegate void GCShopReloadDelegate
    (
        GCShopEventHandler* handler
    );
    private GCShopReloadDelegate? GCShopReload;

    private (uint ItemID, int Count, byte SubCategory, byte Tier) pendingExchange;
    
    private Config config = null!;

    private DRFastGCExchange? addon;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        TaskHelper ??= new();

        GCShopHandlerPtr = (GCShopEventHandler**)GCShopHandlerSig.GetStatic();
        GCShopExchange   = GCShopExchangeSig.GetDelegate<GCShopExchangeDelegate>();
        GCShopReload     = GCShopReloadSig.GetDelegate<GCShopReloadDelegate>();

        addon = new(this)
        {
            InternalName = "DRFastGCExchange",
            Title        = Info.Title,
            Size         = new(290f, 240f)
        };

        CommandManager.Instance().AddSubCommand
        (
            COMMAND,
            new(OnCommand)
            {
                HelpMessage = Lang.Get("FastGrandCompanyExchange-CommandHelp")
            }
        );
    }

    protected override void Uninit()
    {
        CommandManager.Instance().RemoveSubCommand(COMMAND);

        addon?.Dispose();
        addon = null;
    }

    protected override void ConfigUI()
    {
        using (ImRaii.Heading1(Lang.Get("Command")))
            ImGui.TextWrapped($"/pdr {COMMAND} {Lang.Get("FastGrandCompanyExchange-CommandHelp")}");
    }

    private void OnCommand
    (
        string command,
        string args
    )
    {
        args = args.Trim();
        if (args.Length == 0) return;

        if (args == "default" ||
            args.StartsWith("default ", StringComparison.Ordinal))
        {
            EnqueueByName(config.ExchangeItemName, config.ExchangeItemCount);
            return;
        }

        var itemName  = args;
        var itemCount = -1;

        // 从右往左寻找最后一个空白字符
        var separatorIndex = args.Length - 1;
        while (separatorIndex >= 0 &&
               !char.IsWhiteSpace(args[separatorIndex]))
            separatorIndex--;

        // 尝试将最后一个部分解析为数量
        if (separatorIndex >= 0                                                &&
            int.TryParse(args.AsSpan(separatorIndex + 1), out var parsedCount) &&
            parsedCount >= -1)
        {
            itemName  = args[..separatorIndex].TrimEnd();
            itemCount = parsedCount;
        }

        // 带引号的物品名
        if (itemName is ['"', _, ..] &&
            itemName[^1] == '"')
            itemName = itemName[1..^1];

        if (string.IsNullOrWhiteSpace(itemName)) return;

        EnqueueByName(itemName, itemCount);
    }

    private bool EnqueueByName
    (
        string itemName,
        int    itemCount = -1
    )
    {
        TaskHelper.DelayNext(500);
        TaskHelper.Enqueue(() => PrepareExchange(itemName, itemCount), "军票兑换准备");
        TaskHelper.Enqueue
        (
            ExecuteExchange,
            "军票兑换",
            10000,
            TaskAbortBehaviour.AbortCurrent
        );
        return true;
    }

    private bool PrepareExchange
    (
        string itemName,
        int    itemCount = -1
    )
    {
        pendingExchange = default;

        if (GCShopHandlerPtr == null || GCShopExchange == null || GCShopReload == null) return true;

        if (itemName == "default")
        {
            itemName  = config.ExchangeItemName;
            itemCount = config.ExchangeItemCount;
        }

        if (string.IsNullOrWhiteSpace(itemName) || itemCount == 0)
            return true;

        var grandCompany = PlayerState.Instance()->GrandCompany;
        var gcRank       = PlayerState.Instance()->GetGrandCompanyRank();
        var seals        = InventoryManager.Instance()->GetCompanySeals(grandCompany);
        if (seals == 0) return true;

        var result = LuminaGetter.GetSub<GCScripShopItem>()
                                 .SelectMany(x => x)
                                 .Where(x => LuminaGetter.GetRowOrDefault<GCScripShopCategory>(x.RowId).GrandCompany.RowId == grandCompany)
                                 .Where(x => x.CostGCSeals                                                                 > 0)
                                 .Where(x => gcRank                                                                        >= x.RequiredGrandCompanyRank.RowId)
                                 .Where
                                 (x => (x.Item.ValueNullable?.Name.ToString() ?? string.Empty)
                                      .Contains(itemName, StringComparison.OrdinalIgnoreCase)
                                 )
                                 .OrderBy(x => (x.Item.ValueNullable?.Name.ToString() ?? string.Empty).Length)
                                 .FirstOrDefault();
        if (result.RowId == 0) return true;

        var singleCost = result.CostGCSeals;
        if (singleCost == 0) return true;

        var availableExchangeCount = (int)(seals / singleCost);
        var exchangeCount = Math.Min
        (
            itemCount == -1 ?
                availableExchangeCount :
                itemCount,
            availableExchangeCount
        );

        if (exchangeCount == 0)
        {
            TaskHelper.DelayNext(100);
            return true;
        }

        var categoryData = LuminaGetter.GetRowOrDefault<GCScripShopCategory>(result.RowId);
        pendingExchange = (result.Item.RowId, exchangeCount, (byte)categoryData.SubCategory, (byte)(categoryData.Tier - 1));
        return true;
    }

    private bool ExecuteExchange()
    {
        if (pendingExchange.Count <= 0) return true;

        var handler = *GCShopHandlerPtr;
        if (handler == null) return true;

        var displayIndex = FindDisplayIndex(handler, pendingExchange.ItemID);

        if (displayIndex >= 0)
        {
            handler->SelectedDisplayIndex = (uint)displayIndex;
            handler->ExchangeCount        = (uint)pendingExchange.Count;
            GCShopExchange((nint)(&handler->PurchaseInterface), true);
            pendingExchange = default;
            return true;
        }

        if (handler->SubCategory != pendingExchange.SubCategory ||
            handler->Tier        != pendingExchange.Tier)
        {
            handler->SubCategory = pendingExchange.SubCategory;
            handler->Tier        = pendingExchange.Tier;
            GCShopReload(handler);
        }

        return false;
    }

    private static int FindDisplayIndex
    (
        GCShopEventHandler* handler,
        uint                itemID
    )
    {
        var slots = &handler->Slots;

        for (var i = 0; i < SLOT_COUNT; i++)
        {
            var slot = slots + i;
            if (slot->IsValid == 0) break;
            if (slot->ItemID == itemID && slot->CostGCSeals > 0 && slot->DisplayIndex != uint.MaxValue)
                return (int)slot->DisplayIndex;
        }

        return -1;
    }

    private class DRFastGCExchange
    (
        FastGrandCompanyExchange instance
    ) : AttachedAddon("GrandCompanyExchange")
    {
        protected override void OnSetup
        (
            AtkUnitBase*   addon,
            Span<AtkValue> atkValues
        )
        {
            var layoutNode = new VerticalListNode
            {
                Position    = ContentStartPosition,
                ItemSpacing = 1,
                Width       = ContentSize.X,
                FitContents = true
            };

            var exchangeButtonNode = new TextButtonNode
            {
                Size        = new(layoutNode.Width, 38),
                String      = Lang.Get("Exchange"),
                TextureType = ButtonTextureType.ButtonB,
                OnClick = () =>
                {
                    if (instance.TaskHelper.IsBusy)
                        return;
                    instance.EnqueueByName(instance.config.ExchangeItemName, instance.config.ExchangeItemCount);
                }
            };
            layoutNode.AddNode(exchangeButtonNode);

            layoutNode.AddDummy(5);

            var itemLableNode = new TextNode
            {
                Size     = new(layoutNode.Width, 24),
                String   = Lang.Get("Item"),
                FontSize = 14
            };
            layoutNode.AddNode(itemLableNode);

            var itemNameInputNode = new TextInputNode
            {
                Size            = new(layoutNode.Width, 36),
                String          = instance.config.ExchangeItemName,
                OnInputReceived = x => instance.config.ExchangeItemName = x.ToString(),
                OnInputComplete = UpdateExchangeItem
            };

            itemNameInputNode.OnEditComplete = _ => UpdateExchangeItem(itemNameInputNode.String);
            itemNameInputNode.OnUnfocused    = () => UpdateExchangeItem(itemNameInputNode.String);

            itemNameInputNode.CursorNode.ScaleY        =  1.4f;
            itemNameInputNode.CurrentTextNode.FontSize =  14;
            itemNameInputNode.CurrentTextNode.Y        += 3f;

            layoutNode.AddNode(itemNameInputNode);

            layoutNode.AddDummy(5);

            var countLableNode = new TextNode
            {
                Size     = new(layoutNode.Width, 24),
                String   = Lang.Get("Amount"),
                FontSize = 14
            };
            layoutNode.AddNode(countLableNode);

            var countInputNode = new NumericInputNode
            {
                Size  = new(layoutNode.Width, 36),
                Step  = 1,
                Min   = -1,
                Value = instance.config.ExchangeItemCount,
                OnValueUpdate = newValue =>
                {
                    instance.config.ExchangeItemCount = newValue;

                    instance.config.ExchangeItemCount = (int)MathF.Max(-1, instance.config.ExchangeItemCount);
                    instance.config.Save(instance);
                }
            };
            layoutNode.AddNode(countInputNode);

            layoutNode.AttachNode(this);
            SetWindowSize(Size.X, ContentStartPosition.Y + layoutNode.Height + 16f);
        }

        private void UpdateExchangeItem
        (
            ReadOnlySeString x
        )
        {
            instance.config.ExchangeItemName = x.ToString();

            var grandCompany = PlayerState.Instance()->GrandCompany;
            var gcRank       = PlayerState.Instance()->GetGrandCompanyRank();

            var result = LuminaGetter.GetSub<GCScripShopItem>()
                                     .SelectMany(d => d)
                                     .Where(d => LuminaGetter.GetRowOrDefault<GCScripShopCategory>(d.RowId).GrandCompany.RowId == grandCompany)
                                     .Where(d => gcRank                                                                        >= d.RequiredGrandCompanyRank.RowId)
                                     .Where
                                     (d => (d.Item.ValueNullable?.Name.ToString() ?? string.Empty)
                                          .Contains(instance.config.ExchangeItemName, StringComparison.OrdinalIgnoreCase)
                                     )
                                     .OrderBy(d => (d.Item.ValueNullable?.Name.ToString() ?? string.Empty).Length)
                                     .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(instance.config.ExchangeItemName) || result.RowId == 0)
                instance.config.ExchangeItemName = LuminaWrapper.GetItemName(21072);
            else if (result.RowId != 0)
                instance.config.ExchangeItemName = result.Item.Value.Name.ToString();

            if (instance.config.ExchangeItemName == x.ToString())
                return;

            instance.config.Save(instance);
        }
    }

    private class Config : ModuleConfig
    {
        public int    ExchangeItemCount = -1;
        public string ExchangeItemName  = string.Empty;
    }

    #region IPC

    [IPCProvider("DailyRoutines.Modules.FastGrandCompanyExchange.IsBusy")]
    private bool IsCurrentlyBusy => TaskHelper?.IsBusy ?? false;

    [IPCProvider("DailyRoutines.Modules.FastGrandCompanyExchange.EnqueueByName")]
    private bool EnqueueByNameIPC
    (
        string itemName,
        int    itemCount
    ) => EnqueueByName(itemName, itemCount);

    #endregion

    #region 常量

    private const string COMMAND = "gce";

    private const int SLOT_COUNT = 50;

    #endregion
}
