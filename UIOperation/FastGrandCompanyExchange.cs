using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OmenTools.Dalamud.Attributes;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading.TaskHelper.Enums;

namespace DailyRoutines.ModulesPublic;

public class FastGrandCompanyExchange : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("FastGrandCompanyExchangeTitle"),
        Description = Lang.Get("FastGrandCompanyExchangeDescription"),
        Category    = ModuleCategory.UIOperation
    };
    
    private bool IsExchanging => TaskHelper?.IsBusy ?? false;

    private Config config = null!;

    private DRFastGCExchange? addon;
    
    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        TaskHelper ??= new();

        addon ??= new(this)
        {
            InternalName          = "DRFastGCExchange",
            Title                 = Info.Title,
            Size                  = new(290f, 240f),
            RememberClosePosition = true
        };

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "GrandCompanyExchange", OnAddon);

        CommandManager.Instance().AddSubCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("FastGrandCompanyExchange-CommandHelp") });
    }

    protected override void Uninit()
    {
        CommandManager.Instance().RemoveSubCommand(COMMAND);
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);

        addon?.Dispose();
        addon = null;
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Command")}:");

        ImGui.SameLine();
        ImGui.TextWrapped($"/pdr {COMMAND} {Lang.Get("FastGrandCompanyExchange-CommandHelp")}");
    }

    private unsafe void OnAddon(AddonEvent type, AddonArgs? args)
    {
        if (addon.IsOpen || !GrandCompanyExchange->IsAddonAndNodesReady()) return;
        addon.Open();
    }

    private void OnCommand(string command, string args)
    {
        args = args.Trim();
        if (string.IsNullOrWhiteSpace(args)) return;

        var splited = args.Split(' ');
        if (splited.Length is not (1 or 2)) return;

        if (splited[0] == "default")
        {
            EnqueueByName(config.ExchangeItemName, config.ExchangeItemCount);
            return;
        }

        var itemCount = splited.Length == 2 && int.TryParse(splited[1], out var itemCountParsed) && itemCountParsed >= -1 ? itemCountParsed : -1;
        EnqueueByName(splited[0], itemCount);
    }

    private unsafe bool EnqueueByName(string itemName, int itemCount = -1)
    {
        if (!GrandCompanyExchange->IsAddonAndNodesReady()) return false;

        if (itemName == "default")
        {
            itemName  = config.ExchangeItemName;
            itemCount = config.ExchangeItemCount;
        }

        var grandCompany = PlayerState.Instance()->GrandCompany;
        var gcRank       = PlayerState.Instance()->GetGrandCompanyRank();
        var seals        = InventoryManager.Instance()->GetCompanySeals(grandCompany);
        if (seals == 0) return true;

        var result = LuminaGetter.GetSub<GCScripShopItem>()
                                 .SelectMany(x => x)
                                 .Where(x => LuminaGetter.GetRow<GCScripShopCategory>(x.RowId)!.Value.GrandCompany.RowId == grandCompany)
                                 .Where(x => gcRank                                                                      >= x.RequiredGrandCompanyRank.RowId)
                                 .Where
                                 (x => (x.Item.ValueNullable?.Name.ToString() ?? string.Empty)
                                      .Contains(itemName, StringComparison.OrdinalIgnoreCase)
                                 )
                                 .OrderBy(x => (x.Item.ValueNullable?.Name.ToString() ?? string.Empty).Length)
                                 .FirstOrDefault();
        if (result.RowId == 0) return true;

        var singleCost             = result.CostGCSeals;
        var availableExchangeCount = (int)(seals / singleCost);
        var exchangeCount          = Math.Min(itemCount == -1 ? availableExchangeCount : itemCount, availableExchangeCount);

        if (exchangeCount == 0)
        {
            // 不管怎么说 Delay 一下方便其他模块控制
            TaskHelper.DelayNext(100);
            return true;
        }

        var categoryData = LuminaGetter.GetRow<GCScripShopCategory>(result.RowId)!.Value;
        var tier         = categoryData.Tier;
        var subCategory  = categoryData.SubCategory;

        if (GrandCompanyExchange->AtkValues[2].UInt != (uint)tier - 1)
        {
            TaskHelper.Enqueue
            (
                () =>
                {
                    if (!GrandCompanyExchange->IsAddonAndNodesReady()) return false;
                    GrandCompanyExchange->Callback(1, tier - 1);
                    return true;
                },
                "点击军衔类别"
            );
        }

        TaskHelper.Enqueue
        (
            () =>
            {
                if (!GrandCompanyExchange->IsAddonAndNodesReady()) return false;
                GrandCompanyExchange->Callback(2, (int)subCategory);
                return true;
            },
            "点击道具类别"
        );

        TaskHelper.Enqueue
        (
            () =>
            {
                if (!GrandCompanyExchange->IsAddonAndNodesReady()) return false;

                var listNode = GrandCompanyExchange->GetComponentListById(57);
                if (listNode == null) return false;

                for (var i = 0; i < 40; i++)
                    try
                    {
                        var offset   = 17 + i;
                        var atkValue = GrandCompanyExchange->AtkValues[offset];
                        if (atkValue.Type == 0 || !atkValue.String.HasValue) continue;

                        var name = atkValue.String.ExtractText();
                        if (string.IsNullOrWhiteSpace(name) || name != result.Item.Value.Name.ToString()) continue;

                        AgentId.GrandCompanyExchange.SendEvent(0, 0, i, exchangeCount, 0, true, false);

                        TaskHelper.Enqueue
                        (
                            () => SelectYesno != null,
                            timeoutBehaviour: TaskAbortBehaviour.AbortCurrent,
                            timeoutMS: 2000
                        );
                        TaskHelper.Enqueue
                        (
                            () =>
                            {
                                if (!GrandCompanyExchange->IsAddonAndNodesReady()) return false;
                                if (SelectYesno == null) return true;

                                AddonSelectYesnoEvent.ClickYes();
                                return false;
                            },
                            timeoutBehaviour: TaskAbortBehaviour.AbortCurrent,
                            timeoutMS: 2000
                        );

                        break;
                    }
                    catch
                    {
                        // ignored
                    }

                return true;
            },
            "点击道具"
        );

        return true;
    }

    private unsafe class DRFastGCExchange
    (
        FastGrandCompanyExchange instance
    ) : NativeAddon
    {
        private bool IsNotClosed { get; set; }

        protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
        {
            var layoutNode = new VerticalListNode
            {
                IsVisible   = true,
                Position    = ContentStartPosition + new Vector2(0, 2),
                ItemSpacing = 1,
                Size        = new(275, 28),
                FitContents = true
            };

            var exchangeButtonNode = new TextButtonNode
            {
                IsVisible = true,
                IsEnabled = true,
                Size      = new(layoutNode.Size.X - 10, 38),
                String    = Lang.Get("Exchange"),
                OnClick = () =>
                {
                    if (instance.TaskHelper.IsBusy) return;
                    instance.EnqueueByName(instance.config.ExchangeItemName, instance.config.ExchangeItemCount);
                }
            };

            layoutNode.AddNode(exchangeButtonNode);

            layoutNode.AddDummy(5);

            var itemLableNode = new TextNode
            {
                IsVisible = true,
                Size      = new(layoutNode.Size.X - 20, 24),
                FontSize  = 14,
                String    = Lang.Get("Item")
            };

            layoutNode.AddNode(itemLableNode);

            var itemNameInputNode = new TextInputNode
            {
                IsVisible       = true,
                Size            = new(layoutNode.Size.X - 10, 35),
                String          = instance.config.ExchangeItemName,
                OnInputReceived = x => instance.config.ExchangeItemName = x.ToString()
            };

            itemNameInputNode.OnInputComplete = UpdateExchangeItem;
            itemNameInputNode.OnEditComplete  = _ => UpdateExchangeItem(itemNameInputNode.String);
            itemNameInputNode.OnUnfocused     = () => UpdateExchangeItem(itemNameInputNode.String);

            itemNameInputNode.CursorNode.ScaleY        =  1.4f;
            itemNameInputNode.CurrentTextNode.FontSize =  14;
            itemNameInputNode.CurrentTextNode.Y        += 3f;

            layoutNode.AddNode(itemNameInputNode);

            layoutNode.AddDummy(5);

            var countLableNode = new TextNode
            {
                IsVisible = true,
                Size      = new(layoutNode.Size.X - 20, 24),
                FontSize  = 14,
                String    = Lang.Get("Amount")
            };

            layoutNode.AddNode(countLableNode);

            var countInputNode = new NumericInputNode
            {
                IsVisible = true,
                Size      = new(layoutNode.Size.X - 10, 35),
                Step      = 1,
                Min       = -1,
                OnValueUpdate = newValue =>
                {
                    instance.config.ExchangeItemCount = newValue;

                    instance.config.ExchangeItemCount = (int)MathF.Max(-1, instance.config.ExchangeItemCount);
                    instance.config.Save(instance);
                },
                Value = instance.config.ExchangeItemCount
            };

            layoutNode.AddNode(countInputNode);

            layoutNode.AttachNode(this);
        }

        private void UpdateExchangeItem(ReadOnlySeString x)
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

            IsNotClosed = true;
            Close();
            instance.config.Save(instance);
        }

        protected override void OnUpdate(AtkUnitBase* addon)
        {
            if (GrandCompanyExchange == null)
            {
                Close();
                return;
            }

            var position = new Vector2
            (
                GrandCompanyExchange->RootNode->ScreenX - addon->GetScaledWidth(true),
                GrandCompanyExchange->RootNode->ScreenY
            );

            SetWindowPosition(position);
        }

        protected override void OnFinalize(AtkUnitBase* addon)
        {
            if (IsNotClosed)
            {
                IsNotClosed = false;
                return;
            }

            IsNotClosed = false;

            if (GrandCompanyExchange == null) return;
            GrandCompanyExchange->Close(true);
        }
    }

    private class Config : ModuleConfig
    {
        public int    ExchangeItemCount = -1;
        public string ExchangeItemName  = string.Empty;
    }
    
    #region IPC

    [IPCProvider("DailyRoutines.Modules.FastGrandCompanyExchange.IsBusy")]
    private bool IsCurrentlyBusy => IsExchanging;
    
    [IPCProvider("DailyRoutines.Modules.FastGrandCompanyExchange.EnqueueByName")]
    private bool EnqueueByNameIPC(string itemName, int itemCount) => EnqueueByName(itemName, itemCount);

    #endregion

    #region 常量

    private const string COMMAND = "gce";

    #endregion
}
