using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using DailyRoutines.Modules;
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
        var count = ModuleConfig.Count;
        if (ImGui.InputInt(GetLoc("AutoBuyMapsCount"), ref count))
        {
            ModuleConfig.Count = Math.Clamp(count, 1, 5);
            SaveConfig(ModuleConfig);
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(100f * GlobalFontScale);
        var maxPrice = ModuleConfig.MaxPrice;
        if (ImGui.InputInt(GetLoc("AutoBuyMapsMaxGil"), ref maxPrice))
        {
            ModuleConfig.MaxPrice = Math.Max(0, maxPrice);
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
            ImGui.TextColored(ImGuiColors.DalamudYellow.ToUint(),
                GetLoc("AutoBuyMapsPurchasing", AnalyzeCurrentState().TotalCount, ModuleConfig.Count));
    }

    private void StartPurchase()
    {
        if (isBuying || ModuleConfig.TargetMapID == 0)
            return;

        isBuying = true;

        TaskHelper.Enqueue(() =>
        {
            PurchaseNext();
            return true;
        });
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
            case NextAction.MoveSaddlebagToInventory:
                MoveSaddlebagToInventory();
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
                TaskHelper.DelayNext(1000);
                TaskHelper.Enqueue(() => { PurchaseNext(); return true; });
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
            TaskHelper.DelayNext(1000);
            TaskHelper.Enqueue(() => { SearchMarket(); return true; });
            return;
        }

        TaskHelper.Enqueue(() => IsMarketDataFullyReceived(ModuleConfig.TargetMapID));
        TaskHelper.Enqueue(() => { ProcessResults(); return true; });
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

        var listings = infoProxy->Listings.ToArray();
        var validListings = listings.Where(x => x.ItemId == ModuleConfig.TargetMapID && x.UnitPrice > 0);

        if (ModuleConfig.MaxPrice > 0)
            validListings = validListings.Where(x => x.UnitPrice <= (ulong)ModuleConfig.MaxPrice);

        var cheapest = validListings.OrderBy(x => x.UnitPrice).FirstOrDefault();

        if (cheapest.ItemId == 0)
        {
            StopPurchase();
            return;
        }

        var currentState = AnalyzeCurrentState();
        var remainingCount = ModuleConfig.Count - currentState.TotalCount;
        var listingsToBuy = validListings.Take(Math.Min(remainingCount, 1)).ToList();

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

        TaskHelper.DelayNext(2000);
        TaskHelper.Enqueue(() => WaitForItemArrival(purchasesBefore));
        TaskHelper.DelayNext(1000);
        TaskHelper.Enqueue(() => { PurchaseNext(); return true; });
    }

    private bool SendBuyRequest(MarketBoardListing listing)
    {
        var infoProxy = InfoProxyItemSearch.Instance();
        if (infoProxy == null) return false;

        infoProxy->SetLastPurchasedItem(&listing);
        return infoProxy->SendPurchaseRequestPacket();
    }

    private bool HasItemInInventory(uint itemID) => InventoryManager.Instance() is var m && m != null && m->GetInventoryItemCount(itemID, checkArmory: false, checkEquipped: false) > 0;
    private bool HasItemInSaddlebag(uint itemID) => InventoryManager.Instance() is var m && m != null && (m->GetItemCountInContainer(itemID, InventoryType.SaddleBag1) + m->GetItemCountInContainer(itemID, InventoryType.SaddleBag2)) > 0;

    private bool HasDecipheredMap(uint mapItemID)
    {
        var manager = InventoryManager.Instance();
        if (manager == null) return false;

        var mapInfo = MapData.FirstOrDefault(m => m.ItemID == mapItemID);
        if (mapInfo.DecipheredKeyItemID == 0) return false;

        return manager->GetItemCountInContainer(mapInfo.DecipheredKeyItemID, InventoryType.KeyItems) > 0;
    }

    private bool WaitForItemArrival(int expectedCount)
    {
        var currentState = AnalyzeCurrentState();
        return currentState.TotalCount > expectedCount;
    }

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


    private InventoryState AnalyzeCurrentState()
    {
        var manager = InventoryManager.Instance();
        if (manager == null) return new InventoryState();

        var targetMapID = ModuleConfig.TargetMapID;

        var inventoryCount = 0;
        for (var type = InventoryType.Inventory1; type <= InventoryType.Inventory4; type++)
            inventoryCount += (int)manager->GetItemCountInContainer(targetMapID, type);

        var saddlebagCount = 0;
        for (var type = InventoryType.SaddleBag1; type <= InventoryType.SaddleBag2; type++)
            saddlebagCount += (int)manager->GetItemCountInContainer(targetMapID, type);

        var decipheredCount = 0;
        if (HasDecipheredMap(targetMapID))
        {
            var mapInfo = MapData.FirstOrDefault(m => m.ItemID == targetMapID);
            decipheredCount = (int)manager->GetItemCountInContainer(mapInfo.DecipheredKeyItemID, InventoryType.KeyItems);
        }

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
        MoveSaddlebagToInventory,
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

        if (HasItemInSaddlebag(targetMapID) && !HasDecipheredMap(targetMapID))
            return NextAction.MoveSaddlebagToInventory;

        if (HasDecipheredMap(targetMapID) && HasItemInInventory(targetMapID) && !HasItemInSaddlebag(targetMapID))
            return NextAction.MoveInventoryToSaddlebag;

        if (!HasItemInInventory(targetMapID) && state.TotalCount < ModuleConfig.Count)
            return NextAction.PurchaseMore;

        return NextAction.Wait;
    }

    private void DecipherMap()
    {
        var mapInfo = MapData.FirstOrDefault(m => m.ItemID == ModuleConfig.TargetMapID);
        if (mapInfo.DecipheredKeyItemID == 0)
        {
            TaskHelper.DelayNext(500);
            TaskHelper.Enqueue(() => { PurchaseNext(); return true; });
            return;
        }

        var manager = InventoryManager.Instance();
        if (manager != null && manager->GetItemCountInContainer(mapInfo.DecipheredKeyItemID, InventoryType.KeyItems) > 0)
        {
            TaskHelper.DelayNext(500);
            TaskHelper.Enqueue(() => { PurchaseNext(); return true; });
            return;
        }

        TaskHelper.Enqueue(() =>
        {
            var (invType, slot) = FindItemInInventory(ModuleConfig.TargetMapID);

            if (invType == InventoryType.Invalid)
            {
                var (saddlebagType, saddlebagSlot) = FindItemInSaddlebag(ModuleConfig.TargetMapID);
                if (saddlebagType != InventoryType.Invalid)
                {
                    var moveResult = MoveItemToInventory(saddlebagType, saddlebagSlot);
                    if (!moveResult)
                    {
                        TaskHelper.DelayNext(1000);
                        TaskHelper.Enqueue(() => { PurchaseNext(); return true; });
                        return true;
                    }
                    (invType, slot) = FindItemInInventory(ModuleConfig.TargetMapID);
                }
            }

            if (invType == InventoryType.Invalid)
            {
                TaskHelper.DelayNext(1000);
                TaskHelper.Enqueue(() => { PurchaseNext(); return true; });
                return true;
            }

            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null)
            {
                TaskHelper.DelayNext(1000);
                TaskHelper.Enqueue(() => { PurchaseNext(); return true; });
                return true;
            }

            var itemSlot = inventoryManager->GetInventorySlot(invType, slot);
            if (itemSlot == null)
            {
                TaskHelper.DelayNext(1000);
                TaskHelper.Enqueue(() => { PurchaseNext(); return true; });
                return true;
            }

            HelpersOm.OpenInventoryItemContext(*itemSlot);
            return true;
        });

        TaskHelper.DelayNext(300);
        TaskHelper.Enqueue(() =>
        {
            if (!HelpersOm.IsAddonAndNodesReady(InfosOm.ContextMenu))
                return false;

            var result = HelpersOm.ClickContextMenu(LuminaWrapper.GetAddonText(8100));
            if (!result)
            {
                InfosOm.ContextMenu->Close(true);
                TaskHelper.DelayNext(1000);
                TaskHelper.Enqueue(() => { PurchaseNext(); return true; });
                return false;
            }
            return true;
        });

        TaskHelper.DelayNext(300);
        TaskHelper.Enqueue(() =>
        {
            var result = HelpersOm.ClickSelectYesnoYes();
            if (result)
            {
                    TaskHelper.Enqueue(() =>
                {
                    if (HasDecipheredMap(ModuleConfig.TargetMapID))
                    {
                        TaskHelper.DelayNext(500);
                        TaskHelper.Enqueue(() => { PurchaseNext(); return true; });
                        return true;
                    }
                    return false;
                });
            }
            else
            {
                TaskHelper.DelayNext(1000);
                TaskHelper.Enqueue(() => { PurchaseNext(); return true; });
            }
            return result;
        });
    }

    private void MoveToSaddlebag()
    {
        TaskHelper.Enqueue(() =>
        {
            var (invType, slot) = FindItemInInventory(ModuleConfig.TargetMapID);
            if (invType == InventoryType.Invalid)
            {
                TaskHelper.DelayNext(500);
                TaskHelper.Enqueue(() => { PurchaseNext(); return true; });
                return true;
            }

            var hasSpace = FindEmptySlot(InventoryType.SaddleBag1) != -1 ||
                          FindEmptySlot(InventoryType.SaddleBag2) != -1;
            if (!hasSpace)
            {
                TaskHelper.DelayNext(1000);
                TaskHelper.Enqueue(() => { PurchaseNext(); return true; });
                return true;
            }

            var result = OpenSaddlebag();
            if (!result)
            {
                TaskHelper.DelayNext(1000);
                TaskHelper.Enqueue(() => { PurchaseNext(); return true; });
                return true;
            }
            return true;
        });

        TaskHelper.DelayNext(1000);
        TaskHelper.Enqueue(() =>
        {
            var (invType, slot) = FindItemInInventory(ModuleConfig.TargetMapID);
            if (invType == InventoryType.Invalid)
            {
                CloseSaddlebag();
                TaskHelper.DelayNext(500);
                TaskHelper.Enqueue(() => { PurchaseNext(); return true; });
                return true;
            }

            var moveResult = MoveItem(invType, slot, InventoryType.SaddleBag1);
            if (!moveResult)
                moveResult = MoveItem(invType, slot, InventoryType.SaddleBag2);

            if (!moveResult)
            {
                CloseSaddlebag();
                TaskHelper.DelayNext(1000);
                TaskHelper.Enqueue(() => { PurchaseNext(); return true; });
                return true;
            }

            TaskHelper.Enqueue(() =>
            {
                if (HasItemInSaddlebag(ModuleConfig.TargetMapID))
                {
                    CloseSaddlebag();
                    TaskHelper.DelayNext(500);
                    TaskHelper.Enqueue(() => { PurchaseNext(); return true; });
                    return true;
                }
                return false;
            });
            return true;
        });
    }

    private void MoveSaddlebagToInventory()
    {
        TaskHelper.Enqueue(() =>
        {
            var result = OpenSaddlebag();
            if (!result)
            {
                TaskHelper.DelayNext(1000);
                TaskHelper.Enqueue(() => { PurchaseNext(); return true; });
                return true;
            }
            return true;
        });

        TaskHelper.DelayNext(1000);
        TaskHelper.Enqueue(() =>
        {
            var (saddlebagType, slot) = FindItemInSaddlebag(ModuleConfig.TargetMapID);
            if (saddlebagType == InventoryType.Invalid)
            {
                CloseSaddlebag();
                TaskHelper.DelayNext(1000);
                TaskHelper.Enqueue(() => { PurchaseNext(); return true; });
                return true;
            }

            var moveResult = MoveItem(saddlebagType, slot, InventoryType.Inventory1);
            if (!moveResult)
            {
                CloseSaddlebag();
                TaskHelper.DelayNext(1000);
                TaskHelper.Enqueue(() => { PurchaseNext(); return true; });
                return true;
            }

            TaskHelper.Enqueue(() =>
            {
                if (HasItemInInventory(ModuleConfig.TargetMapID))
                {
                    CloseSaddlebag();
                    TaskHelper.DelayNext(500);
                    TaskHelper.Enqueue(() => { PurchaseNext(); return true; });
                    return true;
                }
                return false;
            });
            return true;
        });
    }


    private string GetMapName(uint mapID) =>
        MapData.FirstOrDefault(m => m.ItemID == mapID) is var map && map.ItemID != 0 && map.Name != "SelectMap" ? map.Name : GetLoc("AutoBuyMapsSelectMap");

    private static unsafe (InventoryType type, int slot) FindItem(uint itemID, params InventoryType[] types)
    {
        var manager = InventoryManager.Instance();
        if (manager == null) return (InventoryType.Invalid, -1);

        foreach (var type in types)
        {
            if (manager->GetItemCountInContainer(itemID, type) > 0)
            {
                var container = manager->GetInventoryContainer(type);
                if (container == null) continue;

                for (int i = 0; i < container->Size; i++)
                {
                    var slot = manager->GetInventorySlot(type, i);
                    if (slot != null && slot->ItemId == itemID)
                        return (type, i);
                }
            }
        }
        return (InventoryType.Invalid, -1);
    }

    private static (InventoryType type, int slot) FindItemInInventory(uint itemID) =>
        FindItem(itemID, InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4);

    private static (InventoryType type, int slot) FindItemInSaddlebag(uint itemID) =>
        FindItem(itemID, InventoryType.SaddleBag1, InventoryType.SaddleBag2);

    private static unsafe bool OpenInventory()
    {
        try
        {
            var uiModule = UIModule.Instance();
            if (uiModule != null)
            {
                uiModule->OpenInventory(0);
                return true;
            }
        }
        catch { }
        return false;
    }


    private static bool OpenSaddlebag()
    {
        try 
        { 
            DService.Command.ProcessCommand("/saddlebag"); 
            return true; 
        }
        catch 
        { 
            try 
            { 
                var ui = UIModule.Instance(); 
                if (ui != null) 
                { 
                    ui->OpenInventory(1); 
                    return true; 
                } 
            } 
            catch { } 
            return false; 
        }
    }

    private static unsafe bool MoveItem(InventoryType fromType, int fromSlot, InventoryType toType)
    {
        try
        {
            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null) return false;

            var targetSlot = FindEmptySlot(toType);
            if (targetSlot == -1) return false;

            var result = inventoryManager->MoveItemSlot(fromType, (ushort)fromSlot, toType, (ushort)targetSlot, true);
            return result > 0;
        }
        catch
        {
            return false;
        }
    }

    private static unsafe bool MoveItemToInventory(InventoryType fromType, int fromSlot)
    {
        InventoryType[] inventoryTypes = {
            InventoryType.Inventory1, InventoryType.Inventory2,
            InventoryType.Inventory3, InventoryType.Inventory4
        };

        foreach (var invType in inventoryTypes)
        {
            if (MoveItem(fromType, fromSlot, invType))
                return true;
        }
        return false;
    }

    private static unsafe int FindEmptySlot(InventoryType inventoryType)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null) return -1;

        var container = inventoryManager->GetInventoryContainer(inventoryType);
        if (container == null) return -1;

        for (int i = 0; i < container->Size; i++)
        {
            var slot = container->GetInventorySlot(i);
            if (slot != null && slot->ItemId == 0)
                return i;
        }
        return -1;
    }

    private static bool CloseSaddlebag()
    {
        try 
        { 
            var agent = AgentInventory.Instance(); 
            if (agent != null) 
                agent->Hide(); 
            return true; 
        }
        catch { return false; }
    }

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

            string gradeInput = parts[0].Trim();
            var mapData = MapData.Skip(1).FirstOrDefault(m => m.Name.Equals(gradeInput, StringComparison.OrdinalIgnoreCase) ||
                         (int.TryParse(gradeInput, out int grade) && m.Name == $"G{grade}"));
            if (mapData.ItemID == 0)
                return;

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
        public bool AutoDecipherFirst = true;
        public bool MoveToSaddlebag = true;
    }
}

internal static class ImGuiColorExtensions
{
    internal static uint ToUint(this Vector4 color)
    {
        return ImGui.ColorConvertFloat4ToU32(color);
    }
}
