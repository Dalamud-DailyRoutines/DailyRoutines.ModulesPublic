using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using KamiToolKit.Nodes;
using KamiToolKit.Premade.Node.Simple;
using OmenTools.Info.Game.Data;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoMoveGearsNotInSet : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoMoveGearsNotInSetTitle"),
        Description = Lang.Get("AutoMoveGearsNotInSetDescription"),
        Category    = ModuleCategory.UIOptimization
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private TextButtonNode? button;

    protected override void Init()
    {
        CommandManager.Instance().AddSubCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("AutoMoveGearsNotInSet-CommandHelp") });

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "ArmouryBoard", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "ArmouryBoard", OnAddon);
    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
        OnAddon(AddonEvent.PreFinalize, null);

        CommandManager.Instance().RemoveSubCommand(COMMAND);
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Command")}:");

        ImGui.SameLine();
        ImGui.TextUnformatted($"/pdr {COMMAND} → {Lang.Get("AutoMoveGearsNotInSet-CommandHelp")}");

        ImGui.Spacing();

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoMoveGearsNotInSet-MannualRetrieve")}:");

        ImGui.SameLine();
        if (ImGui.Button(Lang.Get("Confirm")))
            EnqueueRetrieve();
    }

    private unsafe void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PostDraw:
                if (ArmouryBoard == null) return;

                if (button == null)
                {
                    button = new TextButtonNode
                    {
                        Size        = new(48),
                        Position    = new(12, 500),
                        IsVisible   = true,
                        String      = new SeStringBuilder().AddIcon(BitmapFontIcon.SwordSheathed).Build().Encode(),
                        TextTooltip = Lang.Get("AutoMoveGearsNotInSet-Button"),
                        OnClick     = () => ChatManager.Instance().SendMessage($"/pdr {COMMAND}"),
                        IsEnabled   = true
                    };

                    var backgroundNode = (SimpleNineGridNode)button.BackgroundNode;

                    backgroundNode.TexturePath        = "ui/uld/partyfinder_hr1.tex";
                    backgroundNode.TextureCoordinates = new(38);
                    backgroundNode.TextureSize        = new(32, 34);
                    backgroundNode.LeftOffset         = 0;
                    backgroundNode.RightOffset        = 0f;

                    button.AttachNode(ArmouryBoard->RootNode);
                }

                break;
            case AddonEvent.PreFinalize:
                button?.Dispose();
                button = null;
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

        foreach (var type in Inventories.Armory)
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

                if (!Inventories.Player.TryGetFirstItem(x => x.ItemId == 0, out var emptySlot)) goto Out;

                manager->MoveItemSlot(type, (ushort)i, emptySlot->Container, (ushort)emptySlot->Slot, true);
                counter++;
            }
        }

        Out:
        NotifyHelper.Instance().Chat(Lang.Get("AutoMoveGearsNotInSet-Notification", counter));
    }

    #region 常量

    private const string COMMAND = "retrievegears";

    #endregion
}
