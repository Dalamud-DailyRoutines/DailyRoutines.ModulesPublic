using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Network.Structures;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using Action = System.Action;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class AutoRetainerWork
{
    public class PriceAdjustWorker : RetainerWorkerBase
    {
        public override bool DrawOverlayCondition(string activeAddonName) => 
            activeAddonName is "RetainerList";

        public override bool IsWorkerBusy() => TaskHelper?.IsBusy ?? false;

        public override string RunningMessage() => TaskHelper?.CurrentTaskName ?? string.Empty;

        private delegate int MoveFromRetainerMarketToInventoryDelegate(InventoryManager* manager, InventoryType sourceType, ushort sourceSlot, uint quantity);

        private static readonly CompSig MoveFromRetainerMarketToPlayerInventorySig =
            new("E8 ?? ?? ?? ?? EB 49 84 C0");
        private static readonly CompSig MoveFromRetainerMarketToRetainerInventorySig =
            new("E8 ?? ?? ?? ?? B0 ?? C7 83 ?? ?? ?? ?? ?? ?? ?? ?? 48 83 C4 ?? 5B C3 E8");

        private static MoveFromRetainerMarketToInventoryDelegate? MoveFromRetainerMarketToPlayerInventory;
        private static MoveFromRetainerMarketToInventoryDelegate? MoveFromRetainerMarketToRetainerInventory;

        private static readonly CompSig MoveToRetainerMarketSig = new("44 89 4C 24 ?? 66 44 89 44 24 ?? 53 56 57");
        private delegate void MoveToRetainerMarketDelegate(
            InventoryManager* manager,
            InventoryType     srcInv,
            ushort            srcSlot,
            InventoryType     dstInv,
            ushort            dstSlot,
            uint              quantity,
            uint              unitPrice);
        private static Hook<MoveToRetainerMarketDelegate>? MoveToRetainerMarketHook;

        private static readonly string[] SellInventoryItemsText =
            ["玩家所持物品", "Sell items in your inventory", "プレイヤー所持品から"];

        private static  TaskHelper?             TaskHelper;
        private static  LuminaSearcher<Item>?   ItemSearcher;

        private static          ItemConfig?    SelectedItemConfig;
        private static readonly Vector2        ChildSizeLeft     = ScaledVector2(200, 400);
        private static          Vector2        ChildSizeRight    = ScaledVector2(450, 400);
        private static          string         PresetSearchInput = string.Empty;
        private static          string         ItemSearchInput   = string.Empty;
        private static          uint           NewConfigItemID;
        private static          bool           NewConfigItemHQ;
        private static          AbortCondition ConditionInput = AbortCondition.低于最小值;
        private static          AbortBehavior  BehaviorInput  = AbortBehavior.无;
        private static          uint           ItemModifyUnitPriceManual;
        private static          uint           ItemModifyCountManual;
        private static          Vector2        MarketDataTableImageSize = new(32);
        private static          Vector2        ManualUnitPriceImageSize = new(32);

        private static KeyValuePair<uint, List<IMarketBoardHistoryListing>> HistoryListings;

        private static bool          IsNeedToDrawMarketListWindow;
        private static bool          IsNeedToDrawMarketUpshelfWindow;
        private static InventoryType SourceUpshelfType;
        private static ushort        SourceUpshelfSlot;
        private static uint          UpshelfUnitPriceInput;
        private static uint          UpshelfQuantityInput;
        
        public override void Init()
        {
            MoveFromRetainerMarketToPlayerInventory   ??= MoveFromRetainerMarketToPlayerInventorySig.GetDelegate<MoveFromRetainerMarketToInventoryDelegate>();
            MoveFromRetainerMarketToRetainerInventory ??= MoveFromRetainerMarketToRetainerInventorySig.GetDelegate<MoveFromRetainerMarketToInventoryDelegate>();

            MoveToRetainerMarketHook ??= MoveToRetainerMarketSig.GetHook<MoveToRetainerMarketDelegate>(MoveToRetainerMarketDetour);
            MoveToRetainerMarketHook.Enable();

            ItemSearcher ??= new(LuminaGetter.Get<Item>(), [x => x.Name.ExtractText(), x => x.RowId.ToString()], x => x.Name.ExtractText());

            TaskHelper ??= new() { TimeLimitMS = 30_000 };

            DService.MarketBoard.HistoryReceived   += OnHistoryReceived;
            DService.MarketBoard.OfferingsReceived += OnOfferingReceived;

            DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "RetainerSell",     OnRetainerSell);
            DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "RetainerSellList", OnRetainerSellList);
            DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "RetainerSellList", OnRetainerSellList);

            WindowManager.Draw += DrawMarketListWindow;
            WindowManager.Draw += DrawUpshelfWindow;
        }

        public override void DrawConfig()
        {
            ImGui.TextColored(KnownColor.RoyalBlue.ToVector4(), GetLoc("AutoRetainerWork-PriceAdjust-Title"));

            ItemConfigSelector();

            ImGui.SameLine();
            ItemConfigEditor();
        }

        public override void DrawOverlay(string activeAddonName)
        {
            using var node = ImRaii.TreeNode(GetLoc("AutoRetainerWork-PriceAdjust-Title"));
            if (!node) return;
            if (activeAddonName != "RetainerList") return;
            
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("AutoRetainerWork-PriceAdjust-AdjustForRetainers"));
            ImGui.Spacing();

            using (ImRaii.Disabled(TaskHelper.IsBusy))
            {
                if (ImGui.Button(GetLoc("Start")))
                    EnqueuePriceAdjustAll();
            }

            ImGui.SameLine();
            if (ImGui.Button(GetLoc("Stop")))
                TaskHelper.Abort();

            ScaledDummy(6f);

            if (ImGui.Checkbox(GetLoc("AutoRetainerWork-PriceAdjust-SendProcessMessage"), ref ModuleConfig.SendPriceAdjustProcessMessage))
                ModuleConfig.Save(Module);

            if (ImGui.Checkbox(GetLoc("AutoRetainerWork-PriceAdjust-AutoAdjustWhenNewOnSale"), ref ModuleConfig.AutoPriceAdjustWhenNewOnSale))
                ModuleConfig.Save(Module);
        }

        private static void DrawMarketListWindow()
        {
            if (!IsNeedToDrawMarketListWindow) return;
            if (!IsAddonAndNodesReady(RetainerSellList))
            {
                IsNeedToDrawMarketListWindow = false;
                return;
            }

            var addon = RetainerSellList;
            if (addon == null) return;

            var size = new Vector2(addon->GetScaledWidth(true), addon->GetScaledHeight(true));
            var windowPos = default(Vector2);

            ImGui.SetNextWindowSize(size);
            if (ImGui.Begin("改价窗口##AutoRetainerWork-PriceAdjustWorker",
                            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar |
                            ImGuiWindowFlags.MenuBar))
            {
                windowPos = ImGui.GetWindowPos();
                DrawMarketItemsTable();
                ImGui.End();
            }

            if (addon->X != (short)windowPos.X || addon->Y != (short)windowPos.Y)
                addon->SetPosition((short)windowPos.X, (short)windowPos.Y);

            var node = addon->GetComponentListById(11);
            if (node != null && node->OwnerNode->Alpha_2 != 255)
                node->OwnerNode->SetAlpha(0);

            if (InfoProxyItemSearch.Instance()->SearchItemId == 0) return;
            
            ImGui.SetNextWindowSizeConstraints(new(200, 300), new(float.MaxValue));
            if (ImGui.Begin("市场数据窗口##AutoRetainerWork-PriceAdjustWorker", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar))
            {
                DrawMarketDataTable();

                if (HistoryListings.Key != 0 && HistoryListings.Value.Count > 0)
                {
                    ImGui.NewLine();
                    
                    DrawMarketHistoryDataTable();
                }
                ImGui.End();
            }
        }

        private static void DrawUpshelfWindow()
        {
            if (!IsNeedToDrawMarketUpshelfWindow) return;
            if (!IsAddonAndNodesReady(RetainerSellList))
            {
                IsNeedToDrawMarketUpshelfWindow = false;
                return;
            }

            if (ImGui.Begin("上架窗口##AutoRetainerWork-PriceAdjustWorker", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
            {
                DrawMarketUpshelf();
                ImGui.End();
            }
        }

        #region 配置界面

        private static void ItemConfigSelector()
        {
            using var child = ImRaii.Child("ItemConfigSelectorChild", ChildSizeLeft, true);
            if (!child) return;

            if (ImGuiOm.ButtonIcon("AddNewConfig", FontAwesomeIcon.Plus, GetLoc("Add")))
                ImGui.OpenPopup("AddNewPreset");

            ImGui.SameLine();
            if (ImGuiOm.ButtonIcon("ImportConfig", FontAwesomeIcon.FileImport, GetLoc("ImportFromClipboard")))
            {
                var itemConfig = ImportFromClipboard<ItemConfig>();
                if (itemConfig != null)
                {
                    var itemKey = new ItemKey(itemConfig.ItemID, itemConfig.IsHQ).ToString();
                    ModuleConfig.ItemConfigs[itemKey] = itemConfig;
                }
            }

            using (var popup0 = ImRaii.Popup("AddNewPreset"))
            {
                if (popup0)
                {
                    AddNewConfigItemPopup(() =>
                    {
                        var newConfigStr = new ItemKey(NewConfigItemID, NewConfigItemHQ).ToString();
                        var newConfig = new ItemConfig(NewConfigItemID, NewConfigItemHQ);
                        if (ModuleConfig.ItemConfigs.TryAdd(newConfigStr, newConfig))
                        {
                            ModuleConfig.Save(Module);
                            ImGui.CloseCurrentPopup();
                        }
                    });
                }
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputTextWithHint("###PresetSearchInput", GetLoc("PleaseSearch"), ref PresetSearchInput, 100);

            ImGui.Separator();

            foreach (var itemConfig in ModuleConfig.ItemConfigs.ToList())
            {
                if (!string.IsNullOrWhiteSpace(PresetSearchInput) && !itemConfig.Value.ItemName.Contains(PresetSearchInput))
                    continue;

                if (ImGui.Selectable($"{itemConfig.Value.ItemName} {(itemConfig.Value.IsHQ ? "(HQ)" : "")}",
                                     itemConfig.Value == SelectedItemConfig))
                    SelectedItemConfig = itemConfig.Value;

                var isOpenPopup = false;
                using (var popup1 = ImRaii.ContextPopupItem($"{itemConfig.Value}_{itemConfig.Key}_{itemConfig.Value.ItemID}"))
                {
                    if (popup1)
                    {
                        if (ImGui.MenuItem(GetLoc("ExportToClipboard")))
                            ExportToClipboard(itemConfig.Value);

                        if (ImGui.MenuItem(GetLoc("AutoRetainerWork-PriceAdjust-CreateNewBaseOnExisted")))
                            isOpenPopup = true;

                        if (itemConfig.Value.ItemID != 0)
                        {
                            if (ImGui.MenuItem(GetLoc("Delete")))
                            {
                                ModuleConfig.ItemConfigs.Remove(itemConfig.Key);
                                ModuleConfig.Save(Module);

                                SelectedItemConfig = null;
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
                        AddNewConfigItemPopup(() =>
                        {
                            var newConfigStr = new ItemKey(NewConfigItemID, NewConfigItemHQ).ToString();
                            var newConfig = new ItemConfig
                            {
                                ItemID = NewConfigItemID,
                                IsHQ = NewConfigItemHQ,
                                ItemName = LuminaGetter.GetRow<Item>(NewConfigItemID)?.Name.ExtractText() ?? string.Empty,
                                AbortLogic = itemConfig.Value.AbortLogic,
                                AdjustBehavior = itemConfig.Value.AdjustBehavior,
                                AdjustValues = itemConfig.Value.AdjustValues,
                                PriceExpected = itemConfig.Value.PriceExpected,
                                PriceMaximum = itemConfig.Value.PriceMaximum,
                                PriceMaxReduction = itemConfig.Value.PriceMaxReduction,
                                PriceMinimum = itemConfig.Value.PriceMinimum
                            };

                            if (ModuleConfig.ItemConfigs.TryAdd(newConfigStr, newConfig))
                            {
                                ModuleConfig.Save(Module);
                                ImGui.CloseCurrentPopup();
                            }
                        });
                    }
                }

                if (itemConfig.Value is { ItemID: 0, IsHQ: true })
                    ImGui.Separator();
            }
        }

        private static void AddNewConfigItemPopup(Action confirmAction)
        {
            ImGui.SetNextItemWidth(150f * GlobalFontScale);
            if (ImGui.InputTextWithHint("###GameItemSearchInput", GetLoc("PleaseSearch"), ref ItemSearchInput, 128))
                ItemSearcher.Search(ItemSearchInput);

            ImGui.SameLine();
            ImGui.Checkbox("HQ", ref NewConfigItemHQ);

            ImGui.SameLine();
            if (ImGui.Button(GetLoc("Confirm")))
                confirmAction();

            if (string.IsNullOrWhiteSpace(ItemSearchInput)) return;

            ImGui.Separator();
            foreach (var item in ItemSearcher.SearchResult)
            {
                var itemIcon = DService.Texture.GetFromGameIcon(new(item.Icon, NewConfigItemHQ)).GetWrapOrDefault();
                if (itemIcon == null) continue;

                if (ImGuiOm.SelectableImageWithText(itemIcon.Handle, new(ImGui.GetTextLineHeightWithSpacing()),
                                                    item.Name.ExtractText(), item.RowId == NewConfigItemID,
                                                    ImGuiSelectableFlags.DontClosePopups))
                    NewConfigItemID = item.RowId;
            }
        }

        private static void ItemConfigEditor()
        {
            ChildSizeRight.X = ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X;
            using var child = ImRaii.Child("ItemConfigEditorChild", ChildSizeRight, true);

            if (SelectedItemConfig == null) return;

            // 基本信息获取
            if (!LuminaGetter.TryGetRow<Item>(SelectedItemConfig.ItemID, out var item)) return;
            
            var itemName = SelectedItemConfig.ItemID == 0
                               ? GetLoc("AutoRetainerWork-PriceAdjust-CommonItemPreset")
                               : item.Name.ExtractText() ?? string.Empty;

            var itemLogo = DService.Texture
                                   .GetFromGameIcon(new(SelectedItemConfig.ItemID == 0 ? 65002 : (uint)item.Icon, SelectedItemConfig.IsHQ))
                                   .GetWrapOrDefault();
            if (itemLogo == null) return;

            var itemBuyingPrice = SelectedItemConfig.ItemID == 0 ? 1 : item.PriceLow;

            if (!child) return;

            // 物品基本信息展示
            ImGui.Image(itemLogo.Handle, ScaledVector2(48f));

            ImGui.SameLine();
            using (FontManager.UIFont140.Push())
                ImGui.Text(itemName);

            ImGui.SameLine();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (6f * GlobalFontScale));
            ImGui.Text(SelectedItemConfig.IsHQ ? $"({GetLoc("HQ")})" : string.Empty);

            ImGui.Separator();

            // 改价逻辑配置
            using (ImRaii.Group())
            {
                foreach (AdjustBehavior behavior in Enum.GetValues(typeof(AdjustBehavior)))
                {
                    if (ImGui.RadioButton(behavior.ToString(), behavior == SelectedItemConfig.AdjustBehavior))
                    {
                        SelectedItemConfig.AdjustBehavior = behavior;
                        ModuleConfig.Save(Module);
                    }
                }
            }

            ImGui.SameLine();
            using (ImRaii.Group())
            {
                if (SelectedItemConfig.AdjustBehavior == AdjustBehavior.固定值)
                {
                    var originalValue = SelectedItemConfig.AdjustValues[AdjustBehavior.固定值];
                    ImGui.SetNextItemWidth(100f * GlobalFontScale);
                    ImGui.InputInt(GetLoc("AutoRetainerWork-PriceAdjust-ValueReduction"), ref originalValue);
                    if (ImGui.IsItemDeactivatedAfterEdit())
                    {
                        SelectedItemConfig.AdjustValues[AdjustBehavior.固定值] = originalValue;
                        ModuleConfig.Save(Module);
                    }
                }
                else
                    ImGui.Dummy(new(ImGui.GetTextLineHeightWithSpacing()));

                if (SelectedItemConfig.AdjustBehavior == AdjustBehavior.百分比)
                {
                    var originalValue = SelectedItemConfig.AdjustValues[AdjustBehavior.百分比];
                    ImGui.SetNextItemWidth(100f * GlobalFontScale);
                    ImGui.InputInt(GetLoc("AutoRetainerWork-PriceAdjust-PercentageReduction"), ref originalValue);
                    if (ImGui.IsItemDeactivatedAfterEdit())
                    {
                        SelectedItemConfig.AdjustValues[AdjustBehavior.百分比] = Math.Clamp(originalValue, -99, 99);
                        ModuleConfig.Save(Module);
                    }
                }
                else
                    ImGui.Dummy(new(ImGui.GetTextLineHeightWithSpacing()));
            }

            ScaledDummy(10f);

            // 最低可接受价格
            var originalMin = SelectedItemConfig.PriceMinimum;
            ImGui.SetNextItemWidth(200f * GlobalFontScale);
            ImGui.InputInt(GetLoc("AutoRetainerWork-PriceAdjust-PriceMinimum"), ref originalMin);
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                SelectedItemConfig.PriceMinimum = Math.Max(1, originalMin);
                ModuleConfig.Save(Module);
            }

            ImGui.SameLine();
            using (ImRaii.Disabled(SelectedItemConfig.ItemID == 0))
            {
                if (ImGuiOm.ButtonIcon("ObtainBuyingPrice", FontAwesomeIcon.Store, GetLoc("AutoRetainerWork-PriceAdjust-ObtainBuyingPrice")))
                {
                    SelectedItemConfig.PriceMinimum = Math.Max(1, (int)itemBuyingPrice);
                    ModuleConfig.Save(Module);
                }
            }

            // 最高可接受价格
            var originalMax = SelectedItemConfig.PriceMaximum;
            ImGui.SetNextItemWidth(200f * GlobalFontScale);
            ImGui.InputInt(GetLoc("AutoRetainerWork-PriceAdjust-PriceMaximum"), ref originalMax);
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                SelectedItemConfig.PriceMaximum = Math.Min(int.MaxValue, originalMax);
                ModuleConfig.Save(Module);
            }

            // 预期价格
            var originalExpected = SelectedItemConfig.PriceExpected;
            ImGui.SetNextItemWidth(200f * GlobalFontScale);
            ImGui.InputInt(GetLoc("AutoRetainerWork-PriceAdjust-PriceExpected"), ref originalExpected);
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                SelectedItemConfig.PriceExpected = Math.Max(originalMin + 1, originalExpected);
                ModuleConfig.Save(Module);
            }

            ImGui.SameLine();
            using (ImRaii.Disabled(SelectedItemConfig.ItemID == 0))
            {
                if (ImGuiOm.ButtonIcon("OpenUniversalis", FontAwesomeIcon.Globe, GetLoc("AutoRetainerWork-PriceAdjust-OpenUniversalis")))
                    Util.OpenLink($"https://universalis.app/market/{SelectedItemConfig.ItemID}");
            }

            // 可接受降价值
            var originalPriceReducion = SelectedItemConfig.PriceMaxReduction;
            ImGui.SetNextItemWidth(200f * GlobalFontScale);
            ImGui.InputInt(GetLoc("AutoRetainerWork-PriceAdjust-PriceMaxReduction"), ref originalPriceReducion);
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                SelectedItemConfig.PriceMaxReduction = Math.Max(0, originalPriceReducion);
                ModuleConfig.Save(Module);
            }
            
            // 单次上架数
            var originalUpshelfCount = SelectedItemConfig.UpshelfCount;
            ImGui.SetNextItemWidth(200f * GlobalFontScale);
            ImGui.InputInt(GetLoc("AutoRetainerWork-PriceAdjust-UpshelfCount"), ref originalUpshelfCount);
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                SelectedItemConfig.UpshelfCount = originalUpshelfCount;
                ModuleConfig.Save(Module);
            }

            ScaledDummy(10f);

            // 意外情况
            using (ImRaii.Group())
            {
                ImGui.SetNextItemWidth(250f * GlobalFontScale);
                using (var combo = ImRaii.Combo("###AddNewLogicConditionCombo", ConditionInput.ToString(), ImGuiComboFlags.HeightLarge))
                {
                    if (combo)
                    {
                        foreach (AbortCondition condition in Enum.GetValues(typeof(AbortCondition)))
                        {
                            if (condition == AbortCondition.无) continue;
                            if (ImGui.Selectable(condition.ToString(), ConditionInput.HasFlag(condition), ImGuiSelectableFlags.DontClosePopups))
                            {
                                var combinedCondition = ConditionInput;
                                if (ConditionInput.HasFlag(condition))
                                    combinedCondition &= ~condition;
                                else
                                    combinedCondition |= condition;

                                ConditionInput = combinedCondition;
                            }
                        }
                    }
                }

                ImGui.SetNextItemWidth(250f * GlobalFontScale);
                using (var combo = ImRaii.Combo("###AddNewLogicBehaviorCombo", BehaviorInput.ToString(), ImGuiComboFlags.HeightLarge))
                {
                    if (combo)
                    {
                        foreach (AbortBehavior behavior in Enum.GetValues(typeof(AbortBehavior)))
                        {
                            if (ImGui.Selectable(behavior.ToString(), BehaviorInput == behavior, ImGuiSelectableFlags.DontClosePopups))
                                BehaviorInput = behavior;
                        }
                    }
                }
            }

            var groupSize0 = ImGui.GetItemRectSize();

            ImGui.SameLine();
            if (ImGuiOm.ButtonIconWithTextVertical(FontAwesomeIcon.Plus, GetLoc("Add"),
                                                   groupSize0 with { X = ImGui.CalcTextSize(GetLoc("Add")).X * 2f }))
            {
                if (ConditionInput != AbortCondition.无)
                {
                    SelectedItemConfig.AbortLogic.TryAdd(ConditionInput, BehaviorInput);
                    ModuleConfig.Save(Module);
                }
            }

            ImGui.Separator();

            foreach (var logic in SelectedItemConfig.AbortLogic.ToList())
            {
                // 条件处理 (键)
                var origConditionStr = logic.Key.ToString();
                ImGui.SetNextItemWidth(300f * GlobalFontScale);
                ImGui.InputText($"###Condition_{origConditionStr}", ref origConditionStr, 100, ImGuiInputTextFlags.ReadOnly);

                if (ImGui.IsItemClicked())
                    ImGui.OpenPopup($"###ConditionSelectPopup_{origConditionStr}");

                using (var popup = ImRaii.Popup($"###ConditionSelectPopup_{origConditionStr}"))
                {
                    if (popup)
                    {
                        foreach (AbortCondition condition in Enum.GetValues(typeof(AbortCondition)))
                        {
                            if (ImGui.Selectable(condition.ToString(), logic.Key.HasFlag(condition)))
                            {
                                var combinedCondition = logic.Key;
                                if (logic.Key.HasFlag(condition))
                                    combinedCondition &= ~condition;
                                else
                                    combinedCondition |= condition;

                                if (!SelectedItemConfig.AbortLogic.ContainsKey(combinedCondition))
                                {
                                    var origBehavior = logic.Value;
                                    SelectedItemConfig.AbortLogic[combinedCondition] = origBehavior;
                                    SelectedItemConfig.AbortLogic.Remove(logic.Key);
                                    ModuleConfig.Save(Module);
                                }
                            }
                        }
                    }
                }

                ImGui.SameLine();
                ImGui.Text("→");

                // 行为处理 (值)
                var origBehaviorStr = logic.Value.ToString();
                ImGui.SameLine();
                ImGui.SetNextItemWidth(300f * GlobalFontScale);
                ImGui.InputText($"###Behavior_{origBehaviorStr}", ref origBehaviorStr, 128, ImGuiInputTextFlags.ReadOnly);

                if (ImGui.IsItemClicked())
                    ImGui.OpenPopup($"###BehaviorSelectPopup_{origBehaviorStr}");

                using (var popup = ImRaii.Popup($"###BehaviorSelectPopup_{origBehaviorStr}"))
                {
                    if (popup)
                    {
                        foreach (AbortBehavior behavior in Enum.GetValues(typeof(AbortBehavior)))
                        {
                            if (ImGui.Selectable(behavior.ToString(), behavior == logic.Value))
                            {
                                SelectedItemConfig.AbortLogic[logic.Key] = behavior;
                                ModuleConfig.Save(Module);
                            }
                        }
                    }
                }

                ImGui.SameLine();
                if (ImGuiOm.ButtonIcon($"Delete_{logic.Key}_{logic.Value}", FontAwesomeIcon.TrashAlt, GetLoc("Delete")))
                    SelectedItemConfig.AbortLogic.Remove(logic.Key);
            }
        }

        private static void DrawMarketItemsTable()
        {
            var retainerManager = RetainerManager.Instance();
            if (retainerManager == null) return;

            var currentActiveRetainer = retainerManager->GetActiveRetainer();
            if (currentActiveRetainer == null) return;

            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null) return;

            var marketContainer = inventoryManager->GetInventoryContainer(InventoryType.RetainerMarket);
            if (marketContainer == null || !marketContainer->IsLoaded) return;

            using var font = FontManager.GetUIFont(ModuleConfig.MarketItemsWindowFontScale).Push();

            if (ImGui.BeginMenuBar())
            {
                ImGui.Text($"{GetLoc("AutoRetainerWork-PriceAdjust-Adjust")}:");
                
                using (ImRaii.Disabled(TaskHelper.IsBusy))
                {
                    if (ImGui.MenuItem(GetLoc("Start")))
                        EnqueuePriceAdjustSingle();
                }

                if (ImGui.MenuItem(GetLoc("Stop")))
                    TaskHelper.Abort();
                
                ImGui.TextDisabled("|");
                
                using (ImRaii.Disabled(TaskHelper.IsBusy))
                {
                    if (ImGui.BeginMenu(GetLoc("Shortcut")))
                    {
                        if (ImGui.MenuItem(GetLoc("AutoRetainerWork-PriceAdjust-ReturnAllToInventory")))
                            Enumerable.Range(0, marketContainer->Size)
                                      .ForEach(x => ReturnRetainerMarketItemToInventory((ushort)x, true));

                        if (ImGui.MenuItem(GetLoc("AutoRetainerWork-PriceAdjust-ReturnAllToRetainer")))
                            Enumerable.Range(0, marketContainer->Size)
                                      .ForEach(x => ReturnRetainerMarketItemToInventory((ushort)x, false));
                        ImGui.EndMenu();
                    }
                }

                ImGui.TextDisabled("|");
                
                using (ImRaii.Disabled(TaskHelper.IsBusy))
                {
                    if (ImGui.MenuItem(GetLoc("AutoRetainerWork-PriceAdjust-ClearCache")))
                    {
                        PriceCacheManager.ClearCache();
                        NotificationSuccess(GetLoc("AutoRetainerWork-PriceAdjust-CacheCleared"));
                    }
                }

                ImGui.TextDisabled("|");
                
                if (ImGui.BeginMenu(GetLoc("Settings")))
                {
                    if (ImGui.BeginMenu(GetLoc("FontSize")))
                    {
                        for (var i = 0.6f; i < 1.8f; i += 0.2f)
                        {
                            var fontScale = (float)Math.Round(i, 1);
                            
                            if (ImGui.MenuItem($"{fontScale}", string.Empty,
                                               fontScale == ModuleConfig.MarketItemsWindowFontScale))
                            {
                                ModuleConfig.MarketItemsWindowFontScale = fontScale;
                                ModuleConfig.Save(Module);
                            }
                        }
                        
                        ImGui.EndMenu();
                    }
                    
                    if (ImGui.BeginMenu(GetLoc("AutoRetainerWork-PriceAdjust-SortOrder")))
                    {
                        foreach (var sortOrder in Enum.GetValues<SortOrder>())
                        {
                            if (ImGui.MenuItem($"{sortOrder}", string.Empty,
                                               sortOrder == ModuleConfig.MarketItemsSortOrder))
                            {
                                ModuleConfig.MarketItemsSortOrder = sortOrder;
                                ModuleConfig.Save(Module);
                            }
                        }
                        
                        ImGui.EndMenu();
                    }
                    
                    if (ImGui.MenuItem(GetLoc("AutoRetainerWork-PriceAdjust-AutoAdjustWhenNewOnSale"), string.Empty,
                                       ModuleConfig.AutoPriceAdjustWhenNewOnSale))
                    {
                        ModuleConfig.AutoPriceAdjustWhenNewOnSale ^= true;
                        ModuleConfig.Save(Module);
                    }
                    
                    if (ImGui.MenuItem(GetLoc("AutoRetainerWork-PriceAdjust-SendProcessMessage"), string.Empty,
                                       ModuleConfig.SendPriceAdjustProcessMessage))
                    {
                        ModuleConfig.SendPriceAdjustProcessMessage ^= true;
                        ModuleConfig.Save(Module);
                    }

                    ImGui.EndMenu();
                }
                
                ImGui.TextDisabled("|");
                
                using (ImRaii.Disabled(TaskHelper.IsBusy))
                {
                    if (ImGui.MenuItem(LuminaGetter.GetRow<Addon>(2366)!.Value.Text.ExtractText()))
                        Callback(RetainerSellList, true, -1);
                }

                ImGui.EndMenuBar();
            }
            
            using var disabled = ImRaii.Disabled(TaskHelper.IsBusy);
            using var table = ImRaii.Table("MarketItemTable", 5,
                                           ImGuiTableFlags.Borders   | ImGuiTableFlags.Reorderable |
                                           ImGuiTableFlags.Resizable | ImGuiTableFlags.Hideable);
            if (!table) return;

            ImGui.TableSetupColumn("###Sort",                        ImGuiTableColumnFlags.WidthFixed,   ImGui.GetTextLineHeightWithSpacing());
            ImGui.TableSetupColumn(GetLoc("Item"),                   ImGuiTableColumnFlags.WidthStretch, 30);
            ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(933),  ImGuiTableColumnFlags.WidthStretch, 10);
            ImGui.TableSetupColumn(GetLoc("Amount"),                 ImGuiTableColumnFlags.WidthFixed,   ImGui.CalcTextSize(GetLoc("Amount")).X * 1.2f);
            ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(6936), ImGuiTableColumnFlags.WidthStretch, 10);

            ImGui.TableHeadersRow();

            if (!TryGetInventoryItems([InventoryType.RetainerMarket], x => x.ItemId != 0, out var validItems)) return;

            var itemSource = validItems
                             .Select(x => new
                             {
                                 Inventory = x,
                                 Data      = LuminaGetter.GetRow<Item>(x.ItemId).GetValueOrDefault(),
                                 Slot      = (ushort)x.Slot,
                             })
                             .OrderBy(x => ModuleConfig.MarketItemsSortOrder switch
                             {
                                 SortOrder.上架顺序 => (uint)x.Inventory.Slot,
                                 SortOrder.物品ID => x.Data.RowId,
                                 SortOrder.物品类型 => x.Data.FilterGroup,
                                 _              => 0U
                             })
                             .ThenBy(x => ModuleConfig.MarketItemsSortOrder switch
                             {
                                 SortOrder.物品ID => x.Data.RowId,
                                 _              => 0U
                             })
                             .ToArray();

            foreach (var item in itemSource)
            {
                var itemPrice = GetRetainerMarketPrice(item.Slot);
                if (itemPrice == 0) continue;

                var isItemHQ = item.Inventory.Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                var itemIcon = DService.Texture.GetFromGameIcon(new(item.Data.Icon, isItemHQ)).GetWrapOrDefault();
                if (itemIcon == null) continue;

                var itemName = $"{item.Data.Name.ExtractText()}" + (isItemHQ ? "\ue03c" : string.Empty);

                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.Text($"{item.Slot + 1}");

                DrawItemColumn(item.Slot, item.Inventory.ItemId, itemName, itemIcon);

                DrawUnitPriceColumn(item.Slot, item.Inventory.ItemId, itemPrice, (uint)item.Inventory.Quantity, itemIcon, itemName);

                ImGui.TableNextColumn();
                ImGui.Text($"{item.Inventory.Quantity}");

                ImGui.TableNextColumn();
                ImGui.Text($"{FormatNumber((uint)(item.Inventory.Quantity * itemPrice))}");
            }
        }

        private static void DrawItemColumn(ushort slot, uint itemID, string itemName, IDalamudTextureWrap itemIcon)
        {
            using var id = ImRaii.PushId(slot);

            ImGui.TableNextColumn();
            ImGuiOm.SelectableImageWithText(itemIcon.Handle, new(ImGui.GetTextLineHeightWithSpacing()), itemName, false);

            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsItemClicked())
                RequestMarketItemData(itemID);

            using var popup = ImRaii.ContextPopupItem("MarketItemOperationPopup");
            if (!popup) return;

            if (ImGui.MenuItem(LuminaWrapper.GetAddonText(976)))
                ReturnRetainerMarketItemToInventory(slot, true);

            if (ImGui.MenuItem(LuminaWrapper.GetAddonText(958)))
                ReturnRetainerMarketItemToInventory(slot, false);
        }

        private static void DrawUnitPriceColumn(ushort slot, uint itemID, uint price, uint quantity, IDalamudTextureWrap itemIcon, string itemName)
        {
            using var id = ImRaii.PushId(slot);

            ImGui.TableNextColumn();
            ImGui.Selectable($"{FormatNumber(price)}");

            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            var isNeedOpenManualModifyPopup    = false;
            var isNeedOpenAllManualModifyPopup = false;

            using (var popup = ImRaii.ContextPopupItem("ModifyUnitPricePopup"))
            {
                if (popup)
                {
                    if (ImGui.MenuItem(GetLoc("AutoRetainerWork-PriceAdjust-AdjustUnitPriceAuto")))
                        EnqueuePriceAdjustSingle(slot);

                    if (ImGui.MenuItem(GetLoc("AutoRetainerWork-PriceAdjust-AdjustUnitPriceManual")))
                    {
                        ImGui.CloseCurrentPopup();

                        RequestMarketItemData(itemID);
                        isNeedOpenManualModifyPopup = true;
                    }

                    using (ImRaii.Group())
                    {
                        if (ImGui.MenuItem(GetLoc("AutoRetainerWork-PriceAdjust-AdjustUnitPriceAllSameItems")))
                        {
                            if (TryGetSameItemSlots(itemID, out var slots))
                                slots.ForEach(s => EnqueuePriceAdjustSingle(s));
                        }
                    
                        if (ImGui.MenuItem(GetLoc("AutoRetainerWork-PriceAdjust-AdjustUnitPriceAllSameItemsManual")))
                        {
                            ImGui.CloseCurrentPopup();
                        
                            RequestMarketItemData(itemID);
                            isNeedOpenAllManualModifyPopup = true;
                        }
                    }
                    ImGuiOm.TooltipHover(GetLoc("AutoRetainerWork-PriceAdjust-AdjustUnitPriceAllSameItemsHelp"));
                }
            }

            if (isNeedOpenManualModifyPopup)
                ImGui.OpenPopup("ModifyUnitPriceManualPopup");

            using (var popup = ImRaii.Popup("ModifyUnitPriceManualPopup"))
            {
                if (popup)
                {
                    if (ImGui.IsWindowAppearing())
                        ItemModifyUnitPriceManual = price;

                    ImGui.Image(itemIcon.Handle, ManualUnitPriceImageSize with { X = ManualUnitPriceImageSize.Y });

                    ImGui.SameLine();
                    using (ImRaii.Group())
                    {
                        using (FontManager.UIFont140.Push())
                            ImGui.Text($"{itemName}");

                        ImGui.TextDisabled($"{GetLoc("AutoRetainerWork-PriceAdjust-MarketItemsCount")}: {quantity}");
                    }
                    ManualUnitPriceImageSize = ImGui.GetItemRectSize();

                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{LuminaWrapper.GetAddonText(933)}:");

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(150f * GlobalFontScale);
                    ImGui.InputUInt("###UnitPriceInput", ref ItemModifyUnitPriceManual);

                    ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{LuminaWrapper.GetAddonText(6936)}:");

                    ImGui.SameLine();
                    ImGui.Text($"{FormatNumber(quantity * ItemModifyUnitPriceManual)}");

                    ImGui.Separator();

                    if (ImGuiOm.ButtonSelectable(GetLoc("Confirm")))
                    {
                        SetRetainerMarketItemPrice(slot, ItemModifyUnitPriceManual);
                        ImGui.CloseCurrentPopup();
                    }
                }
            }
            
            if (isNeedOpenAllManualModifyPopup)
                ImGui.OpenPopup("ModifyAllUnitPriceManualPopup");

            using (var popup = ImRaii.Popup("ModifyAllUnitPriceManualPopup"))
            {
                if (popup)
                {
                    if (ImGui.IsWindowAppearing())
                    {
                        ItemModifyUnitPriceManual = price;
                        ItemModifyCountManual     = (uint)(TryGetSameItemSlots(itemID, out var slots) ? slots.Count : 0);
                    }

                    ImGui.Image(itemIcon.Handle, ManualUnitPriceImageSize with { X = ManualUnitPriceImageSize.Y });

                    ImGui.SameLine();
                    using (ImRaii.Group())
                    {
                        using (FontManager.UIFont140.Push())
                            ImGui.Text($"{itemName}");

                        ImGui.TextDisabled($"{GetLoc("AutoRetainerWork-PriceAdjust-MarketItemsCount")}: {quantity}");
                        
                        ImGui.SameLine();
                        ImGui.TextDisabled("/");
                        
                        ImGui.SameLine();
                        ImGui.TextDisabled($"{GetLoc("AutoRetainerWork-PriceAdjust-SameItemsCount")}: {ItemModifyCountManual}");
                    }
                    ManualUnitPriceImageSize = ImGui.GetItemRectSize();

                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{LuminaWrapper.GetAddonText(933)}:");

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(150f * GlobalFontScale);
                    ImGui.InputUInt("###UnitPriceInput", ref ItemModifyUnitPriceManual);

                    ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{LuminaWrapper.GetAddonText(6936)}:");

                    ImGui.SameLine();
                    ImGui.Text($"{FormatNumber(quantity * ItemModifyUnitPriceManual)}");

                    ImGui.Separator();

                    if (ImGuiOm.ButtonSelectable(GetLoc("Confirm")))
                    {
                        if (TryGetSameItemSlots(itemID, out var slots))
                            slots.ForEach(s => EnqueuePriceAdjustSingle(s, ItemModifyUnitPriceManual));
                        
                        ImGui.CloseCurrentPopup();
                    }
                }
            }
        }

        private static void DrawMarketDataTable()
        {
            var info = InfoProxyItemSearch.Instance();
            if (info == null) return;

            if (info->SearchItemId == 0) return;

            var listingsArray = info->Listings.ToArray()
                                              .Where(x => x.ItemId == info->SearchItemId && x.UnitPrice != 0 &&
                                                          !PlayerRetainers.Contains(x.SellingRetainerContentId))
                                              .OrderBy(x => x.UnitPrice)
                                              .ToArray();

            if (!LuminaGetter.TryGetRow<Item>(info->SearchItemId, out var itemData)) return;

            var itemIcon = DService.Texture.GetFromGameIcon(new(itemData.Icon)).GetWrapOrDefault();
            if (itemIcon == null) return;

            using var font = FontManager.UIFont.Push();

            ImGui.Image(itemIcon.Handle, MarketDataTableImageSize with { X = MarketDataTableImageSize.Y });

            ImGui.SameLine();
            using (ImRaii.Group())
            {
                using (FontManager.UIFont160.Push())
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.Text($"{itemData.Name}");
                }

                using (FontManager.UIFont.Push())
                {
                    ImGui.TextDisabled($"{GetLoc("AutoRetainerWork-PriceAdjust-OnSaleCount")}: {info->ListingCount}");

                    if (listingsArray.Length > 0)
                    {
                        var minPrice = listingsArray.Min(x => x.UnitPrice);
                        ImGui.SameLine();
                        ImGui.TextDisabled($" / {GetLoc("AutoRetainerWork-PriceAdjust-MinPrice")}: {FormatNumber(minPrice)} / ");
                        ClickToCopy(minPrice.ToString());

                        var maxPrice = listingsArray.Max(x => x.UnitPrice);
                        ImGui.SameLine();
                        ImGui.TextDisabled($"{GetLoc("AutoRetainerWork-PriceAdjust-MaxPrice")}: {FormatNumber(maxPrice)}");
                        ClickToCopy(maxPrice.ToString());
                    }
                }
            }

            MarketDataTableImageSize = ImGui.GetItemRectSize();

            var       childSize = new Vector2(ImGui.GetContentRegionAvail().X, 250f * GlobalFontScale);
            using var child     = ImRaii.Child("MarketDataChild", childSize, false, ImGuiWindowFlags.NoBackground);
            if (!child) return;

            var isAnyHQ              = listingsArray.Any(x => x.IsHqItem);
            var isAnyOnMannequin     = listingsArray.Any(x => x.IsMannequin);
            var isAnyMateriaEquipped = itemData.MateriaSlotCount > 0 && listingsArray.Any(x => x.MateriaCount > 0);

            var columnsCount = 6;
            if (!isAnyHQ) 
                columnsCount--;
            if (!isAnyMateriaEquipped) 
                columnsCount--;
            if (!isAnyOnMannequin) 
                columnsCount--;

            using var table = ImRaii.Table("MarketBoardDataTable", columnsCount, ImGuiTableFlags.Borders);
            if (!table) return;

            if (isAnyHQ)
                ImGui.TableSetupColumn("\ue03c", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("\ue03c").X);

            if (isAnyMateriaEquipped)
            {
                var materiaText = LuminaWrapper.GetAddonText(1937);
                ImGui.TableSetupColumn(materiaText, ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize(materiaText).X);
            }

            if (isAnyOnMannequin)
                ImGui.TableSetupColumn(GetLoc("Mannequin"), ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize(GetLoc("Mannequin")).X);

            ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(357),  ImGuiTableColumnFlags.WidthStretch, 15);
            ImGui.TableSetupColumn(GetLoc("Amount"),                 ImGuiTableColumnFlags.WidthFixed,   ImGui.CalcTextSize(GetLoc("Amount")).X);
            ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(6936), ImGuiTableColumnFlags.WidthStretch, 15);

            ImGui.TableHeadersRow();

            foreach (var listing in listingsArray)
            {
                using var id = ImRaii.PushId(listing.ListingId.ToString());
                ImGui.TableNextRow();

                if (isAnyHQ)
                {
                    ImGui.TableNextColumn();
                    ImGui.Text(listing.IsHqItem ? "√" : string.Empty);
                }

                if (isAnyMateriaEquipped)
                {
                    ImGui.TableNextColumn();
                    ImGui.Text($"{listing.MateriaCount}");
                }

                if (isAnyOnMannequin)
                {
                    ImGui.TableNextColumn();
                    ImGui.Text(listing.IsMannequin ? "√" : string.Empty);
                }

                ImGui.TableNextColumn();
                ImGui.Text($"{FormatNumber(listing.UnitPrice)}");
                ClickToCopy(listing.UnitPrice.ToString());

                ImGui.TableNextColumn();
                ImGui.Text($"{listing.Quantity}");

                ImGui.TableNextColumn();
                ImGui.Text($"{FormatNumber((listing.UnitPrice * listing.Quantity) + listing.TotalTax)}");
            }
        }

        private static void DrawMarketHistoryDataTable()
        {
            var info = InfoProxyItemSearch.Instance();
            if (info == null) return;

            if (HistoryListings.Key == 0) return;
            if (!LuminaGetter.TryGetRow<Item>(HistoryListings.Key, out _)) return;

            using var font = FontManager.UIFont.Push();

            using (ImRaii.Group())
            {
                using (FontManager.UIFont160.Push())
                    ImGui.Text($"{LuminaWrapper.GetAddonText(1165)}");

                ImGui.TextDisabled($"{GetLoc("AutoRetainerWork-PriceAdjust-OnSaleCount")}: {info->ListingCount}");

                if (HistoryListings.Value.Count > 0)
                {
                    var minPrice = HistoryListings.Value.Min(x => x.SalePrice);
                    ImGui.SameLine();
                    ImGui.TextDisabled($" / {GetLoc("AutoRetainerWork-PriceAdjust-MinPrice")}: {FormatNumber(minPrice)} / ");
                    ClickToCopy(minPrice.ToString());

                    var maxPrice = HistoryListings.Value.Max(x => x.SalePrice);
                    ImGui.SameLine();
                    ImGui.TextDisabled($"{GetLoc("AutoRetainerWork-PriceAdjust-MaxPrice")}: {FormatNumber(maxPrice)}");
                    ClickToCopy(maxPrice.ToString());
                }
            }

            var       childSize = new Vector2(ImGui.GetContentRegionAvail().X, 250f * GlobalFontScale);
            using var child     = ImRaii.Child("HistoryDataChild", childSize, false, ImGuiWindowFlags.NoBackground);
            if (!child) return;

            var isAnyHQ = HistoryListings.Value.Any(x => x.IsHq);

            var columnsCount = 5;
            if (!isAnyHQ) 
                columnsCount--;

            using var table = ImRaii.Table("MarketBoardDataTable", columnsCount, ImGuiTableFlags.Borders);
            if (!table) return;

            if (isAnyHQ)
                ImGui.TableSetupColumn("\ue03c", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("\ue03c").X);

            ImGui.TableSetupColumn(GetLoc("Amount"),                 ImGuiTableColumnFlags.WidthFixed,   ImGui.CalcTextSize(GetLoc("Amount")).X);
            ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(357),  ImGuiTableColumnFlags.WidthStretch, 15);
            ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(1975), ImGuiTableColumnFlags.WidthStretch, 15);
            ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(1976), ImGuiTableColumnFlags.WidthStretch, 15);

            ImGui.TableHeadersRow();

            foreach (var listing in HistoryListings.Value)
            {
                if (listing.OnMannequin) continue;

                using var id = ImRaii.PushId($"{listing.BuyerName}-{listing.SalePrice}-{listing.Quantity}-{listing.PurchaseTime}");
                ImGui.TableNextRow();

                if (isAnyHQ)
                {
                    ImGui.TableNextColumn();
                    ImGui.Text(listing.IsHq ? "√" : string.Empty);
                }

                ImGui.TableNextColumn();
                ImGui.Text($"{listing.Quantity}");

                ImGui.TableNextColumn();
                ImGui.Text($"{FormatNumber(listing.SalePrice)}");
                ClickToCopy(listing.SalePrice.ToString());

                ImGui.TableNextColumn();
                ImGui.Text($"{listing.BuyerName}");
                ClickToCopy(listing.BuyerName);

                ImGui.TableNextColumn();
                ImGui.Text($"{listing.PurchaseTime}");
            }
        }

        private static void DrawMarketUpshelf()
        {
            var manager = InventoryManager.Instance();
            if (manager == null) return;

            var container = manager->GetInventoryContainer(SourceUpshelfType);
            if (container == null || !container->IsLoaded) return;

            var slotData = container->GetInventorySlot(SourceUpshelfSlot);
            if (slotData == null || slotData->ItemId == 0) return;

            if (!LuminaGetter.TryGetRow<Item>(slotData->ItemId, out var itemData)) return;

            var isItemHQ = slotData->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);

            var itemIcon = DService.Texture
                                   .GetFromGameIcon(new(itemData.Icon, isItemHQ))
                                   .GetWrapOrDefault();
            if (itemIcon == null) return;

            using var id   = ImRaii.PushId($"{SourceUpshelfType}_{SourceUpshelfSlot}");
            using var font = FontManager.UIFont120.Push();

            using (FontManager.UIFont80.Push())
            {
                if (ImGuiOm.ButtonSelectable(LuminaWrapper.GetAddonText(2366)))
                    IsNeedToDrawMarketUpshelfWindow = false;
            }

            ImGui.Separator();
            ImGui.Spacing();
            
            ImGui.Image(itemIcon.Handle, ManualUnitPriceImageSize with { X = ManualUnitPriceImageSize.Y });

            ImGui.SameLine();
            using (ImRaii.Group())
            {
                using (FontManager.UIFont160.Push())
                    ImGui.Text($"{itemData.Name.ExtractText()}" + (isItemHQ ? "\ue03c" : string.Empty));
            }
            ManualUnitPriceImageSize = ImGui.GetItemRectSize();

            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{LuminaWrapper.GetAddonText(933)}:");

            ImGui.SameLine();
            ImGui.SetNextItemWidth(150f * GlobalFontScale);
            ImGui.InputUInt("###UnitPriceInput", ref UpshelfUnitPriceInput);

            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("Amount")}:");

            ImGui.SameLine();
            ImGui.SetNextItemWidth(150f * GlobalFontScale);
            ImGui.InputUInt("###QuantityInput", ref UpshelfQuantityInput);

            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{LuminaWrapper.GetAddonText(6936)}:");

            ImGui.SameLine();
            ImGui.Text($"{FormatNumber(UpshelfQuantityInput * UpshelfUnitPriceInput)}");

            ImGui.Separator();

            if (ImGuiOm.ButtonSelectable(GetLoc("AutoRetainerWork-PriceAdjust-UpshelfAuto")))
            {
                if (TryGetFirstEmptyRetainerMarketSlot(out var firstEmptySlot))
                {
                    UpshelfMarketItem(SourceUpshelfType, SourceUpshelfSlot, UpshelfQuantityInput, 9_9999_9999, (short)firstEmptySlot);
                    EnqueuePriceAdjustSingle(firstEmptySlot);
                    IsNeedToDrawMarketUpshelfWindow = false;
                }
            }

            if (ImGuiOm.ButtonSelectable(GetLoc("AutoRetainerWork-PriceAdjust-UpshelfManual")))
            {
                UpshelfMarketItem(SourceUpshelfType, SourceUpshelfSlot, UpshelfQuantityInput, UpshelfUnitPriceInput);
                IsNeedToDrawMarketUpshelfWindow = false;
            }
        }

        #endregion

        #region 事件

        // 出售品列表 (悬浮窗控制)
        private static void OnRetainerSellList(AddonEvent type, AddonArgs args)
        {
            if (!DService.Condition[ConditionFlag.OccupiedSummoningBell]) return;
            
            IsNeedToDrawMarketListWindow = type switch
            {
                AddonEvent.PostDraw => true,
                AddonEvent.PreFinalize => false,
                _ => IsNeedToDrawMarketListWindow
            };
        }

        // 出售界面
        private static void OnRetainerSell(AddonEvent type, AddonArgs args)
        {
            if (!DService.Condition[ConditionFlag.OccupiedSummoningBell]) return;
            if (!IsAddonAndNodesReady(args.Addon.ToAtkUnitBase())) return;
            Callback(args.Addon, true, 0);
        }

        // 当前市场数据获取
        private static void OnOfferingReceived(IMarketBoardCurrentOfferings data) => 
            PriceCacheManager.OnOfferingReceived(data);

        // 历史交易数据获取
        private static void OnHistoryReceived(IMarketBoardHistory history)
        {
            if (history.ItemId != HistoryListings.Key)
                HistoryListings = new(history.ItemId, []);
            HistoryListings.Value.AddRange(history.HistoryListings);
            
            PriceCacheManager.OnHistoryReceived(history);
        }

        // 上架 => 全部拦截
        private static void MoveToRetainerMarketDetour(
            InventoryManager* manager,
            InventoryType     srcInv,
            ushort            srcSlot,
            InventoryType     dstInv,
            ushort            dstSlot,
            uint              quantity,
            uint              unitPrice)
        {
            var slot = manager->GetInventorySlot(srcInv, srcSlot);
            if (slot == null) return;

            if (!TryGetItemUpshelfCountLimit(*slot, out var upshelfQuantity)) return;

            if (ModuleConfig.AutoPriceAdjustWhenNewOnSale && !IsConflictKeyPressed())
            {
                MoveToRetainerMarketHook.Original(manager, srcInv, srcSlot, dstInv, dstSlot, upshelfQuantity, 9_9999_9999);
                EnqueuePriceAdjustSingle(dstSlot);
                return;
            }

            SourceUpshelfType = srcInv;
            SourceUpshelfSlot = srcSlot;

            var info = InfoProxyItemSearch.Instance();
            if (info == null) return;

            if (info->SearchItemId != slot->ItemId)
                RequestMarketItemData(slot->ItemId);

            UpshelfUnitPriceInput = LuminaGetter.TryGetRow<Item>(slot->ItemId, out var itemRow) ? itemRow.PriceMid : 1;
            UpshelfQuantityInput  = upshelfQuantity;

            IsNeedToDrawMarketUpshelfWindow = true;
        }

        #endregion

        #region 队列

        private static void EnqueuePriceAdjustAll()
        {
            if (InterruptByConflictKey(TaskHelper, Module)) return;
            if (IsAnyOtherWorkerBusy(typeof(PriceAdjustWorker))) return;

            var count = GetValidRetainerCount(x => x is { Available: true, MarketItemCount: > 0 }, out var validRetainers);
            if (count == 0) return;

            validRetainers
                .ForEach(index =>
                {
                    TaskHelper.Enqueue(() =>
                    {
                        if (InterruptByConflictKey(TaskHelper, Module)) return true;
                        return EnterRetainer(index);
                    }, $"选择进入 {index} 号雇员");
                    TaskHelper.Enqueue(() =>
                    {
                        if (InterruptByConflictKey(TaskHelper, Module)) return true;
                        return IsAddonAndNodesReady(SelectString) && RetainerManager.Instance()->GetActiveRetainer() != null;
                    }, $"等待接收 {index} 号雇员的数据");
                    TaskHelper.Enqueue(() =>
                    {
                        if (InterruptByConflictKey(TaskHelper, Module)) return true;
                        return ClickSelectString(SellInventoryItemsText);
                    }, "点击进入出售玩家所持物品列表");
                    TaskHelper.Enqueue(() =>
                    {
                        if (InterruptByConflictKey(TaskHelper, Module)) return;
                        EnqueuePriceAdjustSingle();
                    }, "由单一雇员商品改价接管后续逻辑");
                    TaskHelper.Enqueue(() =>
                    {
                        if (InterruptByConflictKey(TaskHelper, Module)) return;
                        if (!IsAddonAndNodesReady(RetainerSellList)) return;
                        Callback(RetainerSellList, true, -1);
                    }, "单一雇员改价完成, 退出出售品列表界面");
                    TaskHelper.Enqueue(() =>
                    {
                        if (InterruptByConflictKey(TaskHelper, Module)) return true;
                        return LeaveRetainer();
                    }, "单一雇员改价完成, 返回至雇员列表界面");
                });
        }

        private static void EnqueuePriceAdjustSingle()
        {
            if (InterruptByConflictKey(TaskHelper, Module)) return;
            if (IsAnyOtherWorkerBusy(typeof(PriceAdjustWorker))) return;

            var retainer = RetainerManager.Instance()->GetActiveRetainer();
            if (retainer == null || retainer->MarketItemCount <= 0) return;

            var container = InventoryManager.Instance()->GetInventoryContainer(InventoryType.RetainerMarket);
            if (container == null || !container->IsLoaded) return;

            for (ushort i = 0; i < container->Size; i++)
                EnqueuePriceAdjustSingle(i);
        }

        private static void EnqueuePriceAdjustSingle(ushort slotIndex, uint forcePrice = 0)
        {
            if (InterruptByConflictKey(TaskHelper, Module)) return;
            if (IsAnyOtherWorkerBusy(typeof(PriceAdjustWorker))) return;

            TaskHelper.Enqueue(() =>
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
                    
                    TaskHelper.Enqueue(() =>
                    {
                        if (InterruptByConflictKey(TaskHelper, Module)) return;
                        RequestMarketItemData(itemID);
                    }, $"请求雇员 {retainer->NameString} {slotIndex} 号位置处 {itemName} 的市场价格数据", weight: 2);
                    if (isNothingSearched)
                        TaskHelper.DelayNext(1000, "初始无数据, 等待 1 秒", weight: 2);
                    TaskHelper.Enqueue(() =>
                    {
                        if (InterruptByConflictKey(TaskHelper, Module)) return true;
                        if (IsMarketStuck()) return false;
                        
                        return IsMarketItemDataReady(itemID);
                    }, $"等待 {itemName} 市场价格数据完全到达", weight: 2);
                    TaskHelper.Enqueue(() =>
                    {
                        if (InterruptByConflictKey(TaskHelper, Module)) return;
                        // 什么价格数据都没有, 设置为 0
                        if (!PriceCacheManager.TryGetPriceCache(itemID, isItemHQ, out price)) 
                            price = 0;
                        EnqueuePriceAdjustSingleItem(slotIndex, price, forcePrice);
                    }, "由单一物品改价接管后续逻辑", weight: 2);
                    return;
                }

                TaskHelper.Enqueue(() => EnqueuePriceAdjustSingleItem(slotIndex, price, forcePrice), "由单一物品改价接管后续逻辑", weight: 2);
            }, weight: 1);
        }

        private static void EnqueuePriceAdjustSingleItem(ushort slot, uint marketPrice, uint forcePrice = 0)
        {
            if (InterruptByConflictKey(TaskHelper, Module)) return;
            if (IsAnyOtherWorkerBusy(typeof(PriceAdjustWorker))) return;

            var itemMarketData = GetRetainerMarketItem(slot);
            if (itemMarketData == null) return;

            var itemConfig    = GetItemConfigByItemKey(itemMarketData.Value.Item);
            var modifiedPrice = forcePrice > 0 ? forcePrice : GetModifiedPrice(itemConfig, marketPrice);

            // 价格为 0
            if (modifiedPrice == 0) return;

            // 价格不变
            if (modifiedPrice == itemMarketData.Value.Price) return;

            if (IsAnyAbortConditionsMet(itemConfig,             itemMarketData.Value.Price, modifiedPrice, marketPrice,
                                        out var abortCondition, out var abortBehavior))
            {
                NotifyAbortCondition(itemMarketData.Value.Item.ItemID, itemMarketData.Value.Item.IsHQ, abortCondition);
                EnqueueAbortBehavior(abortBehavior);
                return;
            }

            SetRetainerMarketItemPrice(slot, modifiedPrice);
            NotifyPriceAdjustSuccessfully(itemMarketData.Value.Item.ItemID, itemMarketData.Value.Item.IsHQ,
                                          itemMarketData.Value.Price, modifiedPrice);
            return;

            // 采取意外情况逻辑
            void EnqueueAbortBehavior(AbortBehavior behavior)
            {
                if (ModuleConfig.SendPriceAdjustProcessMessage)
                {
                    var message = GetSLoc("AutoRetainerWork-PriceAdjust-ConductAbortBehavior",
                                          new SeStringBuilder().AddUiForeground(behavior.ToString(), 67).Build());
                    Chat(message);
                }

                if (behavior == AbortBehavior.无) return;

                switch (behavior)
                {
                    case AbortBehavior.改价至最小值:
                        SetRetainerMarketItemPrice(slot, (uint)itemConfig.PriceMinimum);
                        NotifyPriceAdjustSuccessfully(itemMarketData.Value.Item.ItemID, 
                                                      itemMarketData.Value.Item.IsHQ,
                                                      itemMarketData.Value.Price, 
                                                      (uint)itemConfig.PriceMinimum);
                        break;
                    case AbortBehavior.改价至预期值:
                        SetRetainerMarketItemPrice(slot, (uint)itemConfig.PriceExpected);
                        NotifyPriceAdjustSuccessfully(itemMarketData.Value.Item.ItemID, 
                                                      itemMarketData.Value.Item.IsHQ,
                                                      itemMarketData.Value.Price, 
                                                      (uint)itemConfig.PriceExpected);
                        break;
                    case AbortBehavior.改价至最高值:
                        SetRetainerMarketItemPrice(slot, (uint)itemConfig.PriceMaximum);
                        NotifyPriceAdjustSuccessfully(itemMarketData.Value.Item.ItemID, 
                                                      itemMarketData.Value.Item.IsHQ,
                                                      itemMarketData.Value.Price, 
                                                      (uint)itemConfig.PriceMaximum);
                        break;
                    case AbortBehavior.收回至雇员:
                        ReturnRetainerMarketItemToInventory(slot, false);
                        break;
                    case AbortBehavior.收回至背包:
                        ReturnRetainerMarketItemToInventory(slot, true);
                        break;
                    case AbortBehavior.出售至系统商店:
                        TaskHelper.Enqueue(() => ReturnRetainerMarketItemToInventory(slot, true), "将物品收回背包, 以待出售", weight: 3);
                        TaskHelper.Enqueue(() =>
                        {
                            if (!TrySearchItemInInventory(itemMarketData.Value.Item.ItemID, itemMarketData.Value.Item.IsHQ, out var foundItems) ||
                                foundItems is not { Count: > 0 })
                                return false;

                            var foundItem = foundItems.FirstOrDefault();
                            return OpenInventoryItemContext(foundItem);
                        }, "找到物品并打开其右键菜单", weight: 3);
                        TaskHelper.Enqueue(() => IsAddonAndNodesReady(InfosOm.ContextMenuXIV),          "等待右键菜单出现", weight: 3);
                        TaskHelper.Enqueue(() => ClickContextMenu(LuminaWrapper.GetAddonText(5480)), "出售物品至系统商店", weight: 3);
                        break;
                }
            }
        }

        private static ItemConfig GetItemConfigByItemKey(ItemKey key) =>
            ModuleConfig.ItemConfigs.TryGetValue(key.ToString(), out var itemConfig)
                ? itemConfig
                : ModuleConfig.ItemConfigs[new ItemKey(0, key.IsHQ).ToString()];

        #endregion

        #region 操作

        /// <summary>
        /// 将当前雇员市场售卖物品收回背包/雇员
        /// </summary>
        /// <param name="slot"></param>
        /// <param name="isInventory">若为 True 则为收回背包, 否则则为收回雇员背包</param>
        private static bool ReturnRetainerMarketItemToInventory(ushort slot, bool isInventory)
        {
            if (!RetainerThrottler.Throttle("ReturnMarketItemToInventory", 100)) return false;

            var manager = InventoryManager.Instance();
            if (manager == null) return false;

            var container = manager->GetInventoryContainer(InventoryType.RetainerMarket);
            if (container == null || !container->IsLoaded) return false;

            var inventoryItem = container->GetInventorySlot(slot);
            if (inventoryItem == null || inventoryItem->ItemId == 0) return true;

            if (isInventory)
                MoveFromRetainerMarketToPlayerInventory(manager, InventoryType.RetainerMarket, slot, (uint)inventoryItem->Quantity);
            else
                MoveFromRetainerMarketToRetainerInventory(manager, InventoryType.RetainerMarket, slot, (uint)inventoryItem->Quantity);
            return false;
        }

        /// <summary>
        /// 设定当前雇员市场售卖物品价格
        /// </summary>
        private static bool SetRetainerMarketItemPrice(ushort slot, uint price)
        {
            if (slot >= 20) return false;

            var manager = InventoryManager.Instance();
            if (manager == null) return false;

            manager->SetRetainerMarketPrice((short)slot, price);
            return true;
        }

        /// <summary>
        /// 上架物品至市场
        /// </summary>
        private static void UpshelfMarketItem(InventoryType srcType, ushort srcSlot, uint quantity, uint unitPrice, short targetSlot = -1)
        {
            if (targetSlot >= 20) return;
            ushort slot;
            if (targetSlot < 0)
            {
                if (!TryGetFirstEmptyRetainerMarketSlot(out slot)) return;
            }
            else
                slot = (ushort)targetSlot;

            var manager = InventoryManager.Instance();
            if (manager == null) return;

            MoveToRetainerMarketHook.Original(manager, srcType, srcSlot, InventoryType.RetainerMarket, slot, quantity, unitPrice);
        }

        /// <summary>
        /// 获取当前雇员市场售卖物品数据
        /// </summary>
        private static (ItemKey Item, uint Price)? GetRetainerMarketItem(ushort slot)
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
        /// 获取当前雇员市场售卖物品价格
        /// </summary>
        private static uint GetRetainerMarketPrice(ushort slot)
        {
            if (slot >= 20) return 0;

            var manager = InventoryManager.Instance();
            if (manager == null) return 0;

            return (uint)manager->GetRetainerMarketPrice((short)slot);
        }

        /// <summary>
        /// 获取当前市场物品数据
        /// </summary>
        private static void RequestMarketItemData(uint itemID)
        {
            var proxy = InfoProxyItemSearch.Instance();
            if (proxy == null) return;

            proxy->EndRequest();
            proxy->ClearListData();
            proxy->EntryCount = 0;

            proxy->SearchItemId = itemID;
            proxy->RequestData();
        }

        /// <summary>
        /// 当前市场物品数据是否已就绪
        /// </summary>
        private static bool IsMarketItemDataReady(uint itemID)
        {
            var proxy = InfoProxyItemSearch.Instance();
            if (proxy == null) return false;

            if (proxy->SearchItemId != itemID)
            {
                RequestMarketItemData(itemID);
                return false;
            }

            if (IsMarketStuck()) return false;

            if (proxy->Listings.ToArray()
                               .Where(x => x.ItemId == proxy->SearchItemId && x.UnitPrice != 0)
                               .ToList().Count != proxy->ListingCount)
                return false;
            
            return proxy->EntryCount switch
            {
                > 10 => proxy->ListingCount >= 10,
                0 => true,
                _ => proxy->ListingCount != 0
            };
        }

        /// <summary>
        /// 尝试获取雇员市场售卖列表中首个为空的槽位
        /// </summary>
        /// <returns></returns>
        private static bool TryGetFirstEmptyRetainerMarketSlot(out ushort slot)
        {
            slot = 0;
            var manager = InventoryManager.Instance();
            if (manager == null) return false;

            var container = manager->GetInventoryContainer(InventoryType.RetainerMarket);
            if (container == null || !container->IsLoaded) return false;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null || item->ItemId != 0) continue;

                slot = (ushort)i;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 是否满足任何意外情况
        /// </summary>
        /// <returns>正常/不需要修改价格为 False</returns>
        private static bool IsAnyAbortConditionsMet(
            ItemConfig         config,
            uint               origPrice,
            uint               modifiedPrice,
            uint               marketPrice,
            out AbortCondition conditionMet,
            out AbortBehavior  behaviorNeeded)
        {
            conditionMet   = AbortCondition.无;
            behaviorNeeded = AbortBehavior.无;

            // 检查每个条件
            foreach (var condition in PriceCheckConditions.GetAll())
            {
                if (config.AbortLogic.Keys.Any(x => x.HasFlag(condition.Condition)) &&
                    condition.Predicate(config, origPrice, modifiedPrice, marketPrice))
                {
                    conditionMet   = condition.Condition;
                    behaviorNeeded = config.AbortLogic.FirstOrDefault(x => x.Key.HasFlag(condition.Condition)).Value;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 获取修改后价格结果
        /// </summary>
        private static uint GetModifiedPrice(ItemConfig config, uint marketPrice) =>
            (uint)(config.AdjustBehavior switch
            {
                AdjustBehavior.固定值 => Math.Max(
                    0, marketPrice - config.AdjustValues[AdjustBehavior.固定值]),
                AdjustBehavior.百分比 => Math.Max(
                    0, marketPrice * (1 - (config.AdjustValues[AdjustBehavior.百分比] / 100))),
                _ => marketPrice
            });

        /// <summary>
        /// 发送改价成功通知信息
        /// </summary>
        private static void NotifyPriceAdjustSuccessfully(uint itemID, bool isHQ, uint origPrice, uint modifiedPrice)
        {
            if (!ModuleConfig.SendPriceAdjustProcessMessage) return;

            var itemPayload = new SeStringBuilder().AddItemLink(itemID, isHQ).Build();

            var priceChangedValue = (long)modifiedPrice - origPrice;
    
            var priceChangeText = FormatNumber(priceChangedValue);
            if (!priceChangeText.StartsWith('-'))
                priceChangeText = $"+{priceChangeText}";

            var priceChangeRate     = origPrice == 0 ? 0 : (double)priceChangedValue / origPrice * 100;
            var priceChangeRateText = priceChangeRate.ToString("+0.##;-0.##") + "%";

            Chat(GetSLoc("AutoRetainerWork-PriceAdjust-PriceAdjustSuccessfully",
                         itemPayload,
                         RetainerManager.Instance()->GetActiveRetainer()->NameString,
                         FormatNumber(origPrice),
                         FormatNumber(modifiedPrice),
                         priceChangeText,
                         priceChangeRateText));
        }

        /// <summary>
        /// 发送意外情况检测通知信息
        /// </summary>
        private static void NotifyAbortCondition(uint itemID, bool isHQ, AbortCondition condition)
        {
            if (!ModuleConfig.SendPriceAdjustProcessMessage) return;

            var itemPayload = new SeStringBuilder().AddItemLink(itemID, isHQ).Build();
            Chat(GetSLoc("AutoRetainerWork-PriceAdjust-DetectAbortCondition",
                                      itemPayload,
                                      RetainerManager.Instance()->GetActiveRetainer()->NameString,
                                      new SeStringBuilder().AddUiForeground(condition.ToString(), 60).Build()));
        }

        /// <summary>
        /// 获取当前雇员市场为同一物品的全部槽位
        /// </summary>
        private static bool TryGetSameItemSlots(uint itemID, out List<ushort> slots)
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

        /// <summary>
        /// 尝试获取物品最大可上架数量
        /// </summary>
        private static bool TryGetItemUpshelfCountLimit(InventoryItem item, out uint count)
        {
            count = 0;
            if (item.ItemId == 0) return false;
            
            if (!LuminaGetter.TryGetRow<Item>(item.ItemId, out var itemData)) return false;

            var itemKey    = new ItemKey(item.ItemId, item.Flags.HasFlag(InventoryItem.ItemFlags.HighQuality));
            var itemConfig = GetItemConfigByItemKey(itemKey);

            var itemStackSize     = itemData.StackSize;
            var defaultStackLimit = itemStackSize           == 9999 ? 9999U : 99U;
            var upshelfLimit      = itemConfig.UpshelfCount > 0 ? (uint)itemConfig.UpshelfCount : defaultStackLimit;
            
            count = (uint)Math.Min(item.Quantity, upshelfLimit);
            return true;
        }

        /// <summary>
        /// 根据语言格式化数字
        /// </summary>
        private static string FormatNumber(long number)
        {
            if (LanguageManager.CurrentLanguage is not ("ChineseSimplified" or "ChineseTraditional"))
                return number.ToString(CultureInfo.InvariantCulture);

            return FormatNumberByChineseNotation(number, LanguageManager.CurrentLanguage);
        }

        /// <summary>
        /// 当前市场是否正在重新请求
        /// </summary>
        /// <returns></returns>
        private static bool IsMarketStuck()
        {
            if (!ModuleManager.TryGetModuleByName("AutoRefreshMarketSearchResult", out var module) || module == null) return false;
            
            var type     = module.GetType();
            var property = type.GetProperty("IsMarketStuck", BindingFlags.Public | BindingFlags.Static);

            return property != null && (bool)property.GetValue(null);
        }

        #endregion

        public override void Uninit()
        {
            MoveToRetainerMarketHook?.Dispose();
            MoveToRetainerMarketHook = null;

            DService.AddonLifecycle.UnregisterListener(OnRetainerSell);
            DService.AddonLifecycle.UnregisterListener(OnRetainerSellList);

            WindowManager.Draw -= DrawMarketListWindow;
            IsNeedToDrawMarketListWindow = false;

            WindowManager.Draw -= DrawUpshelfWindow;
            IsNeedToDrawMarketUpshelfWindow = false;

            DService.MarketBoard.HistoryReceived -= OnHistoryReceived;
            DService.MarketBoard.OfferingsReceived -= OnOfferingReceived;

            TaskHelper?.Abort();
            TaskHelper?.Dispose();
            TaskHelper = null;

            PriceCacheManager.ClearCache();
        }

        public static class PriceCacheManager
        {
            private static readonly PriceCache CurrentPriceCache = new();
            private static readonly PriceCache HistoryPriceCache = new();

            private const int CacheExpirationMinutes = 10;

            private static class CacheKeys
            {
                public static string Create(uint itemID, bool isHQ) => $"{itemID}_{(isHQ ? "HQ" : "NQ")}";
            }

            public static void UpdateCache<T>(
                PriceCache     cache,
                uint           itemID,
                IEnumerable<T> listings,
                Func<T, bool>  isHQSelector,
                Func<T, bool>  onMannequinSelector,
                Func<T, uint>  priceSelector,
                Func<T, ulong> retainerSelector = null)
            {
                var filteredListings = listings
                                       .Where(x => !onMannequinSelector(x))
                                       .ToLookup(isHQSelector);

                foreach (var isHQ in new[] { false, true })
                {
                    var items = filteredListings[isHQ];
                    if (retainerSelector != null)
                        items = items.Where(x => !PlayerRetainers.Contains(retainerSelector(x)));

                    var enumerable = items as T[] ?? items.ToArray();
                    var minPrice   = enumerable.Length != 0 ? enumerable.Min(priceSelector) : 0;
                    if (minPrice <= 0) continue;

                    var cacheKey = CacheKeys.Create(itemID, isHQ);
                    if (!cache.TryGetPrice(cacheKey, out var currentPrice) || minPrice < currentPrice)
                        cache.SetPrice(cacheKey, minPrice);
                }
            }

            public static void UpdateHistoryCache<T>(
                PriceCache     cache,
                uint           itemID,
                IEnumerable<T> listings,
                Func<T, bool>  isHQSelector,
                Func<T, bool>  onMannequinSelector,
                Func<T, uint>  priceSelector)
            {
                var filteredListings = listings
                                       .Where(x => !onMannequinSelector(x))
                                       .ToLookup(isHQSelector);

                foreach (var isHQ in new[] { false, true })
                {
                    var items      = filteredListings[isHQ];
                    var enumerable = items as T[] ?? items.ToArray();
                    var maxPrice   = enumerable.Length != 0 ? enumerable.Max(priceSelector) : 0;
                    if (maxPrice <= 0) continue;

                    var cacheKey = CacheKeys.Create(itemID, isHQ);
                    if (!cache.TryGetPrice(cacheKey, out var currentPrice) || maxPrice > currentPrice)
                        cache.SetPrice(cacheKey, maxPrice);
                }
            }

            public static void OnOfferingReceived(IMarketBoardCurrentOfferings data)
            {
                if (!data.ItemListings.Any()) return;
                UpdateCache(
                    CurrentPriceCache,
                    data.ItemListings[0].ItemId,
                    data.ItemListings,
                    x => x.IsHq,
                    x => x.OnMannequin,
                    x => x.PricePerUnit,
                    x => x.RetainerId
                );
            }

            public static void OnHistoryReceived(IMarketBoardHistory history)
            {
                if (!history.HistoryListings.Any()) return;
                UpdateHistoryCache(
                    HistoryPriceCache,
                    history.ItemId,
                    history.HistoryListings,
                    x => x.IsHq,
                    x => x.OnMannequin,
                    x => x.SalePrice
                );
            }

            public static bool TryGetPriceCache(uint itemID, bool isHQ, out uint price)
            {
                price = 0;
                var cacheKey = CacheKeys.Create(itemID, isHQ);
                var oppositeCacheKey = CacheKeys.Create(itemID, !isHQ);

                // 清理过期缓存
                CurrentPriceCache.RemoveExpiredEntries(TimeSpan.FromMinutes(CacheExpirationMinutes));
                HistoryPriceCache.RemoveExpiredEntries(TimeSpan.FromMinutes(CacheExpirationMinutes));

                // 按优先级尝试获取价格
                return (CurrentPriceCache.TryGetPrice(cacheKey, out price) ||
                        CurrentPriceCache.TryGetPrice(oppositeCacheKey, out price) ||
                        HistoryPriceCache.TryGetPrice(cacheKey, out price) ||
                        HistoryPriceCache.TryGetPrice(oppositeCacheKey, out price)) &&
                       price != 0;
            }

            public static (DateTime Current, DateTime History) GetCacheTimes()
                => (CurrentPriceCache.LastUpdateTime, HistoryPriceCache.LastUpdateTime);

            public static void ClearCache(bool clearCurrent = true, bool clearHistory = true)
            {
                if (clearCurrent)
                    CurrentPriceCache.Clear();
                if (clearHistory)
                    HistoryPriceCache.Clear();
            }
        }

        public sealed class PriceCache
        {
            private readonly Dictionary<string, CacheEntry> data = [];

            private class CacheEntry
            {
                public uint Price { get; init; }
                public DateTime LastUpdateTime { get; init; }
            }

            public DateTime LastUpdateTime { get; private set; } = DateTime.MinValue;

            public void RemoveExpiredEntries(TimeSpan expirationTime)
            {
                var now = DateTime.Now;
                var expiredKeys = data
                    .Where(kvp => now - kvp.Value.LastUpdateTime > expirationTime)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in expiredKeys)
                    data.Remove(key);

                if (!data.Any())
                    LastUpdateTime = DateTime.MinValue;
            }

            public bool TryGetPrice(string key, out uint price)
            {
                price = 0;
                if (data.TryGetValue(key, out var entry))
                {
                    price = entry.Price;
                    return true;
                }

                return false;
            }

            public void SetPrice(string key, uint price)
            {
                data[key] = new CacheEntry
                {
                    Price = price,
                    LastUpdateTime = DateTime.Now
                };
                LastUpdateTime = DateTime.Now;
            }

            public void Clear()
            {
                data.Clear();
                LastUpdateTime = DateTime.MinValue;
            }
        }
    }
}
