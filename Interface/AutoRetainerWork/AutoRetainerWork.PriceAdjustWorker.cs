using System.Numerics;
using DailyRoutines.Extensions;
using DailyRoutines.Internal;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Network.Structures;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using OmenTools.Dalamud.Abstractions;
using OmenTools.Dalamud.Attributes;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Interop.Game.Lumina;
using OmenTools.KamiToolKit.Addons;
using OmenTools.KamiToolKit.Nodes;
using OmenTools.OmenService;
using OmenTools.Threading.TaskHelper;
using Action = System.Action;
using AgentRetainer = OmenTools.Interop.Game.Models.Native.AgentRetainer;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe partial class AutoRetainerWork
{
    [IPCSubscriber("DailyRoutines.Modules.BetterMarketBoard.SearchItem")]
    private static IPCSubscriber<uint, bool> SearchItemIPC;

    [IPCSubscriber("DailyRoutines.Modules.BetterMarketBoard.ToggleOverlay")]
    private static IPCSubscriber<bool?, bool> ToggleOverlayIPC;

    private class PriceAdjustWorker
    (
        AutoRetainerWork module
    ) : RetainerWorkerBase(module)
    {
        private delegate void MoveToRetainerMarketDelegate
        (
            InventoryManager* manager,
            InventoryType     srcInv,
            ushort            srcSlot,
            InventoryType     dstInv,
            ushort            dstSlot,
            uint              quantity,
            uint              unitPrice
        );

        private Hook<MoveToRetainerMarketDelegate>? MoveToRetainerMarketHook;

        private TaskHelper?     taskHelper;
        private ItemSelectCombo itemSelectCombo = null!;

        private          ItemConfig?    selectedItemConfig;
        private readonly Vector2        childSizeLeft     = ScaledVector2(200, 400);
        private          Vector2        childSizeRight    = ScaledVector2(450, 400);
        private          string         presetSearchInput = string.Empty;
        private          bool           newConfigItemHQ;
        private          AbortCondition conditionInput = AbortCondition.低于最小值;
        private          AbortBehavior  behaviorInput  = AbortBehavior.无;

        private PriceAdjustContextMenuEntry? contextMenuEntry;
        private PriceAdjustAddon?            priceAdjustAddon;

        private AtkEventWrapper? openMarketEvent;
        private AtkEventWrapper? priceAdjustAllSameEvent;
        private ResNode?         autoPriceAdjustWarningNode;

        private bool isPriceAdjustAllSameItems;
        
        public override bool IsWorkerBusy() =>
            taskHelper?.IsBusy ?? false;

        public override void Init()
        {
            itemSelectCombo = new("AddNewItem");
            MoveToRetainerMarketHook ??= IGameInteropProvider.Instance().HookFromMemberFunction
            (
                typeof(InventoryManager.MemberFunctionPointers),
                "MoveToRetainerMarket",
                (MoveToRetainerMarketDelegate)MoveToRetainerMarketDetour
            );
            MoveToRetainerMarketHook.Enable();

            taskHelper                 ??= new() { TimeoutMS = 30_000, ShowDebug = true };
            taskHelper.EnterBusyAction =   () => ToggleOverlayIPC.TryInvokeFunc(true);
            taskHelper.LeaveBusyAction = () =>
            {
                if (RetainerSellList->IsAddonAndNodesReady())
                    return;
                ToggleOverlayIPC.TryInvokeFunc(false);
            };
            
            contextMenuEntry = new(this);
            priceAdjustAddon = new(this)
            {
                InternalName = "DRAutoRetainerWorkPriceAdjust",
                Title        = Lang.Get("AutoRetainerWork-PriceAdjust-Title"),
                Size         = new(260f, 320f)
            };

            IMarketBoard.Instance().HistoryReceived   += OnHistoryReceived;
            IMarketBoard.Instance().OfferingsReceived += OnOfferingReceived;

            IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup, "RetainerSell", OnRetainerSell);
            if (RetainerSell->IsAddonAndNodesReady())
                OnRetainerSell(AddonEvent.PostSetup, null);

            ContextMenuManager.Instance().Reg(contextMenuEntry);
        }

        public override void Uninit()
        {
            ContextMenuManager.Instance().Unreg(contextMenuEntry);

            priceAdjustAddon?.Dispose();
            priceAdjustAddon = null;
            
            openMarketEvent?.Dispose();
            openMarketEvent = null;
            
            priceAdjustAllSameEvent?.Dispose();
            priceAdjustAllSameEvent = null;

            MoveToRetainerMarketHook?.Dispose();
            MoveToRetainerMarketHook = null;

            IAddonLifecycle.Instance().UnregisterListener(OnRetainerSell);
            
            autoPriceAdjustWarningNode?.Dispose();
            autoPriceAdjustWarningNode = null;

            IMarketBoard.Instance().HistoryReceived   -= OnHistoryReceived;
            IMarketBoard.Instance().OfferingsReceived -= OnOfferingReceived;

            PriceCacheManager.ClearCache();

            contextMenuEntry = null;
            
            taskHelper?.Abort();
            taskHelper?.Dispose();
            taskHelper = null;
        }

        public override void DrawConfig()
        {
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("AutoRetainerWork-PriceAdjust-Title"));

            ItemConfigSelector();

            ImGui.SameLine();
            ItemConfigEditor();
        }

        public override CollaspingCategoryNode CreateOverlayCategory
        (
            float width
        ) =>
            CreateOverlayCategory
            (
                Lang.Get("AutoRetainerWork-PriceAdjust-Title"),
                width,
                CreateOverlayText(Lang.Get("AutoRetainerWork-PriceAdjust-AutoAdjustPrice-AllRetainers"), width),
                CreateOverlayButtonRow
                (
                    () =>
                    {
                        if (taskHelper is not { IsBusy: false }) return;
                        EnqueuePriceAdjustAllRetainers();
                    },
                    () => taskHelper?.Abort(),
                    width
                ),
                CreateOverlayCheckbox
                (
                    Lang.Get("AutoRetainerWork-PriceAdjust-SendProcessMessage"),
                    Module.config.SendPriceAdjustProcessMessage,
                    isChecked =>
                    {
                        Module.config.SendPriceAdjustProcessMessage = isChecked;
                        Module.config.Save(Module);
                    },
                    width
                ),
                CreateOverlayCheckbox
                (
                    Lang.Get("AutoRetainerWork-PriceAdjust-AutoAdjustWhenNewOnSale"),
                    Module.config.AutoPriceAdjustWhenNewOnSale,
                    isChecked =>
                    {
                        Module.config.AutoPriceAdjustWhenNewOnSale = isChecked;
                        Module.config.Save(Module);
                    },
                    width
                )
            );

        #region 配置界面

        private void ItemConfigSelector()
        {
            using var child = ImRaii.Child("ItemConfigSelectorChild", childSizeLeft, true);
            if (!child) return;

            if (ImGuiOm.ButtonIcon("AddNewConfig", FontAwesomeIcon.Plus, Lang.Get("Add")))
                ImGui.OpenPopup("AddNewPreset");

            ImGui.SameLine();

            if (ImGuiOm.ButtonIcon("ImportConfig", FontAwesomeIcon.FileImport, Lang.Get("ImportFromClipboard")))
            {
                var itemConfig = ImportFromClipboard<ItemConfig>();

                if (itemConfig != null)
                {
                    var itemKey = new ItemKey(itemConfig.ItemID, itemConfig.IsHQ).ToString();
                    Module.config.ItemConfigs[itemKey] = itemConfig;
                }
            }

            using (var popup0 = ImRaii.Popup("AddNewPreset"))
            {
                if (popup0)
                {
                    AddNewConfigItemPopup
                    (() =>
                        {
                            var newConfigStr = new ItemKey(itemSelectCombo.SelectedID, newConfigItemHQ).ToString();
                            var newConfig    = new ItemConfig(itemSelectCombo.SelectedID, newConfigItemHQ);

                            if (Module.config.ItemConfigs.TryAdd(newConfigStr, newConfig))
                            {
                                Module.config.Save(Module);
                                ImGui.CloseCurrentPopup();
                            }
                        }
                    );
                }
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputTextWithHint("###PresetSearchInput", Lang.Get("PleaseSearch"), ref presetSearchInput, 100);

            ImGui.Separator();

            foreach (var itemConfig in Module.config.ItemConfigs.ToList())
            {
                if (!string.IsNullOrWhiteSpace(presetSearchInput) && !itemConfig.Value.ItemName.Contains(presetSearchInput))
                    continue;

                if (ImGui.Selectable
                    (
                        $"{itemConfig.Value.ItemName} {(itemConfig.Value.IsHQ ? "(HQ)" : "")}",
                        itemConfig.Value == selectedItemConfig
                    ))
                    selectedItemConfig = itemConfig.Value;

                var isOpenPopup = false;

                using (var popup1 = ImRaii.ContextPopupItem($"{itemConfig.Value}_{itemConfig.Key}_{itemConfig.Value.ItemID}"))
                {
                    if (popup1)
                    {
                        if (ImGui.MenuItem(Lang.Get("ExportToClipboard")))
                            ExportToClipboard(itemConfig.Value);

                        if (ImGui.MenuItem(Lang.Get("AutoRetainerWork-PriceAdjust-CreateNewBaseOnExisted")))
                            isOpenPopup = true;

                        if (itemConfig.Value.ItemID != 0)
                        {
                            if (ImGui.MenuItem(Lang.Get("Delete")))
                            {
                                Module.config.ItemConfigs.Remove(itemConfig.Key);
                                Module.config.Save(Module);

                                selectedItemConfig = null;
                            }
                        }
                    }
                }

                if (isOpenPopup)
                    ImGui.OpenPopup($"AddNewPresetBasedOnExisted_{itemConfig.Key}");

                using (var popup2 = ImRaii.Popup($"AddNewPresetBasedOnExisted_{itemConfig.Key}"))
                {
                    if (popup2)
                    {
                        AddNewConfigItemPopup
                        (() =>
                            {
                                var newConfigStr = new ItemKey(itemSelectCombo.SelectedID, newConfigItemHQ).ToString();
                                var newConfig = new ItemConfig
                                {
                                    ItemID            = itemSelectCombo.SelectedID,
                                    IsHQ              = newConfigItemHQ,
                                    ItemName          = itemSelectCombo.SelectedItem.Name.ToString() ?? string.Empty,
                                    AbortLogic        = itemConfig.Value.AbortLogic,
                                    AdjustBehavior    = itemConfig.Value.AdjustBehavior,
                                    AdjustValues      = itemConfig.Value.AdjustValues,
                                    PriceExpected     = itemConfig.Value.PriceExpected,
                                    PriceMaximum      = itemConfig.Value.PriceMaximum,
                                    PriceMaxReduction = itemConfig.Value.PriceMaxReduction,
                                    PriceMinimum      = itemConfig.Value.PriceMinimum
                                };

                                if (Module.config.ItemConfigs.TryAdd(newConfigStr, newConfig))
                                {
                                    Module.config.Save(Module);
                                    ImGui.CloseCurrentPopup();
                                }
                            }
                        );
                    }
                }

                if (itemConfig.Value is { ItemID: 0, IsHQ: true })
                    ImGui.Separator();
            }
        }

        private void AddNewConfigItemPopup
        (
            Action confirmAction
        )
        {
            ImGui.SetNextItemWidth(200f * GlobalUIScale);
            itemSelectCombo.DrawRadio();

            ImGui.SameLine();
            ImGui.Checkbox("HQ", ref newConfigItemHQ);

            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("Confirm")))
                confirmAction();
        }

        private void ItemConfigEditor()
        {
            childSizeRight.X = ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X;
            using var child = ImRaii.Child("ItemConfigEditorChild", childSizeRight, true);

            if (selectedItemConfig == null) return;

            // 基本信息获取
            if (!LuminaGetter.TryGetRow<Item>(selectedItemConfig.ItemID, out var item)) return;

            var itemName = selectedItemConfig.ItemID == 0 ?
                               Lang.Get("AutoRetainerWork-PriceAdjust-CommonItemPreset") :
                               item.Name.ToString() ?? string.Empty;

            var itemLogo = ITextureProvider.Instance()
                                           .GetFromGameIcon
                                           (
                                               new
                                               (
                                                   selectedItemConfig.ItemID == 0 ?
                                                       65002 :
                                                       (uint)item.Icon,
                                                   selectedItemConfig.IsHQ
                                               )
                                           )
                                           .GetWrapOrDefault();
            if (itemLogo == null) return;

            var itemBuyingPrice = selectedItemConfig.ItemID == 0 ?
                                      1 :
                                      item.PriceLow;

            if (!child) return;

            // 物品基本信息展示
            ImGui.Image(itemLogo.Handle, ScaledVector2(48f));

            ImGui.SameLine();

            using (FontManager.Instance().UIFont140.Push())
                ImGui.TextUnformatted(itemName);

            ImGui.SameLine();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (6f * GlobalUIScale));
            ImGui.TextUnformatted
            (
                selectedItemConfig.IsHQ ?
                    $"({Lang.Get("HQ")})" :
                    string.Empty
            );

            ImGui.Separator();

            // 改价逻辑配置
            using (ImRaii.Group())
            {
                foreach (var behavior in Enum.GetValues<AdjustBehavior>())
                {
                    if (ImGui.RadioButton(AdjustBehaviorLoc.GetValueOrDefault(behavior), behavior == selectedItemConfig.AdjustBehavior))
                    {
                        selectedItemConfig.AdjustBehavior = behavior;
                        Module.config.Save(Module);
                    }
                }
            }

            ImGui.SameLine();

            using (ImRaii.Group())
            {
                if (selectedItemConfig.AdjustBehavior == AdjustBehavior.固定值)
                {
                    var originalValue = selectedItemConfig.AdjustValues[AdjustBehavior.固定值];
                    ImGui.SetNextItemWidth(100f * GlobalUIScale);
                    ImGui.InputInt(Lang.Get("AutoRetainerWork-PriceAdjust-ValueReduction"), ref originalValue);

                    if (ImGui.IsItemDeactivatedAfterEdit())
                    {
                        selectedItemConfig.AdjustValues[AdjustBehavior.固定值] = originalValue;
                        Module.config.Save(Module);
                    }
                }
                else
                    ImGui.Dummy(new(ImGui.GetTextLineHeightWithSpacing()));

                if (selectedItemConfig.AdjustBehavior == AdjustBehavior.百分比)
                {
                    var originalValue = selectedItemConfig.AdjustValues[AdjustBehavior.百分比];
                    ImGui.SetNextItemWidth(100f * GlobalUIScale);
                    ImGui.InputInt(Lang.Get("AutoRetainerWork-PriceAdjust-PercentageReduction"), ref originalValue);

                    if (ImGui.IsItemDeactivatedAfterEdit())
                    {
                        selectedItemConfig.AdjustValues[AdjustBehavior.百分比] = Math.Clamp(originalValue, -99, 99);
                        Module.config.Save(Module);
                    }
                }
                else
                    ImGui.Dummy(new(ImGui.GetTextLineHeightWithSpacing()));
            }

            ImGuiOm.ScaledDummy(10f);

            // 最低可接受价格
            var originalMin = selectedItemConfig.PriceMinimum;
            ImGui.SetNextItemWidth(200f * GlobalUIScale);
            ImGui.InputInt(Lang.Get("AutoRetainerWork-PriceAdjust-PriceMinimum"), ref originalMin);

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                selectedItemConfig.PriceMinimum = Math.Max(1, originalMin);
                Module.config.Save(Module);
            }

            ImGui.SameLine();

            using (ImRaii.Disabled(selectedItemConfig.ItemID == 0))
            {
                if (ImGuiOm.ButtonIcon("ObtainBuyingPrice", FontAwesomeIcon.Store, Lang.Get("AutoRetainerWork-PriceAdjust-ObtainBuyingPrice")))
                {
                    selectedItemConfig.PriceMinimum = Math.Max(1, (int)itemBuyingPrice);
                    Module.config.Save(Module);
                }
            }

            // 最高可接受价格
            var originalMax = selectedItemConfig.PriceMaximum;
            ImGui.SetNextItemWidth(200f * GlobalUIScale);
            ImGui.InputInt(Lang.Get("AutoRetainerWork-PriceAdjust-PriceMaximum"), ref originalMax);

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                selectedItemConfig.PriceMaximum = Math.Min(int.MaxValue, originalMax);
                Module.config.Save(Module);
            }

            // 预期价格
            var originalExpected = selectedItemConfig.PriceExpected;
            ImGui.SetNextItemWidth(200f * GlobalUIScale);
            ImGui.InputInt(Lang.Get("AutoRetainerWork-PriceAdjust-PriceExpected"), ref originalExpected);

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                selectedItemConfig.PriceExpected = Math.Max(originalMin + 1, originalExpected);
                Module.config.Save(Module);
            }

            ImGui.SameLine();

            using (ImRaii.Disabled(selectedItemConfig.ItemID == 0))
            {
                if (ImGuiOm.ButtonIcon("OpenUniversalis", FontAwesomeIcon.Globe, Lang.Get("AutoRetainerWork-PriceAdjust-OpenUniversalis")))
                    Util.OpenLink($"https://universalis.app/market/{selectedItemConfig.ItemID}");
            }

            // 可接受降价值
            var originalPriceReducion = selectedItemConfig.PriceMaxReduction;
            ImGui.SetNextItemWidth(200f * GlobalUIScale);
            ImGui.InputInt(Lang.Get("AutoRetainerWork-PriceAdjust-PriceMaxReduction"), ref originalPriceReducion);

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                selectedItemConfig.PriceMaxReduction = Math.Max(0, originalPriceReducion);
                Module.config.Save(Module);
            }

            // 单次上架数
            var originalUpshelfCount = selectedItemConfig.UpshelfCount;
            ImGui.SetNextItemWidth(200f * GlobalUIScale);
            ImGui.InputInt(Lang.Get("AutoRetainerWork-PriceAdjust-UpshelfCount"), ref originalUpshelfCount);

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                selectedItemConfig.UpshelfCount = originalUpshelfCount;
                Module.config.Save(Module);
            }

            ImGuiOm.ScaledDummy(10f);

            // 意外情况
            using (ImRaii.Group())
            {
                ImGui.SetNextItemWidth(250f * GlobalUIScale);

                using (var combo = ImRaii.Combo("###AddNewLogicConditionCombo", GetAbortConditionName(conditionInput), ImGuiComboFlags.HeightLarge))
                {
                    if (combo)
                    {
                        foreach (var condition in AbortConditions)
                        {
                            if (condition == AbortCondition.无) continue;

                            var isSelected = (conditionInput & condition) == condition;

                            if (ImGui.Selectable(AbortConditionLoc.GetValueOrDefault(condition), isSelected, ImGuiSelectableFlags.DontClosePopups))
                            {
                                var combinedCondition = conditionInput;
                                if (isSelected)
                                    combinedCondition &= ~condition;
                                else
                                    combinedCondition |= condition;

                                conditionInput = combinedCondition;
                            }
                        }
                    }
                }

                ImGui.SetNextItemWidth(250f * GlobalUIScale);

                using (var combo = ImRaii.Combo("###AddNewLogicBehaviorCombo", AbortBehaviorLoc.GetValueOrDefault(behaviorInput), ImGuiComboFlags.HeightLarge))
                {
                    if (combo)
                    {
                        foreach (AbortBehavior behavior in Enum.GetValues(typeof(AbortBehavior)))
                        {
                            if (ImGui.Selectable(AbortBehaviorLoc.GetValueOrDefault(behavior), behaviorInput == behavior, ImGuiSelectableFlags.DontClosePopups))
                                behaviorInput = behavior;
                        }
                    }
                }
            }

            var groupSize0 = ImGui.GetItemRectSize();

            ImGui.SameLine();

            if (ImGuiOm.ButtonIconWithTextVertical
                (
                    FontAwesomeIcon.Plus,
                    Lang.Get("Add"),
                    groupSize0 with { X = ImGui.CalcTextSize(Lang.Get("Add")).X * 2f }
                ))
            {
                if (conditionInput != AbortCondition.无)
                {
                    selectedItemConfig.AbortLogic.TryAdd(conditionInput, behaviorInput);
                    Module.config.Save(Module);
                }
            }

            ImGui.Separator();

            foreach (var logic in selectedItemConfig.AbortLogic.ToList())
            {
                // 条件处理 (键)
                var origConditionStr = GetAbortConditionName(logic.Key);
                ImGui.SetNextItemWidth(300f * GlobalUIScale);
                ImGui.InputText($"###Condition_{origConditionStr}", ref origConditionStr, 100, ImGuiInputTextFlags.ReadOnly);

                if (ImGui.IsItemClicked())
                    ImGui.OpenPopup($"###ConditionSelectPopup_{origConditionStr}");

                using (var popup = ImRaii.Popup($"###ConditionSelectPopup_{origConditionStr}"))
                {
                    if (popup)
                    {
                        foreach (var condition in AbortConditions)
                        {
                            var isSelected = (logic.Key & condition) == condition;

                            if (ImGui.Selectable(AbortConditionLoc.GetValueOrDefault(condition), isSelected))
                            {
                                var combinedCondition = logic.Key;
                                if (isSelected)
                                    combinedCondition &= ~condition;
                                else
                                    combinedCondition |= condition;

                                if (!selectedItemConfig.AbortLogic.ContainsKey(combinedCondition))
                                {
                                    var origBehavior = logic.Value;
                                    selectedItemConfig.AbortLogic[combinedCondition] = origBehavior;
                                    selectedItemConfig.AbortLogic.Remove(logic.Key);
                                    Module.config.Save(Module);
                                }
                            }
                        }
                    }
                }

                ImGui.SameLine();
                ImGui.TextUnformatted("→");

                // 行为处理 (值)
                var origBehaviorStr = AbortBehaviorLoc.GetValueOrDefault(logic.Value);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(300f * GlobalUIScale);
                ImGui.InputText($"###Behavior_{origBehaviorStr}", ref origBehaviorStr, 128, ImGuiInputTextFlags.ReadOnly);

                if (ImGui.IsItemClicked())
                    ImGui.OpenPopup($"###BehaviorSelectPopup_{origBehaviorStr}");

                using (var popup = ImRaii.Popup($"###BehaviorSelectPopup_{origBehaviorStr}"))
                {
                    if (popup)
                    {
                        foreach (var behavior in Enum.GetValues<AbortBehavior>())
                        {
                            if (ImGui.Selectable(AbortBehaviorLoc.GetValueOrDefault(behavior), behavior == logic.Value))
                            {
                                selectedItemConfig.AbortLogic[logic.Key] = behavior;
                                Module.config.Save(Module);
                            }
                        }
                    }
                }

                ImGui.SameLine();
                if (ImGuiOm.ButtonIcon($"Delete_{logic.Key}_{logic.Value}", FontAwesomeIcon.TrashAlt, Lang.Get("Delete")))
                    selectedItemConfig.AbortLogic.Remove(logic.Key);
            }
        }

        #endregion

        #region 事件

        // 出售界面
        private void OnRetainerSell
        (
            AddonEvent type,
            AddonArgs  args
        )
        {
            if (!ICondition.Instance()[ConditionFlag.OccupiedSummoningBell]) return;

            switch (type)
            {
                case AddonEvent.PostSetup:
                    var marketButton = RetainerSell->GetComponentButtonById(4);
                    if (marketButton != null)
                    {
                        marketButton->OwnerNode->ClearEvents();
                        
                        openMarketEvent = new((_, _, _, _) =>
                            {
                                var slot = InventoryManager.Instance()->GetInventorySlot
                                (
                                    AgentRetainer.Instance()->SellItemInventoryType,
                                    AgentRetainer.Instance()->SellItemInventorySlot
                                );
                                if (slot == null) return;
                                
                                RequestMarketItemData(slot->GetBaseItemId(), true);
                            }
                        );
                        openMarketEvent.Add(RetainerSell, (AtkResNode*)marketButton->OwnerNode, AtkEventType.ButtonClick);
                    }

                    if (isPriceAdjustAllSameItems)
                    {
                        var confirmButton = RetainerSell->GetComponentButtonById(21);

                        if (confirmButton != null)
                        {
                            confirmButton->OwnerNode->ClearEvents();
                            
                            priceAdjustAllSameEvent = new((_, _, _, _) =>
                                {
                                    var slot = InventoryManager.Instance()->GetInventorySlot
                                    (
                                        AgentRetainer.Instance()->SellItemInventoryType,
                                        AgentRetainer.Instance()->SellItemInventorySlot
                                    );
                                    if (slot == null) return;

                                    if (TryGetSameItemSlots(slot->GetBaseItemId(), out var slots))
                                    {
                                        foreach (var s in slots)
                                            EnqueuePriceAdjustSlot(s, (uint)AgentRetainer.Instance()->SellItemUnitPrice);
                                    }

                                    RetainerSell->Close(true);
                                    isPriceAdjustAllSameItems = false;
                                }
                            );
                            priceAdjustAllSameEvent.Add(RetainerSell, (AtkResNode*)confirmButton->OwnerNode, AtkEventType.ButtonClick);
                        }
                    }

                    if (Module.config.AutoPriceAdjustWhenNewOnSale)
                    {
                        var countInputComponent = (AtkComponentNumericInput*)RetainerSell->GetComponentByNodeId(14);
                        var priceInputComponent = (AtkComponentNumericInput*)RetainerSell->GetComponentByNodeId(10);

                        if (countInputComponent                             != null &&
                            priceInputComponent                             != null &&
                            AgentRetainer.Instance()->SellItemInventoryType != InventoryType.RetainerMarket)
                        {
                            priceInputComponent->SetEnabledState(false);

                            var ownerNode = priceInputComponent->OwnerNode;
                            if (ownerNode == null) return;

                            var parentNode = ownerNode->ParentNode;
                            if (parentNode == null) return;

                            autoPriceAdjustWarningNode = new()
                            {
                                Size        = new(ownerNode->Width, ownerNode->Height),
                                Position    = new(ownerNode->X, ownerNode->Y),
                                TextTooltip = Lang.Get("AutoRetainerWork-PriceAdjust-AutoAdjustWhenNewOnSale-Warning")
                            };
                            autoPriceAdjustWarningNode.AttachNode(parentNode);
                        }
                    }
                    break;
                
                case AddonEvent.PreFinalize:
                    autoPriceAdjustWarningNode = null;
                    
                    openMarketEvent?.Dispose();
                    openMarketEvent = null;
                    
                    priceAdjustAllSameEvent?.Dispose();
                    priceAdjustAllSameEvent = null;

                    isPriceAdjustAllSameItems = false;
                    break;
            }
        }

        // 当前市场数据获取
        private void OnOfferingReceived
        (
            IMarketBoardCurrentOfferings data
        ) =>
            PriceCacheManager.OnOfferingReceived(Module, data);

        // 历史交易数据获取
        private static void OnHistoryReceived
        (
            IMarketBoardHistory history
        ) =>
            PriceCacheManager.OnHistoryReceived(history);

        // 上架 => 全部拦截
        private void MoveToRetainerMarketDetour
        (
            InventoryManager* manager,
            InventoryType     srcInv,
            ushort            srcSlot,
            InventoryType     dstInv,
            ushort            dstSlot,
            uint              quantity,
            uint              unitPrice
        )
        {
            var slot = manager->GetInventorySlot(srcInv, srcSlot);
            if (slot == null)
            {
                InvokeOriginal();
                return;
            }

            if (Module.config.AutoPriceAdjustWhenNewOnSale && !PluginConfig.Instance().ConflictKeyBinding.IsPressed())
            {
                MoveToRetainerMarketHook.Original(manager, srcInv, srcSlot, dstInv, dstSlot, quantity, 9_9999_9999);
                EnqueuePriceAdjustSlot(dstSlot);
                return;
            }

            InvokeOriginal();

            return;

            void InvokeOriginal() =>
                MoveToRetainerMarketHook.Original(manager, srcInv, srcSlot, dstInv, dstSlot, quantity, unitPrice);
        }

        #endregion

        #region 队列

        internal void EnqueuePriceAdjustAllRetainers()
        {
            if (taskHelper.AbortByConflictKey(Module)) return;
            if (Module.IsAnyOtherWorkerBusy(typeof(PriceAdjustWorker))) return;

            var count = GetValidRetainerCount(x => x is { Available: true, MarketItemCount: > 0 }, out var validRetainers);
            if (count == 0) return;

            validRetainers
                .ForEach
                (index =>
                    {
                        taskHelper.Enqueue
                        (
                            () =>
                            {
                                if (taskHelper.AbortByConflictKey(Module)) return true;
                                return Module.EnterRetainer(index);
                            },
                            $"选择进入 {index} 号雇员"
                        );
                        taskHelper.Enqueue
                        (
                            () =>
                            {
                                if (taskHelper.AbortByConflictKey(Module)) return true;
                                return SelectString->IsAddonAndNodesReady() && RetainerManager.Instance()->GetActiveRetainer() != null;
                            },
                            $"等待接收 {index} 号雇员的数据"
                        );
                        taskHelper.Enqueue
                        (
                            () =>
                            {
                                if (taskHelper.AbortByConflictKey(Module)) return true;
                                return AddonSelectStringEvent.Select(SellInventoryItemsText);
                            },
                            "点击进入出售玩家所持物品列表"
                        );
                        taskHelper.Enqueue
                        (
                            () =>
                            {
                                if (taskHelper.AbortByConflictKey(Module)) return;
                                EnqueuePriceAdjustRetainer();
                            },
                            "由单一雇员商品改价接管后续逻辑"
                        );
                        taskHelper.Enqueue
                        (
                            () =>
                            {
                                if (taskHelper.AbortByConflictKey(Module)) return;
                                if (!RetainerSellList->IsAddonAndNodesReady()) return;
                                RetainerSellList->Callback(-1);
                            },
                            "单一雇员改价完成, 退出出售品列表界面"
                        );
                        taskHelper.Enqueue
                        (
                            () =>
                            {
                                if (taskHelper.AbortByConflictKey(Module)) return true;
                                return LeaveRetainer();
                            },
                            "单一雇员改价完成, 返回至雇员列表界面"
                        );
                    }
                );
        }

        private void EnqueuePriceAdjustRetainer()
        {
            if (taskHelper.AbortByConflictKey(Module)) return;
            if (Module.IsAnyOtherWorkerBusy(typeof(PriceAdjustWorker))) return;

            var retainer = RetainerManager.Instance()->GetActiveRetainer();
            if (retainer == null || retainer->MarketItemCount <= 0) return;

            var container = InventoryManager.Instance()->GetInventoryContainer(InventoryType.RetainerMarket);
            if (container == null || !container->IsLoaded) return;

            for (ushort i = 0; i < container->Size; i++)
                EnqueuePriceAdjustSlot(i);
        }

        private void EnqueuePriceAdjustSlot
        (
            ushort slotIndex,
            uint   forcePrice = 0
        )
        {
            if (taskHelper.AbortByConflictKey(Module)) return;
            if (Module.IsAnyOtherWorkerBusy(typeof(PriceAdjustWorker))) return;

            taskHelper.Enqueue
            (
                () =>
                {
                    var retainer = RetainerManager.Instance()->GetActiveRetainer();
                    if (retainer == null) return;

                    var container = InventoryManager.Instance()->GetInventoryContainer(InventoryType.RetainerMarket);
                    if (container == null || !container->IsLoaded) return;

                    var slot   = container->GetInventorySlot(slotIndex);
                    var itemID = slot->ItemId;
                    if (slot == null || slot->ItemId == 0) return;

                    var itemName      = LuminaGetter.GetRow<Item>(itemID)?.Name ?? string.Empty;
                    var isItemHQ      = slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                    var isPriceCached = PriceCacheManager.TryGetPriceCache(itemID, isItemHQ, out var price);

                    if (!isPriceCached)
                    {
                        var isNothingSearched = InfoProxyItemSearch.Instance()->SearchItemId == 0;

                        taskHelper.Enqueue
                        (
                            () =>
                            {
                                if (taskHelper.AbortByConflictKey(Module)) return;
                                RequestMarketItemData(itemID, false);
                            },
                            $"请求雇员 {retainer->NameString} {slotIndex} 号位置处 {itemName} 的市场价格数据",
                            weight: 2
                        );
                        if (isNothingSearched)
                            taskHelper.DelayNext(1000, "初始无数据, 等待 1 秒", 2);
                        taskHelper.Enqueue
                        (
                            () =>
                            {
                                if (taskHelper.AbortByConflictKey(Module)) return true;

                                return IsMarketItemDataReady(itemID);
                            },
                            $"等待 {itemName} 市场价格数据完全到达",
                            weight: 2
                        );
                        taskHelper.Enqueue
                        (
                            () =>
                            {
                                if (taskHelper.AbortByConflictKey(Module)) return;
                                // 什么价格数据都没有, 设置为 0
                                if (!PriceCacheManager.TryGetPriceCache(itemID, isItemHQ, out price))
                                    price = 0;

                                EnqueuePriceAdjustSingleItem(slotIndex, price, forcePrice);
                            },
                            "由单一物品改价接管后续逻辑",
                            weight: 2
                        );
                        return;
                    }

                    taskHelper.Enqueue(() => EnqueuePriceAdjustSingleItem(slotIndex, price, forcePrice), "由单一物品改价接管后续逻辑", weight: 2);
                },
                $"检查当前市场第 {slotIndex} 栏的物品数据, 强制价格: {forcePrice}",
                weight: 1
            );
        }

        private void EnqueuePriceAdjustSingleItem
        (
            ushort slot,
            uint   marketPrice,
            uint   forcePrice = 0
        )
        {
            if (taskHelper.AbortByConflictKey(Module)) return;
            if (Module.IsAnyOtherWorkerBusy(typeof(PriceAdjustWorker))) return;

            var itemMarketData = GetRetainerMarketItem(slot);
            if (itemMarketData == null) return;

            var itemConfig = GetItemConfigByItemKey(itemMarketData.Value.Item);
            var modifiedPrice = forcePrice > 0 ?
                                    forcePrice :
                                    GetModifiedPrice(itemConfig, marketPrice);

            // 价格为 0
            if (modifiedPrice == 0) return;

            // 价格不变
            if (modifiedPrice == itemMarketData.Value.Price) return;

            if (IsAnyAbortConditionsMet
                (
                    itemConfig,
                    itemMarketData.Value.Price,
                    modifiedPrice,
                    marketPrice,
                    out var abortCondition,
                    out var abortBehavior
                ))
            {
                NotifyAbortCondition(itemMarketData.Value.Item.ItemID, itemMarketData.Value.Item.IsHQ, abortCondition);
                EnqueueAbortBehavior(abortBehavior);
                return;
            }

            SetRetainerMarketItemPrice(slot, modifiedPrice);
            NotifyPriceAdjustSuccessfully
            (
                itemMarketData.Value.Item.ItemID,
                itemMarketData.Value.Item.IsHQ,
                itemMarketData.Value.Price,
                modifiedPrice
            );
            return;

            // 采取意外情况逻辑
            void EnqueueAbortBehavior
            (
                AbortBehavior behavior
            )
            {
                if (Module.config.SendPriceAdjustProcessMessage)
                {
                    var message = Lang.GetSe
                    (
                        "AutoRetainerWork-PriceAdjust-ConductAbortBehavior",
                        new SeStringBuilder().AddUiForeground(AbortBehaviorLoc.GetValueOrDefault(behavior), 67).Build()
                    );
                    NotifyHelper.Instance().Chat(message);
                }

                if (behavior == AbortBehavior.无) return;

                switch (behavior)
                {
                    case AbortBehavior.改价至最小值:
                        SetRetainerMarketItemPrice(slot, (uint)itemConfig.PriceMinimum);
                        NotifyPriceAdjustSuccessfully
                        (
                            itemMarketData.Value.Item.ItemID,
                            itemMarketData.Value.Item.IsHQ,
                            itemMarketData.Value.Price,
                            (uint)itemConfig.PriceMinimum
                        );
                        break;
                    case AbortBehavior.改价至预期值:
                        SetRetainerMarketItemPrice(slot, (uint)itemConfig.PriceExpected);
                        NotifyPriceAdjustSuccessfully
                        (
                            itemMarketData.Value.Item.ItemID,
                            itemMarketData.Value.Item.IsHQ,
                            itemMarketData.Value.Price,
                            (uint)itemConfig.PriceExpected
                        );
                        break;
                    case AbortBehavior.改价至最高值:
                        SetRetainerMarketItemPrice(slot, (uint)itemConfig.PriceMaximum);
                        NotifyPriceAdjustSuccessfully
                        (
                            itemMarketData.Value.Item.ItemID,
                            itemMarketData.Value.Item.IsHQ,
                            itemMarketData.Value.Price,
                            (uint)itemConfig.PriceMaximum
                        );
                        break;
                    case AbortBehavior.收回至雇员:
                        ReturnRetainerMarketItemToInventory(slot, false);
                        break;
                    case AbortBehavior.收回至背包:
                        ReturnRetainerMarketItemToInventory(slot, true);
                        break;
                    case AbortBehavior.出售至系统商店:
                        taskHelper.Enqueue(() => ReturnRetainerMarketItemToInventory(slot, true), "将物品收回背包, 以待出售", weight: 3);
                        taskHelper.Enqueue
                        (
                            () =>
                            {
                                if (!TrySearchItemInInventory(itemMarketData.Value.Item.ItemID, itemMarketData.Value.Item.IsHQ, out var foundItems) ||
                                    foundItems is not { Count: > 0 })
                                    return false;

                                var foundItem = foundItems.FirstOrDefault();
                                return foundItem.OpenContext();
                            },
                            "找到物品并打开其右键菜单",
                            weight: 3
                        );
                        taskHelper.Enqueue(() => ContextMenuAddon->IsAddonAndNodesReady(),                       "等待右键菜单出现",  weight: 3);
                        taskHelper.Enqueue(() => AddonContextMenuEvent.Select(LuminaWrapper.GetAddonText(5480)), "出售物品至系统商店", weight: 3);
                        break;
                }
            }
        }

        private ItemConfig GetItemConfigByItemKey
        (
            ItemKey key
        ) =>
            Module.config.ItemConfigs.TryGetValue(key.ToString(), out var itemConfig) ?
                itemConfig :
                Module.config.ItemConfigs[new ItemKey(0, key.IsHQ).ToString()];

        #endregion

        #region 操作

        /// <summary>
        ///     将当前雇员市场售卖物品收回背包/雇员
        /// </summary>
        /// <param name="slot"></param>
        /// <param name="isInventory">若为 True 则为收回背包, 否则则为收回雇员背包</param>
        private bool ReturnRetainerMarketItemToInventory
        (
            ushort slot,
            bool   isInventory
        )
        {
            if (!Module.retainerThrottler.Throttle("ReturnMarketItemToInventory", 100)) return false;

            var manager = InventoryManager.Instance();
            if (manager == null) return false;

            var container = manager->GetInventoryContainer(InventoryType.RetainerMarket);
            if (container == null || !container->IsLoaded) return false;

            var inventoryItem = container->GetInventorySlot(slot);
            if (inventoryItem == null || inventoryItem->ItemId == 0) return true;

            if (isInventory)
                InventoryManager.Instance()->MoveFromRetainerMarketToPlayerInventory(InventoryType.RetainerMarket, slot, (uint)inventoryItem->Quantity);
            else
                InventoryManager.Instance()->MoveFromRetainerMarketToRetainerInventory(InventoryType.RetainerMarket, slot, (uint)inventoryItem->Quantity);
            return false;
        }

        /// <summary>
        ///     设定当前雇员市场售卖物品价格
        /// </summary>
        private static bool SetRetainerMarketItemPrice
        (
            ushort slot,
            uint   price
        )
        {
            if (slot >= 20) return false;

            var manager = InventoryManager.Instance();
            if (manager == null) return false;

            manager->SetRetainerMarketPrice((short)slot, price);
            RaptureAtkModule.Instance()->AgentUpdateFlag |= RaptureAtkModule.AgentUpdateFlags.RetainerMarketInventoryUpdate;
            return true;
        }

        /// <summary>
        ///     获取当前雇员市场售卖物品数据
        /// </summary>
        private static (ItemKey Item, uint Price)? GetRetainerMarketItem
        (
            ushort slot
        )
        {
            if (slot >= 20) return null;

            var manager = InventoryManager.Instance();
            if (manager == null) return null;

            var container = manager->GetInventoryContainer(InventoryType.RetainerMarket);
            if (container == null || !container->IsLoaded) return null;

            var slotData = container->GetInventorySlot(slot);
            if (slotData == null) return null;

            var item = new ItemKey(slotData->ItemId, slotData->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality));
            return (item, GetRetainerMarketPrice(slot));
        }

        /// <summary>
        ///     获取当前雇员市场售卖物品价格
        /// </summary>
        private static uint GetRetainerMarketPrice
        (
            ushort slot
        )
        {
            if (slot >= 20) return 0;

            var manager = InventoryManager.Instance();
            if (manager == null) return 0;

            return (uint)manager->GetRetainerMarketPrice((short)slot);
        }

        /// <summary>
        ///     获取当前市场物品数据
        /// </summary>
        private static void RequestMarketItemData
        (
            uint itemID,
            bool openOverlay
        )
        {
            if (InfoProxyItemSearch.Instance()->SearchItemId != itemID)
                SearchItemIPC.InvokeFunc(itemID);
            if (openOverlay)
                ToggleOverlayIPC.InvokeFunc(true);
        }

        /// <summary>
        ///     当前市场物品数据是否已就绪
        /// </summary>
        private static bool IsMarketItemDataReady
        (
            uint itemID
        )
        {
            var proxy = InfoProxyItemSearch.Instance();
            if (proxy == null) return false;

            return proxy->IsFullyReceived(itemID);
        }

        /// <summary>
        ///     是否满足任何意外情况
        /// </summary>
        /// <returns>正常/不需要修改价格为 False</returns>
        private static bool IsAnyAbortConditionsMet
        (
            ItemConfig         config,
            uint               origPrice,
            uint               modifiedPrice,
            uint               marketPrice,
            out AbortCondition conditionMet,
            out AbortBehavior  behaviorNeeded
        )
        {
            conditionMet   = AbortCondition.无;
            behaviorNeeded = AbortBehavior.无;

            // 检查每个条件
            foreach (var condition in PriceCheckConditions.GetAll())
            {
                var hasBehavior = false;

                foreach (var logic in config.AbortLogic)
                {
                    if ((logic.Key & condition.Condition) != condition.Condition) continue;

                    behaviorNeeded = logic.Value;
                    hasBehavior    = true;
                    break;
                }

                if (!hasBehavior || !condition.Predicate(config, origPrice, modifiedPrice, marketPrice)) continue;

                conditionMet = condition.Condition;
                return true;
            }

            return false;
        }

        /// <summary>
        ///     获取修改后价格结果
        /// </summary>
        private static uint GetModifiedPrice
        (
            ItemConfig config,
            uint       marketPrice
        ) =>
            (uint)(config.AdjustBehavior switch
                      {
                          AdjustBehavior.固定值 => Math.Max
                          (
                              0,
                              marketPrice - config.AdjustValues[AdjustBehavior.固定值]
                          ),
                          AdjustBehavior.百分比 => Math.Max
                          (
                              0,
                              marketPrice * (1 - (config.AdjustValues[AdjustBehavior.百分比] / 100))
                          ),
                          _ => marketPrice
                      });

        /// <summary>
        ///     发送改价成功通知信息
        /// </summary>
        private void NotifyPriceAdjustSuccessfully
        (
            uint itemID,
            bool isHQ,
            uint origPrice,
            uint modifiedPrice
        )
        {
            if (!Module.config.SendPriceAdjustProcessMessage) return;

            var itemPayload = new SeStringBuilder().AddItemLink(itemID, isHQ).Build();

            var priceChangedValue = (long)modifiedPrice - origPrice;

            var priceChangeText = priceChangedValue.ToChineseString();
            if (!priceChangeText.StartsWith('-'))
                priceChangeText = $"+{priceChangeText}";

            var priceChangeRate = origPrice == 0 ?
                                      0 :
                                      (double)priceChangedValue / origPrice * 100;
            var priceChangeRateText = priceChangeRate.ToString("+0.##;-0.##") + "%";

            NotifyHelper.Instance().Chat
            (
                Lang.GetSe
                (
                    "AutoRetainerWork-PriceAdjust-PriceAdjustSuccessfully",
                    itemPayload,
                    RetainerManager.Instance()->GetActiveRetainer()->NameString,
                    origPrice.ToChineseString(),
                    modifiedPrice.ToChineseString(),
                    priceChangeText,
                    priceChangeRateText
                )
            );
        }

        /// <summary>
        ///     发送意外情况检测通知信息
        /// </summary>
        private void NotifyAbortCondition
        (
            uint           itemID,
            bool           isHQ,
            AbortCondition condition
        )
        {
            if (!Module.config.SendPriceAdjustProcessMessage) return;

            var itemPayload = new SeStringBuilder().AddItemLink(itemID, isHQ).Build();
            NotifyHelper.Instance().Chat
            (
                Lang.GetSe
                (
                    "AutoRetainerWork-PriceAdjust-DetectAbortCondition",
                    itemPayload,
                    RetainerManager.Instance()->GetActiveRetainer()->NameString,
                    new SeStringBuilder().AddUiForeground(GetAbortConditionName(condition), 60).Build()
                )
            );
        }

        /// <summary>
        ///     获取当前雇员市场为同一物品的全部槽位
        /// </summary>
        private static bool TryGetSameItemSlots
        (
            uint             itemID,
            out List<ushort> slots
        )
        {
            slots = [];

            var manager = InventoryManager.Instance();
            if (manager == null) return false;

            var container = manager->GetInventoryContainer(InventoryType.RetainerMarket);
            if (container == null || !container->IsLoaded) return false;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId != itemID) continue;

                slots.Add((ushort)i);
            }

            return slots.Count > 0;
        }

        #endregion

        private class PriceAdjustContextMenuEntry
        (
            PriceAdjustWorker worker
        ) : ContextMenuEntry
        {
            public override string Identifier => nameof(AutoRetainerWork);

            public override IReadOnlyList<ContextMenuItem>? CreateMultiple
            (
                ContextMenuOpenedArgs args
            )
            {
                if (args.AddonName != "RetainerSellList")
                    return null;

                var agent = AgentRetainer.Instance();
                if (agent->ContextMenuIndex   < 0  ||
                    agent->SellListEntryCount == 0 ||
                    agent->ContextMenuIndex   >= agent->SellListEntryCount)
                    return null;

                var manager = InventoryManager.Instance();

                var selectedSellListEntry = agent->SellListEntries[agent->ContextMenuIndex];
                var inventoryItem = manager->GetInventorySlot
                (
                    InventoryType.RetainerMarket,
                    selectedSellListEntry.InventorySlot
                );
                if (inventoryItem == null)
                    return null;

                var itemID = inventoryItem->GetBaseItemId();
                if (!LuminaGetter.TryGetRow(itemID, out Item _))
                    return null;

                return
                [
                    // 自动修改价格
                    new()
                    {
                        Name      = Lang.Get("AutoRetainerWork-PriceAdjust-AutoAdjustPrice"),
                        OnClicked = _ => worker.EnqueuePriceAdjustSlot(inventoryItem->GetSlot())
                    },
                    // 修改同类物品价格
                    new()
                    {
                        Name = Lang.Get("AutoRetainerWork-PriceAdjust-ManualAdjustPrice-AllSame"),
                        OnClicked = _ =>
                        {
                            worker.isPriceAdjustAllSameItems = true;
                            AgentRetainer.Instance()->OpenRetainerSell(inventoryItem->GetInventoryType(), inventoryItem->GetSlot());
                        }
                    },
                    // 自动修改同类物品价格
                    new()
                    {
                        Name = Lang.Get("AutoRetainerWork-PriceAdjust-AutoAdjustPrice-AllSame"),
                        OnClicked = _ =>
                        {
                            if (TryGetSameItemSlots(itemID, out var slots))
                            {
                                foreach (var slot in slots)
                                    worker.EnqueuePriceAdjustSlot(slot);
                            }
                        }
                    }
                ];
            }
        }

        private class PriceAdjustAddon
        (
            PriceAdjustWorker worker
        ) : AttachedAddon("RetainerSellList")
        {
            protected override bool CanOpenAddon => 
                ICondition.Instance()[ConditionFlag.OccupiedSummoningBell];

            public TextButtonNode? PriceAdjustButton         { get; private set; }
            public CheckboxNode?   AutoAdjustPriceCheckbox   { get; private set; }
            public CheckboxNode?   NotifyPriceAdjustCheckbox { get; private set; }

            protected override void OnSetup
            (
                AtkUnitBase*   addon,
                Span<AtkValue> atkValues
            )
            {
                var rootContainer = new VerticalListNode
                {
                    ItemSpacing = 5f,
                    Position    = ContentStartPosition,
                    Width       = ContentSize.X,
                    FitContents = true
                };
                rootContainer.AttachNode(this);

                PriceAdjustButton = new()
                {
                    String      = Lang.Get("AutoRetainerWork-PriceAdjust-AutoAdjustPrice-Batch"),
                    Size        = new(rootContainer.Width, 36),
                    TextureType = ButtonTextureType.ButtonB,
                    OnClick = () =>
                    {
                        if (worker.taskHelper.IsBusy)
                            worker.taskHelper.Abort();
                        else
                            worker.EnqueuePriceAdjustRetainer();
                    }
                };
                rootContainer.AddNode(PriceAdjustButton);
                
                var returnToInventory = new TextButtonNode
                {
                    String      = Lang.Get("AutoRetainerWork-PriceAdjust-ReturnAllToInventory"),
                    Size        = new(rootContainer.Width, 28),
                    OnClick = () =>
                    {
                        var container = InventoryManager.Instance()->GetInventoryContainer(InventoryType.RetainerMarket);
                        for (var i = 0; i < container->Size; i++)
                        {
                            var index = i;
                            worker.taskHelper.Enqueue
                            (
                                () => worker.ReturnRetainerMarketItemToInventory((ushort)index, true),
                                $"将市场中的第{index}栏物品收回至自己"
                            );
                        }
                    }
                };
                rootContainer.AddNode(returnToInventory);
                
                var returnToRetainer = new TextButtonNode
                {
                    String = Lang.Get("AutoRetainerWork-PriceAdjust-ReturnAllToRetainer"),
                    Size   = new(rootContainer.Width, 28),
                    OnClick = () =>
                    {
                        var container = InventoryManager.Instance()->GetInventoryContainer(InventoryType.RetainerMarket);
                        for (var i = 0; i < container->Size; i++)
                        {
                            var index = i;
                            worker.taskHelper.Enqueue
                            (
                                () => worker.ReturnRetainerMarketItemToInventory((ushort)index, false),
                                $"将市场中的第{index}栏物品收回至雇员"
                            );
                        }
                    }
                };
                rootContainer.AddNode(returnToRetainer);
                
                var clearPriceCace = new TextButtonNode
                {
                    String = Lang.Get("AutoRetainerWork-PriceAdjust-ClearCache"),
                    Size   = new(rootContainer.Width, 28),
                    OnClick = () =>
                    {
                        PriceCacheManager.ClearCache();
                        NotifyHelper.Toast(Lang.Get("AutoRetainerWork-PriceAdjust-CacheCleared"));
                    }
                };
                rootContainer.AddNode(clearPriceCace);
                
                rootContainer.AddDummy(2f);

                AutoAdjustPriceCheckbox = new()
                {
                    String    = Lang.Get("AutoRetainerWork-PriceAdjust-AutoAdjustWhenNewOnSale"),
                    Size      = new(rootContainer.Width, 28),
                    IsChecked = worker.Module.config.AutoPriceAdjustWhenNewOnSale,
                    OnClick = value =>
                    {
                        worker.Module.config.AutoPriceAdjustWhenNewOnSale = value;
                        worker.Module.config.Save(worker.Module);
                    }
                };
                rootContainer.AddNode(AutoAdjustPriceCheckbox);
                
                NotifyPriceAdjustCheckbox = new()
                {
                    String    = Lang.Get("AutoRetainerWork-PriceAdjust-SendProcessMessage"),
                    Size      = new(rootContainer.Width, 28),
                    IsChecked = worker.Module.config.SendPriceAdjustProcessMessage,
                    OnClick = value =>
                    {
                        worker.Module.config.SendPriceAdjustProcessMessage = value;
                        worker.Module.config.Save(worker.Module);
                    }
                };
                rootContainer.AddNode(NotifyPriceAdjustCheckbox);
                
                rootContainer.RecalculateLayout();
                SetWindowSize(Size.X, ContentStartPosition.Y + rootContainer.Height + 20f);
                rootContainer.Position = ContentStartPosition;
            }

            protected override void OnAttachedAddonUpdate
            (
                AtkUnitBase* addon,
                AtkUnitBase* hostAddon
            ) =>
                PriceAdjustButton?.String = worker.taskHelper?.IsBusy ?? false ?
                                                Lang.Get("Stop") :
                                                Lang.Get("AutoRetainerWork-PriceAdjust-AutoAdjustPrice-Batch");
        }

        public static class PriceCacheManager
        {
            private const           int        CACHE_EXPIRATION_MINUTES = 10;
            private static readonly PriceCache CurrentPriceCache        = new();
            private static readonly PriceCache HistoryPriceCache        = new();

            public static void UpdateCache<T>
            (
                AutoRetainerWork module,
                PriceCache       cache,
                uint             itemID,
                IEnumerable<T>   listings,
                Func<T, bool>    isHQSelector,
                Func<T, bool>    onMannequinSelector,
                Func<T, uint>    priceSelector,
                Func<T, ulong>   retainerSelector = null
            )
            {
                var filteredListings = listings
                                       .Where(x => !onMannequinSelector(x))
                                       .ToLookup(isHQSelector);

                foreach (var isHQ in new[] { false, true })
                {
                    var items = filteredListings[isHQ];
                    if (retainerSelector != null)
                        items = items.Where(x => !module.playerRetainers.Contains(retainerSelector(x)));

                    var enumerable = items as T[] ?? [.. items];
                    var minPrice = enumerable.Length != 0 ?
                                       enumerable.Min(priceSelector) :
                                       0;
                    if (minPrice <= 0) continue;

                    var cacheKey = CacheKeys.Create(itemID, isHQ);
                    if (!cache.TryGetPrice(cacheKey, out var currentPrice) || minPrice < currentPrice)
                        cache.SetPrice(cacheKey, minPrice);
                }
            }

            public static void UpdateHistoryCache<T>
            (
                PriceCache     cache,
                uint           itemID,
                IEnumerable<T> listings,
                Func<T, bool>  isHQSelector,
                Func<T, bool>  onMannequinSelector,
                Func<T, uint>  priceSelector
            )
            {
                var filteredListings = listings
                                       .Where(x => !onMannequinSelector(x))
                                       .ToLookup(isHQSelector);

                foreach (var isHQ in new[] { false, true })
                {
                    var items      = filteredListings[isHQ];
                    var enumerable = items as T[] ?? items.ToArray();
                    var maxPrice = enumerable.Length != 0 ?
                                       enumerable.Max(priceSelector) :
                                       0;
                    if (maxPrice <= 0) continue;

                    var cacheKey = CacheKeys.Create(itemID, isHQ);
                    if (!cache.TryGetPrice(cacheKey, out var currentPrice) || maxPrice > currentPrice)
                        cache.SetPrice(cacheKey, maxPrice);
                }
            }

            public static void OnOfferingReceived
            (
                AutoRetainerWork             module,
                IMarketBoardCurrentOfferings data
            )
            {
                if (!data.ItemListings.Any()) return;
                UpdateCache
                (
                    module,
                    CurrentPriceCache,
                    data.ItemListings[0].ItemId,
                    data.ItemListings,
                    x => x.IsHq,
                    x => x.OnMannequin,
                    x => x.PricePerUnit,
                    x => x.RetainerId
                );
            }

            public static void OnHistoryReceived
            (
                IMarketBoardHistory history
            )
            {
                if (!history.HistoryListings.Any()) return;
                UpdateHistoryCache
                (
                    HistoryPriceCache,
                    history.ItemId,
                    history.HistoryListings,
                    x => x.IsHq,
                    x => x.OnMannequin,
                    x => x.SalePrice
                );
            }

            public static bool TryGetPriceCache
            (
                uint     itemID,
                bool     isHQ,
                out uint price
            )
            {
                price = 0;
                var cacheKey         = CacheKeys.Create(itemID, isHQ);
                var oppositeCacheKey = CacheKeys.Create(itemID, !isHQ);

                // 清理过期缓存
                CurrentPriceCache.RemoveExpiredEntries(TimeSpan.FromMinutes(CACHE_EXPIRATION_MINUTES));
                HistoryPriceCache.RemoveExpiredEntries(TimeSpan.FromMinutes(CACHE_EXPIRATION_MINUTES));

                // 按优先级尝试获取价格
                return (CurrentPriceCache.TryGetPrice(cacheKey,         out price) ||
                        CurrentPriceCache.TryGetPrice(oppositeCacheKey, out price) ||
                        HistoryPriceCache.TryGetPrice(cacheKey,         out price) ||
                        HistoryPriceCache.TryGetPrice(oppositeCacheKey, out price)) &&
                       price != 0;
            }

            public static (DateTime Current, DateTime History) GetCacheTimes() =>
                (CurrentPriceCache.LastUpdateTime, HistoryPriceCache.LastUpdateTime);

            public static void ClearCache
            (
                bool clearCurrent = true,
                bool clearHistory = true
            )
            {
                if (clearCurrent)
                    CurrentPriceCache.Clear();
                if (clearHistory)
                    HistoryPriceCache.Clear();
            }

            private static class CacheKeys
            {
                public static string Create
                (
                    uint itemID,
                    bool isHQ
                ) => $"{itemID}_{(isHQ ? "HQ" : "NQ")}";
            }
        }

        public sealed class PriceCache
        {
            private readonly Dictionary<string, CacheEntry> data = [];

            public DateTime LastUpdateTime { get; private set; } = DateTime.MinValue;

            public void RemoveExpiredEntries
            (
                TimeSpan expirationTime
            )
            {
                var now = StandardTimeManager.Instance().Now;
                var expiredKeys = data
                                  .Where(kvp => now - kvp.Value.LastUpdateTime > expirationTime)
                                  .Select(kvp => kvp.Key)
                                  .ToList();

                foreach (var key in expiredKeys)
                    data.Remove(key);

                if (!data.Any())
                    LastUpdateTime = DateTime.MinValue;
            }

            public bool TryGetPrice
            (
                string   key,
                out uint price
            )
            {
                price = 0;

                if (data.TryGetValue(key, out var entry))
                {
                    price = entry.Price;
                    return true;
                }

                return false;
            }

            public void SetPrice
            (
                string key,
                uint   price
            )
            {
                data[key] = new CacheEntry
                {
                    Price          = price,
                    LastUpdateTime = StandardTimeManager.Instance().Now
                };
                LastUpdateTime = StandardTimeManager.Instance().Now;
            }

            public void Clear()
            {
                data.Clear();
                LastUpdateTime = DateTime.MinValue;
            }

            private class CacheEntry
            {
                public uint     Price          { get; init; }
                public DateTime LastUpdateTime { get; init; }
            }
        }

        #region 常量

        private static readonly string[] SellInventoryItemsText =
        [
            "玩家所持物品",
            "Sell items in your inventory",
            "プレイヤー所持品から",
            "플레이어 소지품에서 선택",
            "Gegenstände aus dem eigenen Inventar verkaufen",
            "Mettre en vente un objet de votre inventaire"
        ];

        #endregion
    }
}
