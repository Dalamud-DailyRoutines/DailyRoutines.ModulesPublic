using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Info.Game.Packets.Upstream;
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
        TaskHelper = new();

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup, "PunchingMachine", OnAddonSetup);
    }
    
    protected override unsafe void Uninit()
    {
        IAddonLifecycle.Instance().UnregisterListener(OnAddonSetup);

        if (PunchingMachine->IsAddonAndNodesReady())
            SendRoundEnd();
    }

    protected override void ConfigUI()
    {
        ImGuiOm.ConflictKeyText();

        ImGui.NewLine();

        using (ImRaii.Disabled
               (
                   GameState.TerritoryType != 144 ||
                   TaskHelper.IsBusy              ||
                   ICondition.Instance().IsOccupiedInEvent
               ))
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Play, Lang.Get("Start")))
                SendInteractWithMachine();
        }

        ImGui.SameLine();

        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Stop, Lang.Get("Stop")))
        {
            TaskHelper.Abort();
            SendRoundEnd();
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
                SendNewRound();
            }
        );
        TaskHelper.Enqueue(() => InventoryManager.Instance()->GetInventoryItemCount(29) != currentMGP);
        TaskHelper.DelayNext(1000);
        TaskHelper.Enqueue
        (() =>
            {
                UpdateSelectStringInfo($"{Lang.Get("AutoCuffACur-WaitingForResult")}......");
                SendPlayGame();
            }
        );
        TaskHelper.DelayNext(3000);
        TaskHelper.Enqueue(SendRoundEnd);
        TaskHelper.Enqueue(() => !ICondition.Instance().IsOccupiedInEvent);
        TaskHelper.Enqueue(SendInteractWithMachine);
    }

    private static unsafe bool WaitSelectStringAddon() =>
        SelectString->IsAddonAndNodesReady() && PunchingMachine->IsAddonAndNodesReady();

    private static void SendInteractWithMachine() =>
        new EventStartPackt(LocalPlayerState.EntityID, EVENT_ID).Send();

    private static void SendNewRound() =>
        new EventActionPacket(EVENT_ID, ROUND_START_CATEGORY).Send();

    private static void SendPlayGame() =>
        new EventActionPacket(EVENT_ID, PLAY_GAME_CATEGORY, 3).Send();
    
    private static void SendRoundEnd() =>
        new EventCompletePackt(EVENT_ID, 14).Send();

    private static unsafe void UpdateSelectStringInfo(string info)
    {
        if (!SelectString->IsAddonAndNodesReady() ||
            !PunchingMachine->IsAddonAndNodesReady())
            return;

        var list = SelectString->GetComponentListById(3);
        var text = SelectString->GetTextNodeById(2);
        if (list == null || text == null) return;

        list->OwnerNode->ToggleVisibility(false);
        list->SetEnabledState(false);

        text->FontSize      = 18;
        text->AlignmentType = AlignmentType.Center;

        using var rented = new RentedSeStringBuilder();
        
        var builder = rented.Builder;
        builder.PushColorType(28)
               .Append($"[{Lang.Get("AutoCuffACurTitle")}]")
               .PopColorType()
               .AppendNewLine()
               .Append(info);

        text->SetText(builder.GetViewAsSpan());
        text->SetPositionFloat(20, 60);
    }

    #region 常量

    private const uint EVENT_ID = 0x240004;
    
    private const uint ROUND_START_CATEGORY = 0x107000E;
    
    private const uint PLAY_GAME_CATEGORY = 0x108000E;

    #endregion
}
