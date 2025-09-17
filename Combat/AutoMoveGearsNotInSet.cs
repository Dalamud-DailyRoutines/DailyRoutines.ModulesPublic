using System.Collections.Generic;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using KamiToolKit.Nodes;

namespace DailyRoutines.ModulesPublic;

public class AutoMoveGearsNotInSet : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoMoveGearsNotInSetTitle"),
        Description = GetLoc("AutoMoveGearsNotInSetDescription"),
        Category    = ModuleCategories.Combat
    };

    private const string Command = "retrievegears";

    private static readonly InventoryType[] ArmoryInventories =
    [
        InventoryType.ArmoryOffHand, InventoryType.ArmoryHead, InventoryType.ArmoryBody, InventoryType.ArmoryHands,
        InventoryType.ArmoryWaist, InventoryType.ArmoryLegs, InventoryType.ArmoryFeets, InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings, InventoryType.ArmoryMainHand,
    ];

    private static TextButtonNode? Button;

    protected override void Init()
    {
        CommandManager.AddSubCommand(Command, new(OnCommand) { HelpMessage = GetLoc("AutoMoveGearsNotInSet-CommandHelp") });
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "ArmouryBoard", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "ArmouryBoard", OnAddon);
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        
        Service.AddonController.DetachNode(Button);
        Button = null;
        
        CommandManager.RemoveSubCommand(Command);
    }

    protected override void ConfigUI()
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
    
    private static unsafe void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PostDraw:
                if (ArmouryBoard == null) return;

                if (Button == null)
                {
                    Button = new TextButtonNode
                    {
                        Size      = new(50),
                        Position  = new(12, 515),
                        IsVisible = true,
                        SeString  = new SeStringBuilder().AddIcon(BitmapFontIcon.SwordSheathed).Build(),
                        Tooltip   = GetLoc("AutoMoveGearsNotInSet-Button"),
                        OnClick   = () => ChatHelper.SendMessage($"/pdr {Command}"),
                        IsEnabled = true,
                    };

                    var backgroundNode = (SimpleNineGridNode)Button.BackgroundNode;

                    backgroundNode.TexturePath        = "ui/uld/partyfinder_hr1.tex";
                    backgroundNode.TextureCoordinates = new(38);
                    backgroundNode.TextureSize        = new(32, 34);
                    backgroundNode.LeftOffset         = 0;
                    backgroundNode.RightOffset        = 0f;
                    
                    Service.AddonController.AttachNode(Button, ArmouryBoard->RootNode);
                }
                break;
            case AddonEvent.PreFinalize:
                Service.AddonController.DetachNode(Button);
                Button = null;
                break;
        }
    }

    private static void OnCommand(string command, string args) => 
        EnqueueRetrieve();

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
                
                if (!TryGetFirstInventoryItem(PlayerInventories, x => x.ItemId == 0, out var emptySlot)) goto Out;
                
                manager->MoveItemSlot(type, (ushort)i, emptySlot->Container, (ushort)emptySlot->Slot, true);
                counter++;
            }
        }
        
        Out:
        Chat(GetLoc("AutoMoveGearsNotInSet-Notification", counter));
    }
}
