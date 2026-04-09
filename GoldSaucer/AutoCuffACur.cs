using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Interop.Game.Models.Packets.Upstream;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoCuffACur : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoCuffACurTitle"),
        Description = Lang.Get("AutoCuffACurDescription"),
        Category    = ModuleCategory.GoldSaucer
    };

    protected override void Init()
    {
        TaskHelper ??= new();

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "PunchingMachine", OnAddonSetup);
    }
    
    protected override unsafe void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonSetup);

        if (PunchingMachine->IsAddonAndNodesReady())
            new EventCompletePackt(2359300, 14).Send();
    }

    protected override void ConfigUI()
    {
        ImGuiOm.ConflictKeyText();

        ImGui.NewLine();

        using (ImRaii.Disabled(GameState.TerritoryType != 144 || TaskHelper.IsBusy || DService.Instance().Condition.IsOccupiedInEvent))
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Play, Lang.Get("Start")))
                EnqueueNewRound();
        }

        ImGui.SameLine();

        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Stop, Lang.Get("Stop")))
        {
            TaskHelper.Abort();
            new EventCompletePackt(2359300, 14).Send();
        }
    }

    private unsafe void OnAddonSetup(AddonEvent type, AddonArgs args)
    {
        if (TaskHelper.AbortByConflictKey(this)) return;

        var currentMGP = 0;

        TaskHelper.Abort();
        TaskHelper.Enqueue(WaitSelectStringAddon);
        TaskHelper.Enqueue
        (() =>
            {
                UpdateSelectStringInfo(Lang.Get("AutoCuffACur-StartingRound"));

                currentMGP = InventoryManager.Instance()->GetInventoryItemCount(29);
                new EventActionPacket(2359300, 17235982).Send();
            }
        );
        TaskHelper.Enqueue(() => InventoryManager.Instance()->GetInventoryItemCount(29) != currentMGP);
        TaskHelper.DelayNext(1000);
        TaskHelper.Enqueue
        (() =>
            {
                new EventActionPacket(2359300, 17301518, 3).Send();
                UpdateSelectStringInfo($"{Lang.Get("AutoCuffACur-WaitingForResult")}......");
            }
        );
        TaskHelper.DelayNext(3000);
        TaskHelper.Enqueue(() => new EventCompletePackt(2359300, 14).Send());
        TaskHelper.Enqueue(EnqueueNewRound);
    }

    private static unsafe bool WaitSelectStringAddon() =>
        SelectString->IsAddonAndNodesReady() && PunchingMachine->IsAddonAndNodesReady();

    private bool EnqueueNewRound()
    {
        if (TaskHelper.AbortByConflictKey(this)) return true;
        if (DService.Instance().Condition.IsOccupiedInEvent) return false;

        new EventStartPackt(LocalPlayerState.EntityID, 2359300).Send();
        return true;
    }

    private static unsafe void UpdateSelectStringInfo(string info)
    {
        if (!SelectString->IsAddonAndNodesReady() || !PunchingMachine->IsAddonAndNodesReady()) return;

        var list = SelectString->GetComponentListById(3);
        var text = SelectString->GetTextNodeById(2);
        if (list == null || text == null) return;

        list->OwnerNode->ToggleVisibility(false);
        list->SetEnabledState(false);

        text->FontSize      = 18;
        text->AlignmentType = AlignmentType.Center;

        var builder = new SeStringBuilder();
        builder.AddUiForeground(28);
        builder.AddText($"[{Lang.Get("AutoCuffACurTitle")}]");
        builder.AddUiForegroundOff();
        builder.Add(NewLinePayload.Payload);
        builder.AddText(info);

        text->SetText(builder.Build().EncodeWithNullTerminator());
        text->SetPositionFloat(20, 60);
    }
}
