using System.Collections.Frozen;
using System.Numerics;
using System.Reflection;
using DailyRoutines.Extensions;
using DailyRoutines.Internal;
using DailyRoutines.Manager;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Network.Structures;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.Info.Algorithms;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading.TaskHelper;
using Action = System.Action;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class AutoRetainerWork
{
    private class PriceAdjustWorker
    (
        AutoRetainerWork module
    ) : RetainerWorkerBase(module)
    {
        private Hook<MoveToRetainerMarketDelegate>? MoveToRetainerMarketHook;

        private static readonly List<string> SellInventoryItemsText =
        [
            "玩家所持物品",
            "Sell items in your inventory",
            "プレイヤー所持品から"
        ];

        private          TaskHelper?     taskHelper;
        private readonly ItemSelectCombo itemSelectCombo = new("AddNewItem");

        private          ItemConfig?    selectedItemConfig;
        private readonly Vector2        childSizeLeft     = ScaledVector2(200, 400);
        private          Vector2        childSizeRight    = ScaledVector2(450, 400);
        private          string         presetSearchInput = string.Empty;
        private          bool           newConfigItemHQ;
        private          AbortCondition conditionInput = AbortCondition.低于最小值;
        private          AbortBehavior  behaviorInput  = AbortBehavior.无;
        private          uint           itemModifyUnitPriceManual;
        private          uint           itemModifyCountManual;
        private          Vector2        marketDataTableImageSize = new Vector2(32) * GlobalUIScale;
        private          Vector2        manualUnitPriceImageSize = new Vector2(32) * GlobalUIScale;

        private KeyValuePair<uint, List<IMarketBoardHistoryListing>> historyListings;

        private bool          isNeedToDrawMarketListWindow;
        private bool          isNeedToDrawMarketUpshelfWindow;
        private InventoryType sourceUpshelfType;
        private ushort        sourceUpshelfSlot;
        private uint          upshelfUnitPriceInput;
        private uint          upshelfQuantityInput;
        private bool          isDisplayingTooltip;

        public override bool IsWorkerBusy() => taskHelper?.IsBusy ?? false;

        public override void Init()
        {
            MoveToRetainerMarketHook ??= DService.Instance().Hook.HookFromMemberFunction
            (
                typeof(InventoryManager.MemberFunctionPointers),
                "MoveToRetainerMarket",
                (MoveToRetainerMarketDelegate)MoveToRetainerMarketDetour
            );
            MoveToRetainerMarketHook.Enable();
            
            taskHelper ??= new() { TimeoutMS = 30_000, ShowDebug = true };

            DService.Instance().MarketBoard.HistoryReceived   += OnHistoryReceived;
            DService.Instance().MarketBoard.OfferingsReceived += OnOfferingReceived;

            DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "RetainerSell",     OnRetainerSell);
            DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "RetainerSellList", OnRetainerSellList);
            DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "RetainerSellList", OnRetainerSellList);

            WindowManager.Instance().PostDraw += DrawMarketListWindow;
            WindowManager.Instance().PostDraw += DrawUpshelfWindow;
        }

        public override void DrawConfig()
        {
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("AutoRetainerWork-PriceAdjust-Title"));

            ItemConfigSelector();

            ImGui.SameLine();
            ItemConfigEditor();
        }

        public override TreeListCategoryNode CreateOverlayCategory(float width) =>
            CreateOverlayCategory
            (
                Lang.Get("AutoRetainerWork-PriceAdjust-Title"),
                width,
                CreateOverlayText(Lang.Get("AutoRetainerWork-PriceAdjust-AdjustForRetainers"), width),
                CreateOverlayButtonRow
                (
                    () =>
                    {
                        if (taskHelper is not { IsBusy: false }) return;
                        EnqueuePriceAdjustAll();
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

        private void DrawMarketListWindow()
        {
            if (!isNeedToDrawMarketListWindow) return;

            if (!RetainerSellList->IsAddonAndNodesReady())
            {
                isNeedToDrawMarketListWindow = false;
                return;
            }

            var addon = RetainerSellList;
            if (addon == null) return;

            var size      = new Vector2(addon->GetScaledWidth(true), addon->GetScaledHeight(true));
            var windowPos = default(Vector2);

            ImGui.SetNextWindowSize(size);

            if (ImGui.Begin
                (
                    "改价窗口##AutoRetainerWork-PriceAdjustWorker",
                    ImGuiWindowFlags.NoTitleBar  |
                    ImGuiWindowFlags.NoResize    |
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.MenuBar
                ))
            {
                windowPos = ImGui.GetWindowPos();
                DrawMarketItemsTable();
                ImGui.End();
            }

            if (addon->X != (short)windowPos.X || addon->Y != (short)windowPos.Y)
                addon->SetPosition((short)windowPos.X, (short)windowPos.Y);

            if (InfoProxyItemSearch.Instance()->SearchItemId == 0) return;

            ImGui.SetNextWindowSizeConstraints(new(200, 300), new(float.MaxValue));

            if (ImGui.Begin("市场数据窗口##AutoRetainerWork-PriceAdjustWorker", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar))
            {
                DrawMarketDataTable();

                if (historyListings.Key != 0 && historyListings.Value.Count > 0)
                {
                    ImGui.NewLine();

                    DrawMarketHistoryDataTable();
                }

                ImGui.End();
            }
        }

        private void DrawUpshelfWindow()
        {
            if (!isNeedToDrawMarketUpshelfWindow) return;

            if (!RetainerSellList->IsAddonAndNodesReady())
            {
                isNeedToDrawMarketUpshelfWindow = false;
                return;
            }

            if (ImGui.Begin("上架窗口##AutoRetainerWork-PriceAdjustWorker", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
            {
                DrawMarketUpshelf();
                ImGui.End();
            }
        }

        public override void Uninit()
        {
            MoveToRetainerMarketHook?.Dispose();
            MoveToRetainerMarketHook = null;

            DService.Instance().AddonLifecycle.UnregisterListener(OnRetainerSell);
            DService.Instance().AddonLifecycle.UnregisterListener(OnRetainerSellList);

            WindowManager.Instance().PostDraw -= DrawMarketListWindow;
            isNeedToDrawMarketListWindow      =  false;

            WindowManager.Instance().PostDraw -= DrawUpshelfWindow;
            isNeedToDrawMarketUpshelfWindow   =  false;

            DService.Instance().MarketBoard.HistoryReceived   -= OnHistoryReceived;
            DService.Instance().MarketBoard.OfferingsReceived -= OnOfferingReceived;

            taskHelper?.Abort();
            taskHelper?.Dispose();
            taskHelper = null;

            PriceCacheManager.ClearCache();
        }

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

        public static class PriceCacheManager
        {
            private const           int        CACHE_EXPIRATION_MINUTES = 10;
            private static readonly PriceCache CurrentPriceCache        = new();
            private static readonly PriceCache HistoryPriceCache        = new();

            public static void UpdateCache<T>
            (
                AutoRetainerWork  module,
                PriceCache        cache,
                uint              itemID,
                IEnumerable<T>    listings,
                Func<T, bool>     isHQSelector,
                Func<T, bool>     onMannequinSelector,
                Func<T, uint>     priceSelector,
                Func<T, ulong>    retainerSelector = null
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

                    var enumerable = items as T[] ?? items.ToArray();
                    var minPrice   = enumerable.Length != 0 ? enumerable.Min(priceSelector) : 0;
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
                    var maxPrice   = enumerable.Length != 0 ? enumerable.Max(priceSelector) : 0;
                    if (maxPrice <= 0) continue;

                    var cacheKey = CacheKeys.Create(itemID, isHQ);
                    if (!cache.TryGetPrice(cacheKey, out var currentPrice) || maxPrice > currentPrice)
                        cache.SetPrice(cacheKey, maxPrice);
                }
            }

            public static void OnOfferingReceived(AutoRetainerWork module, IMarketBoardCurrentOfferings data)
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

            public static void OnHistoryReceived(IMarketBoardHistory history)
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

            public static bool TryGetPriceCache(uint itemID, bool isHQ, out uint price)
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

            public static void ClearCache(bool clearCurrent = true, bool clearHistory = true)
            {
                if (clearCurrent)
                    CurrentPriceCache.Clear();
                if (clearHistory)
                    HistoryPriceCache.Clear();
            }

            private static class CacheKeys
            {
                public static string Create(uint itemID, bool isHQ) => $"{itemID}_{(isHQ ? "HQ" : "NQ")}";
            }
        }

        public sealed class PriceCache
        {
            private readonly Dictionary<string, CacheEntry> data = [];

            public DateTime LastUpdateTime { get; private set; } = DateTime.MinValue;

            public void RemoveExpiredEntries(TimeSpan expirationTime)
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

        private void AddNewConfigItemPopup(Action confirmAction)
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

            var itemName = selectedItemConfig.ItemID == 0
                               ? Lang.Get("AutoRetainerWork-PriceAdjust-CommonItemPreset")
                               : item.Name.ToString() ?? string.Empty;

            var itemLogo = DService.Instance().Texture
                                   .GetFromGameIcon(new(selectedItemConfig.ItemID == 0 ? 65002 : (uint)item.Icon, selectedItemConfig.IsHQ))
                                   .GetWrapOrDefault();
            if (itemLogo == null) return;

            var itemBuyingPrice = selectedItemConfig.ItemID == 0 ? 1 : item.PriceLow;

            if (!child) return;

            // 物品基本信息展示
            ImGui.Image(itemLogo.Handle, ScaledVector2(48f));

            ImGui.SameLine();

            using (FontManager.Instance().UIFont140.Push())
                ImGui.TextUnformatted(itemName);

            ImGui.SameLine();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 6f * GlobalUIScale);
            ImGui.TextUnformatted(selectedItemConfig.IsHQ ? $"({Lang.Get("HQ")})" : string.Empty);

            ImGui.Separator();

            // 改价逻辑配置
            using (ImRaii.Group())
            {
                foreach (AdjustBehavior behavior in Enum.GetValues<AdjustBehavior>())
                {
                    if (ImGui.RadioButton(behavior.ToString(), behavior == selectedItemConfig.AdjustBehavior))
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

                using (var combo = ImRaii.Combo("###AddNewLogicConditionCombo", conditionInput.ToString(), ImGuiComboFlags.HeightLarge))
                {
                    if (combo)
                    {
                        foreach (AbortCondition condition in Enum.GetValues(typeof(AbortCondition)))
                        {
                            if (condition == AbortCondition.无) continue;

                            if (ImGui.Selectable(condition.ToString(), conditionInput.HasFlag(condition), ImGuiSelectableFlags.DontClosePopups))
                            {
                                var combinedCondition = conditionInput;
                                if (conditionInput.HasFlag(condition))
                                    combinedCondition &= ~condition;
                                else
                                    combinedCondition |= condition;

                                conditionInput = combinedCondition;
                            }
                        }
                    }
                }

                ImGui.SetNextItemWidth(250f * GlobalUIScale);

                using (var combo = ImRaii.Combo("###AddNewLogicBehaviorCombo", behaviorInput.ToString(), ImGuiComboFlags.HeightLarge))
                {
                    if (combo)
                    {
                        foreach (AbortBehavior behavior in Enum.GetValues(typeof(AbortBehavior)))
                        {
                            if (ImGui.Selectable(behavior.ToString(), behaviorInput == behavior, ImGuiSelectableFlags.DontClosePopups))
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
                var origConditionStr = logic.Key.ToString();
                ImGui.SetNextItemWidth(300f * GlobalUIScale);
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
                var origBehaviorStr = logic.Value.ToString();
                ImGui.SameLine();
                ImGui.SetNextItemWidth(300f * GlobalUIScale);
                ImGui.InputText($"###Behavior_{origBehaviorStr}", ref origBehaviorStr, 128, ImGuiInputTextFlags.ReadOnly);

                if (ImGui.IsItemClicked())
                    ImGui.OpenPopup($"###BehaviorSelectPopup_{origBehaviorStr}");

                using (var popup = ImRaii.Popup($"###BehaviorSelectPopup_{origBehaviorStr}"))
                {
                    if (popup)
                    {
                        foreach (AbortBehavior behavior in Enum.GetValues<AbortBehavior>())
                        {
                            if (ImGui.Selectable(behavior.ToString(), behavior == logic.Value))
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

        private void DrawMarketItemsTable()
        {
            var retainerManager = RetainerManager.Instance();
            if (retainerManager == null) return;

            var currentActiveRetainer = retainerManager->GetActiveRetainer();
            if (currentActiveRetainer == null) return;

            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null) return;

            var marketContainer = inventoryManager->GetInventoryContainer(InventoryType.RetainerMarket);
            if (marketContainer == null || !marketContainer->IsLoaded) return;

            using var font = FontManager.Instance().GetUIFont(Module.config.MarketItemsWindowFontScale).Push();

            if (ImGui.BeginMenuBar())
            {
                ImGui.TextUnformatted($"{Lang.Get("AutoRetainerWork-PriceAdjust-Adjust")}:");

                using (ImRaii.Disabled(taskHelper.IsBusy))
                {
                    if (ImGui.MenuItem(Lang.Get("Start")))
                        EnqueuePriceAdjustSingle();
                }

                if (ImGui.MenuItem(Lang.Get("Stop")))
                    taskHelper.Abort();

                ImGui.TextDisabled("|");

                using (ImRaii.Disabled(taskHelper.IsBusy))
                {
                    if (ImGui.BeginMenu(Lang.Get("Shortcut")))
                    {
                        if (ImGui.MenuItem(Lang.Get("AutoRetainerWork-PriceAdjust-ReturnAllToInventory")))
                        {
                            for (var i = 0; i < marketContainer->Size; i++)
                            {
                                var index = i;
                                taskHelper.Enqueue(() => ReturnRetainerMarketItemToInventory((ushort)index, true), $"将市场第 {index} 栏物品收回至背包");
                            }
                        }

                        if (ImGui.MenuItem(Lang.Get("AutoRetainerWork-PriceAdjust-ReturnAllToRetainer")))
                        {
                            for (var i = 0; i < marketContainer->Size; i++)
                            {
                                var index = i;
                                taskHelper.Enqueue(() => ReturnRetainerMarketItemToInventory((ushort)index, false), $"将市场第 {index} 栏物品收回至雇员");
                            }
                        }

                        ImGui.EndMenu();
                    }
                }

                ImGui.TextDisabled("|");

                using (ImRaii.Disabled(taskHelper.IsBusy))
                {
                    if (ImGui.MenuItem(Lang.Get("AutoRetainerWork-PriceAdjust-ClearCache")))
                    {
                        PriceCacheManager.ClearCache();
                        NotifyHelper.Instance().NotificationSuccess(Lang.Get("AutoRetainerWork-PriceAdjust-CacheCleared"));
                    }
                }

                ImGui.TextDisabled("|");

                if (ImGui.BeginMenu(Lang.Get("Settings")))
                {
                    if (ImGui.BeginMenu(Lang.Get("FontSize")))
                    {
                        for (var i = 0.6f; i < 1.8f; i += 0.2f)
                        {
                            var fontScale = (float)Math.Round(i, 1);

                            if (ImGui.MenuItem
                                (
                                    $"{fontScale}",
                                    string.Empty,
                                    fontScale == Module.config.MarketItemsWindowFontScale
                                ))
                            {
                                Module.config.MarketItemsWindowFontScale = fontScale;
                                Module.config.Save(Module);
                            }
                        }

                        ImGui.EndMenu();
                    }

                    if (ImGui.BeginMenu(Lang.Get("AutoRetainerWork-PriceAdjust-SortOrder")))
                    {
                        foreach (var sortOrder in Enum.GetValues<SortOrder>())
                        {
                            if (ImGui.MenuItem
                                (
                                    $"{sortOrder}",
                                    string.Empty,
                                    sortOrder == Module.config.MarketItemsSortOrder
                                ))
                            {
                                Module.config.MarketItemsSortOrder = sortOrder;
                                Module.config.Save(Module);
                            }
                        }

                        ImGui.EndMenu();
                    }

                    if (ImGui.MenuItem
                        (
                            Lang.Get("AutoRetainerWork-PriceAdjust-AutoAdjustWhenNewOnSale"),
                            string.Empty,
                            Module.config.AutoPriceAdjustWhenNewOnSale
                        ))
                    {
                        Module.config.AutoPriceAdjustWhenNewOnSale ^= true;
                        Module.config.Save(Module);
                    }

                    if (ImGui.MenuItem
                        (
                            Lang.Get("AutoRetainerWork-PriceAdjust-SendProcessMessage"),
                            string.Empty,
                            Module.config.SendPriceAdjustProcessMessage
                        ))
                    {
                        Module.config.SendPriceAdjustProcessMessage ^= true;
                        Module.config.Save(Module);
                    }

                    ImGui.EndMenu();
                }

                ImGui.TextDisabled("|");

                using (ImRaii.Disabled(taskHelper.IsBusy))
                {
                    if (ImGui.MenuItem(LuminaWrapper.GetAddonText(2366)))
                        RetainerSellList->Callback(-1);
                }

                ImGui.EndMenuBar();
            }

            using var disabled = ImRaii.Disabled(taskHelper.IsBusy);
            using var table = ImRaii.Table
            (
                "MarketItemTable",
                5,
                ImGuiTableFlags.Borders     |
                ImGuiTableFlags.Reorderable |
                ImGuiTableFlags.Resizable   |
                ImGuiTableFlags.Hideable
            );
            if (!table) return;

            ImGui.TableSetupColumn("###Sort",                        ImGuiTableColumnFlags.WidthFixed,   ImGui.GetTextLineHeightWithSpacing());
            ImGui.TableSetupColumn(Lang.Get("Item"),                 ImGuiTableColumnFlags.WidthStretch, 30);
            ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(933),  ImGuiTableColumnFlags.WidthStretch, 10);
            ImGui.TableSetupColumn(Lang.Get("Amount"),               ImGuiTableColumnFlags.WidthFixed,   ImGui.CalcTextSize(Lang.Get("Amount")).X * 1.2f);
            ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(6936), ImGuiTableColumnFlags.WidthStretch, 10);

            ImGui.TableHeadersRow();

            if (!InventoryType.RetainerMarket.TryGetItems(x => x.ItemId != 0, out var validItems)) return;

            var itemSource = validItems
                             .Select
                             (x => new
                                 {
                                     Inventory = x,
                                     Data      = LuminaGetter.GetRow<Item>(x.ItemId).GetValueOrDefault(),
                                     Slot      = (ushort)x.Slot
                                 }
                             )
                             .OrderBy
                             (x => Module.config.MarketItemsSortOrder switch
                                 {
                                     SortOrder.上架顺序 => (uint)x.Inventory.Slot,
                                     SortOrder.物品ID => x.Data.RowId,
                                     SortOrder.物品类型 => x.Data.FilterGroup,
                                     _              => 0U
                                 }
                             )
                             .ThenBy
                             (x => Module.config.MarketItemsSortOrder switch
                                 {
                                     SortOrder.物品ID => x.Data.RowId,
                                     _              => 0U
                                 }
                             )
                             .ToArray();

            var isTooltip     = false;
            var tooltipItemID = 0U;

            for (var index = 0; index < itemSource.Length; index++)
            {
                var item      = itemSource[index];
                var itemPrice = GetRetainerMarketPrice(item.Slot);
                if (itemPrice == 0) continue;

                var isItemHQ = item.Inventory.Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                var itemIcon = DService.Instance().Texture.GetFromGameIcon(new(item.Data.Icon, isItemHQ)).GetWrapOrDefault();
                if (itemIcon == null) continue;

                var itemName = $"{item.Data.Name.ToString()}" + (isItemHQ ? "\ue03c" : string.Empty);

                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{index + 1}");

                DrawItemColumn(item.Slot, item.Inventory.ItemId, itemName, itemIcon, ref isTooltip, ref tooltipItemID);

                DrawUnitPriceColumn(item.Slot, item.Inventory.ItemId, itemPrice, (uint)item.Inventory.Quantity, itemIcon, itemName);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{item.Inventory.Quantity}");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{(item.Inventory.Quantity * itemPrice).ToChineseString()}");
            }

            if (isTooltip)
            {
                AtkStage.Instance()->ShowItemTooltip(ScreenText->RootNode, tooltipItemID);
                isDisplayingTooltip = true;
            }
            else
            {
                if (isDisplayingTooltip)
                {
                    isDisplayingTooltip = false;
                    AtkStage.Instance()->HideTooltip(ScreenText->Id);
                }
            }
        }

        private void DrawItemColumn(ushort slot, uint itemID, string itemName, IDalamudTextureWrap itemIcon, ref bool isTooltip, ref uint tooltipItemID)
        {
            using var id    = ImRaii.PushId(slot);
            using var group = ImRaii.Group();

            ImGui.TableNextColumn();
            ImGuiOm.SelectableImageWithText(itemIcon.Handle, new(ImGui.GetTextLineHeightWithSpacing()), itemName, false);

            if (ImGui.IsItemHovered())
            {
                isTooltip     = true;
                tooltipItemID = itemID;
            }

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

        private void DrawUnitPriceColumn(ushort slot, uint itemID, uint price, uint quantity, IDalamudTextureWrap itemIcon, string itemName)
        {
            using var id    = ImRaii.PushId(slot);
            using var group = ImRaii.Group();

            ImGui.TableNextColumn();
            ImGui.Selectable($"{price.ToChineseString()}");

            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            var isNeedOpenManualModifyPopup    = false;
            var isNeedOpenAllManualModifyPopup = false;

            using (var popup = ImRaii.ContextPopupItem("ModifyUnitPricePopup"))
            {
                if (popup)
                {
                    if (ImGui.MenuItem(Lang.Get("AutoRetainerWork-PriceAdjust-AdjustUnitPriceAuto")))
                        EnqueuePriceAdjustSingle(slot);

                    if (ImGui.MenuItem(Lang.Get("AutoRetainerWork-PriceAdjust-AdjustUnitPriceManual")))
                    {
                        ImGui.CloseCurrentPopup();

                        RequestMarketItemData(itemID);
                        isNeedOpenManualModifyPopup = true;
                    }

                    using (ImRaii.Group())
                    {
                        if (ImGui.MenuItem(Lang.Get("AutoRetainerWork-PriceAdjust-AdjustUnitPriceAllSameItems")))
                        {
                            if (TryGetSameItemSlots(itemID, out var slots))
                                slots.ForEach(s => EnqueuePriceAdjustSingle(s));
                        }

                        if (ImGui.MenuItem(Lang.Get("AutoRetainerWork-PriceAdjust-AdjustUnitPriceAllSameItemsManual")))
                        {
                            ImGui.CloseCurrentPopup();

                            RequestMarketItemData(itemID);
                            isNeedOpenAllManualModifyPopup = true;
                        }
                    }

                    ImGuiOm.TooltipHover(Lang.Get("AutoRetainerWork-PriceAdjust-AdjustUnitPriceAllSameItemsHelp"));
                }
            }

            if (isNeedOpenManualModifyPopup)
                ImGui.OpenPopup("ModifyUnitPriceManualPopup");

            using (var popup = ImRaii.Popup("ModifyUnitPriceManualPopup"))
            {
                if (popup)
                {
                    if (ImGui.IsWindowAppearing())
                        itemModifyUnitPriceManual = price;

                    ImGui.Image(itemIcon.Handle, manualUnitPriceImageSize with { X = manualUnitPriceImageSize.Y });

                    ImGui.SameLine();

                    using (ImRaii.Group())
                    {
                        using (FontManager.Instance().UIFont140.Push())
                            ImGui.TextUnformatted($"{itemName}");

                        ImGui.TextDisabled($"{Lang.Get("AutoRetainerWork-PriceAdjust-MarketItemsCount")}: {quantity}");
                    }

                    manualUnitPriceImageSize = ImGui.GetItemRectSize();

                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{LuminaWrapper.GetAddonText(933)}:");

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(150f * GlobalUIScale);
                    ImGui.InputUInt("###UnitPriceInput", ref itemModifyUnitPriceManual);

                    ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{LuminaWrapper.GetAddonText(6936)}:");

                    ImGui.SameLine();
                    ImGui.TextUnformatted($"{(quantity * itemModifyUnitPriceManual).ToChineseString()}");

                    ImGui.Separator();

                    if (ImGuiOm.ButtonSelectable(Lang.Get("Confirm")))
                    {
                        SetRetainerMarketItemPrice(slot, itemModifyUnitPriceManual);
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
                        itemModifyUnitPriceManual = price;
                        itemModifyCountManual     = (uint)(TryGetSameItemSlots(itemID, out var slots) ? slots.Count : 0);
                    }

                    ImGui.Image(itemIcon.Handle, manualUnitPriceImageSize with { X = manualUnitPriceImageSize.Y });

                    ImGui.SameLine();

                    using (ImRaii.Group())
                    {
                        using (FontManager.Instance().UIFont140.Push())
                            ImGui.TextUnformatted($"{itemName}");

                        ImGui.TextDisabled($"{Lang.Get("AutoRetainerWork-PriceAdjust-MarketItemsCount")}: {quantity}");

                        ImGui.SameLine();
                        ImGui.TextDisabled("/");

                        ImGui.SameLine();
                        ImGui.TextDisabled($"{Lang.Get("AutoRetainerWork-PriceAdjust-SameItemsCount")}: {itemModifyCountManual}");
                    }

                    manualUnitPriceImageSize = ImGui.GetItemRectSize();

                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{LuminaWrapper.GetAddonText(933)}:");

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(150f * GlobalUIScale);
                    ImGui.InputUInt("###UnitPriceInput", ref itemModifyUnitPriceManual);

                    ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{LuminaWrapper.GetAddonText(6936)}:");

                    ImGui.SameLine();
                    ImGui.TextUnformatted($"{(quantity * itemModifyUnitPriceManual).ToChineseString()}");

                    ImGui.Separator();

                    if (ImGuiOm.ButtonSelectable(Lang.Get("Confirm")))
                    {
                        if (TryGetSameItemSlots(itemID, out var slots))
                            slots.ForEach(s => EnqueuePriceAdjustSingle(s, itemModifyUnitPriceManual));

                        ImGui.CloseCurrentPopup();
                    }
                }
            }
        }

        private void DrawMarketDataTable()
        {
            var info = InfoProxyItemSearch.Instance();
            if (info == null) return;

            if (info->SearchItemId == 0) return;

            var listingsArray = info->Listings.ToArray()
                                              .Where
                                              (x => x.ItemId    == info->SearchItemId &&
                                                    x.UnitPrice != 0                  &&
                                                    !Module.playerRetainers.Contains(x.RetainerId)
                                              )
                                              .OrderBy(x => x.UnitPrice)
                                              .ToArray();

            if (!LuminaGetter.TryGetRow<Item>(info->SearchItemId, out var itemData)) return;

            var itemIcon = DService.Instance().Texture.GetFromGameIcon(new(itemData.Icon)).GetWrapOrDefault();
            if (itemIcon == null) return;

            using var font = FontManager.Instance().UIFont.Push();

            ImGui.Image(itemIcon.Handle, marketDataTableImageSize with { X = marketDataTableImageSize.Y });

            ImGui.SameLine();

            using (ImRaii.Group())
            {
                using (FontManager.Instance().UIFont160.Push())
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextUnformatted($"{itemData.Name}");
                }

                using (FontManager.Instance().UIFont.Push())
                {
                    ImGui.TextDisabled($"{Lang.Get("AutoRetainerWork-PriceAdjust-OnSaleCount")}: {info->ListingCount}");

                    if (listingsArray.Length > 0)
                    {
                        var minPrice = listingsArray.Min(x => x.UnitPrice);
                        ImGui.SameLine();
                        ImGui.TextDisabled($" / {Lang.Get("AutoRetainerWork-PriceAdjust-MinPrice")}: {minPrice.ToChineseString()} / ");
                        ImGuiOm.ClickToCopyAndNotify(minPrice.ToString());

                        var maxPrice = listingsArray.Max(x => x.UnitPrice);
                        ImGui.SameLine();
                        ImGui.TextDisabled($"{Lang.Get("AutoRetainerWork-PriceAdjust-MaxPrice")}: {maxPrice.ToChineseString()}");
                        ImGuiOm.ClickToCopyAndNotify(maxPrice.ToString());
                    }
                }
            }

            marketDataTableImageSize = ImGui.GetItemRectSize();

            var       childSize = new Vector2(ImGui.GetContentRegionAvail().X, 250f * GlobalUIScale);
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
                ImGui.TableSetupColumn(Lang.Get("Mannequin"), ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize(Lang.Get("Mannequin")).X);

            ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(357),  ImGuiTableColumnFlags.WidthStretch, 15);
            ImGui.TableSetupColumn(Lang.Get("Amount"),               ImGuiTableColumnFlags.WidthFixed,   ImGui.CalcTextSize(Lang.Get("Amount")).X);
            ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(6936), ImGuiTableColumnFlags.WidthStretch, 15);

            ImGui.TableHeadersRow();

            foreach (var listing in listingsArray)
            {
                using var id = ImRaii.PushId(listing.ListingId.ToString());
                ImGui.TableNextRow();

                if (isAnyHQ)
                {
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(listing.IsHqItem ? "√" : string.Empty);
                }

                if (isAnyMateriaEquipped)
                {
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{listing.MateriaCount}");
                }

                if (isAnyOnMannequin)
                {
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(listing.IsMannequin ? "√" : string.Empty);
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{listing.UnitPrice.ToChineseString()}");
                ImGuiOm.ClickToCopyAndNotify(listing.UnitPrice.ToString());

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{listing.Quantity}");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{(listing.UnitPrice * listing.Quantity + listing.TotalTax).ToChineseString()}");
            }
        }

        private void DrawMarketHistoryDataTable()
        {
            var info = InfoProxyItemSearch.Instance();
            if (info == null) return;

            if (historyListings.Key == 0) return;
            if (!LuminaGetter.TryGetRow<Item>(historyListings.Key, out _)) return;

            using var font = FontManager.Instance().UIFont.Push();

            using (ImRaii.Group())
            {
                using (FontManager.Instance().UIFont160.Push())
                    ImGui.TextUnformatted($"{LuminaWrapper.GetAddonText(1165)}");

                ImGui.TextDisabled($"{Lang.Get("AutoRetainerWork-PriceAdjust-OnSaleCount")}: {info->ListingCount}");

                if (historyListings.Value.Count > 0)
                {
                    var minPrice = historyListings.Value.Min(x => x.SalePrice);
                    ImGui.SameLine();
                    ImGui.TextDisabled($" / {Lang.Get("AutoRetainerWork-PriceAdjust-MinPrice")}: {minPrice.ToChineseString()} / ");
                    ImGuiOm.ClickToCopyAndNotify(minPrice.ToString());

                    var maxPrice = historyListings.Value.Max(x => x.SalePrice);
                    ImGui.SameLine();
                    ImGui.TextDisabled($"{Lang.Get("AutoRetainerWork-PriceAdjust-MaxPrice")}: {maxPrice.ToChineseString()}");
                    ImGuiOm.ClickToCopyAndNotify(maxPrice.ToString());
                }
            }

            var       childSize = new Vector2(ImGui.GetContentRegionAvail().X, 250f * GlobalUIScale);
            using var child     = ImRaii.Child("HistoryDataChild", childSize, false, ImGuiWindowFlags.NoBackground);
            if (!child) return;

            var isAnyHQ = historyListings.Value.Any(x => x.IsHq);

            var columnsCount = 5;
            if (!isAnyHQ)
                columnsCount--;

            using var table = ImRaii.Table("MarketBoardDataTable", columnsCount, ImGuiTableFlags.Borders);
            if (!table) return;

            if (isAnyHQ)
                ImGui.TableSetupColumn("\ue03c", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("\ue03c").X);

            ImGui.TableSetupColumn(Lang.Get("Amount"),               ImGuiTableColumnFlags.WidthFixed,   ImGui.CalcTextSize(Lang.Get("Amount")).X);
            ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(357),  ImGuiTableColumnFlags.WidthStretch, 15);
            ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(1975), ImGuiTableColumnFlags.WidthStretch, 15);
            ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(1976), ImGuiTableColumnFlags.WidthStretch, 15);

            ImGui.TableHeadersRow();

            foreach (var listing in historyListings.Value)
            {
                if (listing.OnMannequin) continue;

                using var id = ImRaii.PushId($"{listing.BuyerName}-{listing.SalePrice}-{listing.Quantity}-{listing.PurchaseTime}");
                ImGui.TableNextRow();

                if (isAnyHQ)
                {
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(listing.IsHq ? "√" : string.Empty);
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{listing.Quantity}");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{listing.SalePrice.ToChineseString()}");
                ImGuiOm.ClickToCopyAndNotify(listing.SalePrice.ToString());

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{listing.BuyerName}");
                ImGuiOm.ClickToCopyAndNotify(listing.BuyerName);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{listing.PurchaseTime.ToLocalTime():yyyy/MM/dd HH:mm:ss}");
            }
        }

        private void DrawMarketUpshelf()
        {
            var manager = InventoryManager.Instance();
            if (manager == null) return;

            var container = manager->GetInventoryContainer(sourceUpshelfType);
            if (container == null || !container->IsLoaded) return;

            var slotData = container->GetInventorySlot(sourceUpshelfSlot);
            if (slotData == null || slotData->ItemId == 0) return;

            if (!LuminaGetter.TryGetRow<Item>(slotData->ItemId, out var itemData)) return;

            var isItemHQ = slotData->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);

            var itemIcon = DService.Instance().Texture
                                   .GetFromGameIcon(new(itemData.Icon, isItemHQ))
                                   .GetWrapOrDefault();
            if (itemIcon == null) return;

            using var id   = ImRaii.PushId($"{sourceUpshelfType}_{sourceUpshelfSlot}");
            using var font = FontManager.Instance().UIFont120.Push();

            using (FontManager.Instance().UIFont80.Push())
            {
                if (ImGuiOm.ButtonSelectable(LuminaWrapper.GetAddonText(2366)))
                    isNeedToDrawMarketUpshelfWindow = false;
            }

            ImGui.Separator();
            ImGui.Spacing();

            ImGui.Image(itemIcon.Handle, manualUnitPriceImageSize with { X = manualUnitPriceImageSize.Y });

            ImGui.SameLine();

            using (ImRaii.Group())
            using (FontManager.Instance().UIFont160.Push())
                ImGui.TextUnformatted($"{itemData.Name.ToString()}" + (isItemHQ ? "\ue03c" : string.Empty));

            manualUnitPriceImageSize = ImGui.GetItemRectSize();

            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{LuminaWrapper.GetAddonText(933)}:");

            ImGui.SameLine();
            ImGui.SetNextItemWidth(150f * GlobalUIScale);
            ImGui.InputUInt("###UnitPriceInput", ref upshelfUnitPriceInput);

            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Amount")}:");

            ImGui.SameLine();
            ImGui.SetNextItemWidth(150f * GlobalUIScale);
            ImGui.InputUInt("###QuantityInput", ref upshelfQuantityInput);

            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{LuminaWrapper.GetAddonText(6936)}:");

            ImGui.SameLine();
            ImGui.TextUnformatted($"{(upshelfQuantityInput * upshelfUnitPriceInput).ToChineseString()}");

            ImGui.Separator();

            if (ImGuiOm.ButtonSelectable(Lang.Get("AutoRetainerWork-PriceAdjust-UpshelfAuto")))
            {
                if (TryGetFirstEmptyRetainerMarketSlot(out var firstEmptySlot))
                {
                    UpshelfMarketItem(sourceUpshelfType, sourceUpshelfSlot, upshelfQuantityInput, 9_9999_9999, (short)firstEmptySlot);
                    EnqueuePriceAdjustSingle(firstEmptySlot);
                    isNeedToDrawMarketUpshelfWindow = false;
                }
            }

            if (ImGuiOm.ButtonSelectable(Lang.Get("AutoRetainerWork-PriceAdjust-UpshelfManual")))
            {
                UpshelfMarketItem(sourceUpshelfType, sourceUpshelfSlot, upshelfQuantityInput, upshelfUnitPriceInput);
                isNeedToDrawMarketUpshelfWindow = false;
            }
        }

        #endregion

        #region 事件

        // 出售品列表 (悬浮窗控制)
        private void OnRetainerSellList(AddonEvent type, AddonArgs args)
        {
            // 因为有模特存在
            if (!DService.Instance().Condition[ConditionFlag.OccupiedSummoningBell]) return;

            switch (type)
            {
                case AddonEvent.PostDraw:
                    isNeedToDrawMarketListWindow = true;

                    if (RetainerSellList != null)
                    {
                        var listComponent = RetainerSellList->GetComponentListById(11);

                        if (listComponent != null)
                        {
                            for (var i = 0; i < listComponent->GetItemCount(); i++)
                            {
                                var item = listComponent->GetItemRenderer(i);
                                if (item == null || !item->OwnerNode->IsVisible()) continue;

                                item->OwnerNode->ToggleVisibility(false);
                            }
                        }
                    }

                    break;
                case AddonEvent.PreFinalize:
                    isNeedToDrawMarketListWindow = false;

                    isDisplayingTooltip = false;
                    AtkStage.Instance()->HideTooltip(ScreenText->Id);
                    break;
            }
        }

        // 出售界面
        private static void OnRetainerSell(AddonEvent type, AddonArgs args)
        {
            if (!DService.Instance().Condition[ConditionFlag.OccupiedSummoningBell]) return;
            if (!args.Addon.ToStruct()->IsAddonAndNodesReady()) return;
            args.Addon.ToStruct()->Callback(0);
        }

        // 当前市场数据获取
        private void OnOfferingReceived(IMarketBoardCurrentOfferings data) =>
            PriceCacheManager.OnOfferingReceived(Module, data);

        // 历史交易数据获取
        private void OnHistoryReceived(IMarketBoardHistory history)
        {
            if (history.ItemId != historyListings.Key)
                historyListings = new(history.ItemId, []);
            historyListings.Value.AddRange(history.HistoryListings);

            PriceCacheManager.OnHistoryReceived(history);
        }

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
            if (slot == null) return;

            if (!TryGetItemUpshelfCountLimit(*slot, out var upshelfQuantity)) return;

            if (Module.config.AutoPriceAdjustWhenNewOnSale && !PluginConfig.Instance().ConflictKeyBinding.IsPressed())
            {
                MoveToRetainerMarketHook.Original(manager, srcInv, srcSlot, dstInv, dstSlot, upshelfQuantity, 9_9999_9999);
                EnqueuePriceAdjustSingle(dstSlot);
                return;
            }

            sourceUpshelfType = srcInv;
            sourceUpshelfSlot = srcSlot;

            var info = InfoProxyItemSearch.Instance();
            if (info == null) return;

            if (info->SearchItemId != slot->ItemId)
                RequestMarketItemData(slot->ItemId);

            upshelfUnitPriceInput = LuminaGetter.TryGetRow<Item>(slot->ItemId, out var itemRow) ? itemRow.PriceMid : 1;
            upshelfQuantityInput  = upshelfQuantity;

            isNeedToDrawMarketUpshelfWindow = true;
        }

        #endregion

        #region 队列

        private void EnqueuePriceAdjustAll()
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
                                EnqueuePriceAdjustSingle();
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

        private void EnqueuePriceAdjustSingle()
        {
            if (taskHelper.AbortByConflictKey(Module)) return;
            if (Module.IsAnyOtherWorkerBusy(typeof(PriceAdjustWorker))) return;

            var retainer = RetainerManager.Instance()->GetActiveRetainer();
            if (retainer == null || retainer->MarketItemCount <= 0) return;

            var container = InventoryManager.Instance()->GetInventoryContainer(InventoryType.RetainerMarket);
            if (container == null || !container->IsLoaded) return;

            for (ushort i = 0; i < container->Size; i++)
                EnqueuePriceAdjustSingle(i);
        }

        private void EnqueuePriceAdjustSingle(ushort slotIndex, uint forcePrice = 0)
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
                                RequestMarketItemData(itemID);
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
                                if (IsMarketStuck()) return false;

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

        private void EnqueuePriceAdjustSingleItem(ushort slot, uint marketPrice, uint forcePrice = 0)
        {
            if (taskHelper.AbortByConflictKey(Module)) return;
            if (Module.IsAnyOtherWorkerBusy(typeof(PriceAdjustWorker))) return;

            var itemMarketData = GetRetainerMarketItem(slot);
            if (itemMarketData == null) return;

            var itemConfig    = GetItemConfigByItemKey(itemMarketData.Value.Item);
            var modifiedPrice = forcePrice > 0 ? forcePrice : GetModifiedPrice(itemConfig, marketPrice);

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
            void EnqueueAbortBehavior(AbortBehavior behavior)
            {
                if (Module.config.SendPriceAdjustProcessMessage)
                {
                    var message = Lang.GetSe
                    (
                        "AutoRetainerWork-PriceAdjust-ConductAbortBehavior",
                        new SeStringBuilder().AddUiForeground(behavior.ToString(), 67).Build()
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

        private ItemConfig GetItemConfigByItemKey(ItemKey key) =>
            Module.config.ItemConfigs.TryGetValue(key.ToString(), out var itemConfig)
                ? itemConfig
                : Module.config.ItemConfigs[new ItemKey(0, key.IsHQ).ToString()];

        #endregion

        #region 操作

        /// <summary>
        ///     将当前雇员市场售卖物品收回背包/雇员
        /// </summary>
        /// <param name="slot"></param>
        /// <param name="isInventory">若为 True 则为收回背包, 否则则为收回雇员背包</param>
        private bool ReturnRetainerMarketItemToInventory(ushort slot, bool isInventory)
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
        private static bool SetRetainerMarketItemPrice(ushort slot, uint price)
        {
            if (slot >= 20) return false;

            var manager = InventoryManager.Instance();
            if (manager == null) return false;

            manager->SetRetainerMarketPrice((short)slot, price);
            return true;
        }

        /// <summary>
        ///     上架物品至市场
        /// </summary>
        private void UpshelfMarketItem(InventoryType srcType, ushort srcSlot, uint quantity, uint unitPrice, short targetSlot = -1)
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
        ///     获取当前雇员市场售卖物品数据
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
        ///     获取当前雇员市场售卖物品价格
        /// </summary>
        private static uint GetRetainerMarketPrice(ushort slot)
        {
            if (slot >= 20) return 0;

            var manager = InventoryManager.Instance();
            if (manager == null) return 0;

            return (uint)manager->GetRetainerMarketPrice((short)slot);
        }

        /// <summary>
        ///     获取当前市场物品数据
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
        ///     当前市场物品数据是否已就绪
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
                               .ToList().Count !=
                proxy->ListingCount)
                return false;

            return proxy->EntryCount switch
            {
                > 10 => proxy->ListingCount >= 10,
                0    => true,
                _    => proxy->ListingCount != 0
            };
        }

        /// <summary>
        ///     尝试获取雇员市场售卖列表中首个为空的槽位
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
        ///     获取修改后价格结果
        /// </summary>
        private static uint GetModifiedPrice(ItemConfig config, uint marketPrice) =>
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
                              marketPrice * (1 - config.AdjustValues[AdjustBehavior.百分比] / 100)
                          ),
                          _ => marketPrice
                      });

        /// <summary>
        ///     发送改价成功通知信息
        /// </summary>
        private void NotifyPriceAdjustSuccessfully(uint itemID, bool isHQ, uint origPrice, uint modifiedPrice)
        {
            if (!Module.config.SendPriceAdjustProcessMessage) return;

            var itemPayload = new SeStringBuilder().AddItemLink(itemID, isHQ).Build();

            var priceChangedValue = (long)modifiedPrice - origPrice;

            var priceChangeText = priceChangedValue.ToChineseString();
            if (!priceChangeText.StartsWith('-'))
                priceChangeText = $"+{priceChangeText}";

            var priceChangeRate     = origPrice == 0 ? 0 : (double)priceChangedValue / origPrice * 100;
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
        private void NotifyAbortCondition(uint itemID, bool isHQ, AbortCondition condition)
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
                    new SeStringBuilder().AddUiForeground(condition.ToString(), 60).Build()
                )
            );
        }

        /// <summary>
        ///     获取当前雇员市场为同一物品的全部槽位
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
        ///     尝试获取物品最大可上架数量
        /// </summary>
        private bool TryGetItemUpshelfCountLimit(InventoryItem item, out uint count)
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
        ///     当前市场是否正在重新请求
        /// </summary>
        /// <returns></returns>
        private static bool IsMarketStuck()
        {
            if (!ModuleManager.Instance().TryGetModuleByName("AutoRefreshMarketSearchResult", out var module) || module == null) return false;

            var type     = module.GetType();
            var property = type.GetProperty("IsMarketStuck", BindingFlags.Public | BindingFlags.Static);

            return property != null && (bool)property.GetValue(null);
        }

        #endregion
    }
}
