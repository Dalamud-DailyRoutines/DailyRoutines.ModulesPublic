using System;
using System.Linq;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using OmenTools.Helpers;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoBuyMaps : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoBuyMapsTitle"),
        Description = GetLoc("AutoBuyMapsDescription"),
        Category = ModuleCategories.General,
        Author = ["qingsiweisan"]
    };

    private static readonly (uint ItemID, string Name, uint DecipheredKeyItemID)[] MapData =
    {
        (0, "SelectMap", 0), (6688, "G1", 2001087), (6689, "G2", 2001088), (6690, "G3", 2001089), (6691, "G4", 2001090),
        (6692, "G5", 2001091), (12241, "G6", 2001762), (12242, "G7", 2001763), (12243, "G8", 2001764), (17835, "G9", 2002209),
        (17836, "G10", 2002210), (26744, "G11", 2002663), (26745, "G12", 2002664), (36611, "G13", 2003245), (36612, "G14", 2003246),
        (39591, "G15", 2003457), (43556, "G16", 2003562), (43557, "G17", 2003563), (46185, "G18", 2003785)
    };

    private bool isBuying = false;
    private uint? originalMapID = null;
    private int? originalCount = null;
    private int? originalMaxPrice = null;
    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new Config();
        TaskHelper ??= new() { TimeLimitMS = 30_000 };

        CommandManager.AddSubCommand("buymaps", new Dalamud.Game.Command.CommandInfo(OnPdrCommand)
        {
            HelpMessage = GetLoc("AutoBuyMapsCommandHelp"),
        });
    }

    protected override void Uninit()
    {
        CommandManager.RemoveSubCommand("buymaps");
        SaveConfig(ModuleConfig);
    }

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        if (ImGui.BeginCombo(GetLoc("AutoBuyMapsMap"), GetMapName(ModuleConfig.TargetMapID)))
        {
            foreach (var (itemId, name, _) in MapData)
            {
                var displayName = name == "SelectMap" ? GetLoc("AutoBuyMapsSelectMap") : name;
                if (ImGui.Selectable(displayName, ModuleConfig.TargetMapID == itemId))
                {
                    ModuleConfig.TargetMapID = itemId;
                    SaveConfig(ModuleConfig);
                }
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(60f * GlobalFontScale);
        if (ImGui.InputInt(GetLoc("AutoBuyMapsCount"), ref ModuleConfig.Count))
        {
            ModuleConfig.Count = Math.Clamp(ModuleConfig.Count, 1, 3);
            SaveConfig(ModuleConfig);
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(100f * GlobalFontScale);
        if (ImGui.InputInt(GetLoc("AutoBuyMapsMaxGil"), ref ModuleConfig.MaxPrice))
        {
            ModuleConfig.MaxPrice = Math.Max(0, ModuleConfig.MaxPrice);
            SaveConfig(ModuleConfig);
        }

        ImGui.Spacing();

        ImGui.Spacing();

        using (ImRaii.Disabled(isBuying || ModuleConfig.TargetMapID == 0))
        {
            if (ImGui.Button("Start"))
                StartPurchase();
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!isBuying))
        {
            if (ImGui.Button("Stop"))
                StopPurchase();
        }

        if (isBuying)
            ImGui.TextColored(ImGui.ColorConvertFloat4ToU32(ImGuiColors.DalamudYellow),
                GetLoc("AutoBuyMapsPurchasing", AnalyzeCurrentState().TotalCount, ModuleConfig.Count));
    }

    private void RetryPurchase(int delay = 1000) => RetryAction(PurchaseNext, delay);
    private void RetryAction(System.Action action, int delay)
    {
        TaskHelper.DelayNext(delay);
        TaskHelper.Enqueue(() => { action(); return true; });
    }
    private void EnqueueWithRetry(System.Action action) => TaskHelper.Enqueue(() => { action(); return true; });

    private void StartPurchase()
    {
        if (isBuying || ModuleConfig.TargetMapID == 0)
            return;

        isBuying = true;
        EnqueueWithRetry(PurchaseNext);
    }

    private void PurchaseNext()
    {
        if (!isBuying)
        {
            StopPurchase();
            return;
        }

        var state = AnalyzeCurrentState();

        if (state.TotalCount >= ModuleConfig.Count)
        {
            StopPurchase();
            return;
        }

        var nextAction = DetermineNextAction(state);

        switch (nextAction)
        {
            case NextAction.DecipherInventoryMap:
                DecipherMap();
                break;
            case NextAction.MoveInventoryToSaddlebag:
                MoveToSaddlebag();
                break;
            case NextAction.PurchaseMore:
                SearchMarket();
                break;
            case NextAction.Complete:
                StopPurchase();
                break;
            case NextAction.Wait:
                RetryPurchase(1000);
                break;
        }
    }

    private void SearchMarket()
    {
        var infoProxy = InfoProxyItemSearch.Instance();
        if (infoProxy == null)
        {
            StopPurchase();
            return;
        }

        infoProxy->EndRequest();
        infoProxy->SearchItemId = ModuleConfig.TargetMapID;

        if (!infoProxy->RequestData())
        {
            RetryAction(SearchMarket, 1000);
            return;
        }

        TaskHelper.Enqueue(() => IsMarketDataFullyReceived(ModuleConfig.TargetMapID));
        EnqueueWithRetry(ProcessResults);
    }

    private bool IsMarketDataFullyReceived(uint itemID)
    {
        if (!isBuying) return false;

        var infoProxy = InfoProxyItemSearch.Instance();
        if (infoProxy == null || infoProxy->SearchItemId != itemID) return false;

        if (infoProxy->Listings.ToArray()
                               .Where(x => x.ItemId == infoProxy->SearchItemId && x.UnitPrice != 0)
                               .ToList().Count != infoProxy->ListingCount)
            return false;

        return infoProxy->EntryCount switch
        {
            > 10 => infoProxy->ListingCount >= 10,
            0 => true,
            _ => infoProxy->ListingCount != 0
        };
    }

    private void ProcessResults()
    {
        var infoProxy = InfoProxyItemSearch.Instance();
        if (infoProxy == null)
        {
            StopPurchase();
            return;
        }

        if (infoProxy->ListingCount == 0)
        {
            StopPurchase();
            return;
        }

        var currentState = AnalyzeCurrentState();
        var listingsToBuy = infoProxy->Listings.ToArray()
            .Where(x => x.ItemId == ModuleConfig.TargetMapID && x.UnitPrice > 0)
            .Where(x => ModuleConfig.MaxPrice <= 0 || x.UnitPrice <= (ulong)ModuleConfig.MaxPrice)
            .OrderBy(x => x.UnitPrice)
            .Take(Math.Min(ModuleConfig.Count - currentState.TotalCount, 1))
            .ToList();

        if (listingsToBuy.Count == 0)
        {
            StopPurchase();
            return;
        }

        var purchasesBefore = currentState.TotalCount;

        foreach (var listing in listingsToBuy)
        {
            TaskHelper.Enqueue(() => SendBuyRequest(listing));
            TaskHelper.Enqueue(() => infoProxy->Listings.ToArray()
                .FirstOrDefault(x => x.ListingId == listing.ListingId).ListingId == 0);
        }

        TaskHelper.DelayNext(1000);
        TaskHelper.Enqueue(() => WaitForItemArrival(purchasesBefore));
        RetryPurchase(1000);
    }

    private bool SendBuyRequest(MarketBoardListing listing)
    {
        var infoProxy = InfoProxyItemSearch.Instance();
        if (infoProxy == null) return false;

        infoProxy->SetLastPurchasedItem(&listing);
        return infoProxy->SendPurchaseRequestPacket();
    }

    private static bool HasItemInInventory(uint itemID) =>
        HelpersOm.TryGetFirstInventoryItem(InventoryTypes, item => item.ItemId == itemID, out _);

    private static bool HasItemInSaddlebag(uint itemID) =>
        HelpersOm.TryGetFirstInventoryItem(SaddlebagTypes, item => item.ItemId == itemID, out _);

    private static bool HasDecipheredMap(uint mapItemID)
    {
        var decipheredKeyItemID = MapData.FirstOrDefault(m => m.ItemID == mapItemID).DecipheredKeyItemID;
        return decipheredKeyItemID != 0 && HelpersOm.TryGetFirstInventoryItem([InventoryType.KeyItems],
            item => item.ItemId == decipheredKeyItemID, out _);
    }

    private bool WaitForItemArrival(int expectedCount) => AnalyzeCurrentState().TotalCount > expectedCount;

    private void StopPurchase()
    {
        isBuying = false;

        if (originalMapID.HasValue)
        {
            ModuleConfig.TargetMapID = originalMapID.Value;
            ModuleConfig.Count = originalCount ?? ModuleConfig.Count;
            ModuleConfig.MaxPrice = originalMaxPrice ?? ModuleConfig.MaxPrice;
            SaveConfig(ModuleConfig);

            originalMapID = null;
            originalCount = null;
            originalMaxPrice = null;
        }

        TaskHelper?.Abort();
    }

    private struct InventoryState
    {
        public int InventoryCount { get; set; }
        public int SaddlebagCount { get; set; }
        public int DecipheredCount { get; set; }
        public int TotalCount => InventoryCount + SaddlebagCount + DecipheredCount;
    }


    private static InventoryState AnalyzeCurrentState()
    {
        var manager = InventoryManager.Instance();
        if (manager == null) return new InventoryState();

        var targetMapID = ModuleConfig.TargetMapID;
        var inventoryCount = InventoryTypes.Sum(type => (int)manager->GetItemCountInContainer(targetMapID, type));
        var saddlebagCount = SaddlebagTypes.Sum(type => (int)manager->GetItemCountInContainer(targetMapID, type));

        var decipheredCount = 0;
        var mapInfo = MapData.FirstOrDefault(m => m.ItemID == targetMapID);
        if (mapInfo.DecipheredKeyItemID != 0)
            decipheredCount = (int)manager->GetItemCountInContainer(mapInfo.DecipheredKeyItemID, InventoryType.KeyItems);

        return new InventoryState
        {
            InventoryCount = inventoryCount,
            SaddlebagCount = saddlebagCount,
            DecipheredCount = decipheredCount
        };
    }

    private enum NextAction
    {
        DecipherInventoryMap,
        MoveInventoryToSaddlebag,
        PurchaseMore,
        Complete,
        Wait
    }

    private NextAction DetermineNextAction(InventoryState state)
    {
        var targetMapID = ModuleConfig.TargetMapID;

        if (state.TotalCount >= ModuleConfig.Count)
            return NextAction.Complete;

        if (HasItemInInventory(targetMapID) && !HasDecipheredMap(targetMapID))
            return NextAction.DecipherInventoryMap;

        if (HasDecipheredMap(targetMapID) && HasItemInInventory(targetMapID) && !HasItemInSaddlebag(targetMapID))
            return NextAction.MoveInventoryToSaddlebag;

        if (!HasItemInInventory(targetMapID) && state.TotalCount < ModuleConfig.Count)
            return NextAction.PurchaseMore;

        return NextAction.Wait;
    }

    private static int FindTargetMapInPopupMenu(uint targetMapID)
    {
        var addonPtr = DService.Gui.GetAddonByName("SelectIconString");
        if (addonPtr == nint.Zero) return -1;

        var addon = (AddonSelectIconString*)*(nint*)&addonPtr;
        if (!addon->AtkUnitBase.IsVisible) return -1;

        if (!LuminaGetter.TryGetRow<Item>(targetMapID, out var targetItem)) return -1;
        var targetMapName = targetItem.Name.ExtractText();
        if (string.IsNullOrEmpty(targetMapName)) return -1;

        var popupMenu = &addon->PopupMenu.PopupMenu;
        for (var i = 0; i < popupMenu->EntryCount; i++)
        {
            if (popupMenu->EntryNames == null) continue;
            var entryNamePtr = popupMenu->EntryNames[i];
            var entryName = entryNamePtr.ToString();
            if (!string.IsNullOrEmpty(entryName) && entryName.Contains(targetMapName))
                return i;
        }

        return -1;
    }

    private void DecipherMap()
    {
        var mapInfo = MapData.FirstOrDefault(m => m.ItemID == ModuleConfig.TargetMapID);
        if (mapInfo.DecipheredKeyItemID == 0)
        {
            RetryPurchase(500);
            return;
        }

        var manager = InventoryManager.Instance();
        if (manager != null && manager->GetItemCountInContainer(mapInfo.DecipheredKeyItemID, InventoryType.KeyItems) > 0)
        {
            RetryPurchase(500);
            return;
        }

        TaskHelper.Enqueue(() =>
        {
            var actionManager = ActionManager.Instance();
            if (actionManager == null) return false;

            return actionManager->UseAction(ActionType.GeneralAction, 19);
        });

        TaskHelper.DelayNext(100);
        TaskHelper.Enqueue(() =>
        {
            if (!HelpersOm.IsAddonAndNodesReady(InfosOm.SelectIconString))
                return false;

            var targetIndex = FindTargetMapInPopupMenu(ModuleConfig.TargetMapID);
            if (targetIndex == -1)
            {
                HelpersOm.ClickSelectIconString(-1);
                return false;
            }

            return HelpersOm.ClickSelectIconString(targetIndex);
        });

        TaskHelper.DelayNext(100);
        TaskHelper.Enqueue(() =>
        {
            if (!HelpersOm.IsAddonAndNodesReady(InfosOm.SelectYesno))
                return false;

            return HelpersOm.ClickSelectYesnoYes();
        });

        TaskHelper.DelayNext(200);
        TaskHelper.Enqueue(() =>
        {
            if (HasDecipheredMap(ModuleConfig.TargetMapID))
            {
                RetryPurchase(500);
                return true;
            }
            return false;
        });
    }

    private void MoveToSaddlebag()
    {
        var (invType, slot) = FindItemInInventory(ModuleConfig.TargetMapID);
        if (invType == InventoryType.Invalid || !HasSaddlebagSpace())
        {
            RetryPurchase(500);
            return;
        }

        if (!OpenSaddlebag())
        {
            RetryPurchase(1000);
            return;
        }

        TaskHelper.DelayNext(500);
        TaskHelper.Enqueue(() =>
        {
            if (MoveItemToSaddlebag(invType, slot) && HasItemInSaddlebag(ModuleConfig.TargetMapID))
            {
                CloseSaddlebag();
                RetryPurchase(500);
            }
            else
            {
                CloseSaddlebag();
                RetryPurchase(1000);
            }
            return true;
        });
    }

    private bool HasSaddlebagSpace() => SaddlebagTypes.Any(type => FindEmptySlot(type) != -1);

    private string GetMapName(uint mapID) =>
        MapData.FirstOrDefault(m => m.ItemID == mapID) is var map && map.ItemID != 0 && map.Name != "SelectMap" ? map.Name : GetLoc("AutoBuyMapsSelectMap");

    private static unsafe (InventoryType type, int slot) FindItem(uint itemID, params InventoryType[] types)
    {
        if (HelpersOm.TryGetFirstInventoryItem(types, item => item.ItemId == itemID, out var foundItem))
            return (foundItem->Container, foundItem->Slot);
        return (InventoryType.Invalid, -1);
    }

    private static readonly InventoryType[] InventoryTypes = { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 };
    private static readonly InventoryType[] SaddlebagTypes = { InventoryType.SaddleBag1, InventoryType.SaddleBag2 };

    private static (InventoryType type, int slot) FindItemInInventory(uint itemID) => FindItem(itemID, InventoryTypes);
    private static (InventoryType type, int slot) FindItemInSaddlebag(uint itemID) => FindItem(itemID, SaddlebagTypes);

    private static bool OpenSaddlebag() => HelpersOm.SendEvent(AgentId.Inventory, 0) != null;

    private static unsafe bool MoveItemBetweenContainers(InventoryType fromType, int fromSlot, params InventoryType[] targetTypes)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null) return false;

        foreach (var toType in targetTypes)
        {
            var targetSlot = FindEmptySlot(toType);
            if (targetSlot == -1) continue;

            try
            {
                var result = inventoryManager->MoveItemSlot(fromType, (ushort)fromSlot, toType, (ushort)targetSlot, true);
                if (result > 0) return true;
            }
            catch { continue; }
        }
        return false;
    }

    private static bool MoveItemToSaddlebag(InventoryType fromType, int fromSlot) =>
        MoveItemBetweenContainers(fromType, fromSlot, SaddlebagTypes);

    private static unsafe int FindEmptySlot(InventoryType inventoryType)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null) return -1;

        var container = inventoryManager->GetInventoryContainer(inventoryType);
        if (container == null) return -1;

        for (int i = 0; i < container->Size; i++)
        {
            var slot = container->GetInventorySlot(i);
            if (slot != null && slot->IsEmpty())
                return i;
        }
        return -1;
    }

    private static bool CloseSaddlebag() => HelpersOm.SendEvent(AgentId.Inventory, 1) != null;

    private void OnPdrCommand(string command, string args)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(args))
                return;

            var parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return;

            var action = parts[0].ToLower();

            if (action == "start")
            {
                if (ModuleConfig.TargetMapID == 0)
                    return;
                StartPurchase();
                return;
            }

            if (action == "stop")
            {
                StopPurchase();
                return;
            }

            if (parts.Length < 2)
                return;

            var mapData = MapData.Skip(1).FirstOrDefault(m =>
                m.Name.Equals(parts[0], StringComparison.OrdinalIgnoreCase) ||
                (int.TryParse(parts[0], out int grade) && m.Name == $"G{grade}"));
            if (mapData.ItemID == 0) return;

            if (!int.TryParse(parts[1], out int quantity) || quantity <= 0 || quantity > 10)
                return;

            int maxPrice = 0;
            if (parts.Length >= 3)
            {
                if (!int.TryParse(parts[2], out maxPrice) || maxPrice < 0)
                    return;
            }

            if (isBuying)
                return;

            originalMapID = ModuleConfig.TargetMapID;
            originalCount = ModuleConfig.Count;
            originalMaxPrice = ModuleConfig.MaxPrice;

            ModuleConfig.TargetMapID = mapData.ItemID;
            ModuleConfig.Count = quantity;
            ModuleConfig.MaxPrice = maxPrice;

            StartPurchase();
        }
        catch (Exception ex)
        {
            NotificationError(GetLoc("AutoBuyMapsCommandError", ex.Message));
        }
    }

    protected class Config : ModuleConfiguration
    {
        public uint TargetMapID = 0;
        public int Count = 3;
        public int MaxPrice = 0;
    }
}
