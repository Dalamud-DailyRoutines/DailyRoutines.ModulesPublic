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

public class AutoMonsterToss : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoMonsterTossTitle"),
        Description = Lang.Get("AutoMonsterTossDescription"),
        Category    = ModuleCategory.GoldSaucer
    };

    protected override void Init()
    {
        TaskHelper ??= new();

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "BasketBall", OnAddonSetup);
    }
    
    protected override unsafe void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonSetup);

        if (BasketBall->IsAddonAndNodesReady())
            new EventCompletePackt(0x240001, 14).Send();
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
            new EventCompletePackt(0x240001, 14).Send();
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
                UpdateSelectStringInfo(Lang.Get("AutoMonsterToss-StartingGame"));

                currentMGP = InventoryManager.Instance()->GetInventoryItemCount(29);
                new EventActionPacket(0x240001, 0x107000E).Send();
            }
        );
        TaskHelper.Enqueue(() => InventoryManager.Instance()->GetInventoryItemCount(29) != currentMGP);
        TaskHelper.DelayNext(1000);
        TaskHelper.Enqueue
        (() =>
            {
                new EventActionPacket(0x240001, 0x108000E, 1).Send();
                new EventActionPacket(0x240001, 0x108000E, 1).Send();
                new EventActionPacket(0x240001, 0x108000E, 1).Send();
                new EventActionPacket(0x240001, 0x108000E, 1).Send();
                new EventActionPacket(0x240001, 0x108000E, 1).Send();
            }
        );

        const int maxTime = 25;

        for (var i = 0; i < maxTime; i++)
        {
            var second = i;
            TaskHelper.DelayNext(1000);
            TaskHelper.Enqueue(() => UpdateSelectStringInfo(Lang.Get("AutoMonsterToss-WaitingForResult", maxTime - second)));
        }

        TaskHelper.Enqueue(() => new EventCompletePackt(0x240001, 14).Send());
        TaskHelper.Enqueue(EnqueueNewRound);
    }

    private bool EnqueueNewRound()
    {
        if (TaskHelper.AbortByConflictKey(this)) return true;
        if (DService.Instance().Condition.IsOccupiedInEvent) return false;

        new EventStartPackt(LocalPlayerState.EntityID, 0x240001).Send();
        return true;
    }

    private static unsafe bool WaitSelectStringAddon() =>
        SelectString->IsAddonAndNodesReady() && BasketBall->IsAddonAndNodesReady();

    private static unsafe void UpdateSelectStringInfo(string info)
    {
        if (!SelectString->IsAddonAndNodesReady() || !BasketBall->IsAddonAndNodesReady()) return;

        var list = SelectString->GetComponentListById(3);
        var text = SelectString->GetTextNodeById(2);
        if (list == null || text == null) return;

        list->OwnerNode->ToggleVisibility(false);
        list->SetEnabledState(false);

        text->FontSize      = 18;
        text->AlignmentType = AlignmentType.Center;

        var builder = new SeStringBuilder();
        builder.AddUiForeground(28);
        builder.AddText($"[{Lang.Get("AutoMonsterTossTitle")}]");
        builder.AddUiForegroundOff();
        builder.Add(NewLinePayload.Payload);
        builder.AddText(info);

        text->SetText(builder.Encode());
        text->SetPositionFloat(20, 60);
    }
}
