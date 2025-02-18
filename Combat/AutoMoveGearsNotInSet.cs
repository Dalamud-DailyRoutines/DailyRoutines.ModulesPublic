using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using System.Collections.Generic;

namespace DailyRoutines.Modules;

public class AutoMoveGearsNotInSet : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title       = GetLoc("AutoMoveGearsNotInSetTitle"),
        Description = GetLoc("AutoMoveGearsNotInSetDescription"),
        Category    = ModuleCategories.Combat
    };

    private const string Command = "retrievegears";
    
    private static readonly InventoryType[] ArmoryInventories =
    [
        InventoryType.ArmoryOffHand, InventoryType.ArmoryHead, InventoryType.ArmoryBody,
        InventoryType.ArmoryHands, InventoryType.ArmoryWaist, InventoryType.ArmoryLegs, InventoryType.ArmoryFeets,
        InventoryType.ArmoryEar, InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
        InventoryType.ArmoryMainHand,
    ];

    private static readonly InventoryType[] BagInventories =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4
    ];

    public override void Init()
    {
        CommandManager.AddSubCommand(
            Command, new(OnCommand) { HelpMessage = GetLoc("AutoMoveGearsNotInSet-CommandHelp") });
    }

    public override void ConfigUI()
    {
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("Command")}:");
        
        ImGui.SameLine();
        ImGui.Text($"/pdr {Command} → {GetLoc("AutoMoveGearsNotInSet-CommandHelp")}");
        
        ImGui.Spacing();
        
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("AutoMoveGearsNotInSet-MannualRetrieve")}:");
        
        ImGui.SameLine();
        if (ImGui.Button(GetLoc("Confirm")))
            EnqueueRetrieve();
    }

    private static void OnCommand(string command, string args) => EnqueueRetrieve();

    private static unsafe void EnqueueRetrieve()
    {
        var module  = RaptureGearsetModule.Instance();
        var manager = InventoryManager.Instance();
        
        HashSet<uint> gearsetItemIDs = [];
        foreach (var entry in module->Entries)
        {
            foreach (var item in entry.Items)
            {
                if (item.ItemId == 0) continue;
                gearsetItemIDs.Add(item.ItemId);
            }
        }

        var counter = 0;
        foreach (var type in ArmoryInventories)
        {
            var container = manager->GetInventoryContainer(type);
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0) continue;
                
                var itemID = slot->ItemId;
                if (slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality))
                    itemID += 100_0000;
                if (gearsetItemIDs.Contains(itemID)) continue;
                
                if (!TryGetFirstInventoryItem(BagInventories, x => x.ItemId == 0, out var emptySlot)) goto Out;
                
                manager->MoveItemSlot(type, (ushort)i, emptySlot->Container, (ushort)emptySlot->Slot, 1);
                counter++;
            }
        }
        
        Out:
        if (counter > 0)
            NotificationInfo(GetLoc("AutoMoveGearsNotInSet-Notification", counter));
    }
}
