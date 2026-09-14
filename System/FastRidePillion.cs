using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.Interop;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.ExecuteCommand.Implementations;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using GameControl = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace DailyRoutines.ModulesPublic;

public unsafe class FastRidePillion : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("FastRidePillionTitle"),
        Description = Lang.Get("FastRidePillionDescription", COMMAND),
        Category    = ModuleCategory.System
    };

    private Hook<AgentContext.Delegates.OpenContextMenu>? OpenContextMenuHook;

    protected override void Init()
    {
        OpenContextMenuHook = IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(AgentContext.MemberFunctionPointers),
            "OpenContextMenu",
            (AgentContext.Delegates.OpenContextMenu)AgentOpenContextMenuDetour
        );
        OpenContextMenuHook.Enable();

        CommandManager.Instance().AddSubCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("FastRidePillion-Command-Help") });
    }
    
    protected override void Uninit() =>
        CommandManager.Instance().RemoveSubCommand(COMMAND);

    protected override void ConfigUI()
    {
        using var heading = ImRaii.Heading1(Lang.Get("Command"));
        
        ImGui.TextUnformatted($"/pdr {COMMAND} {Lang.Get("FastRidePillion-Command-Help")}");
    }

    private static void OnCommand(string command, string arguments)
    {
        var localPlayer = GameControl.Instance()->LocalPlayer;
        if (localPlayer == null || localPlayer->Mount.MountId != 0)
            return;
        
        var group = GroupManager.Instance()->GetGroup();
        if (group == null)
            return;

        for (var i = 0; i < group->MemberCount; i++)
        {
            var member = group->PartyMembers[i];
            
            var owner  = CharacterManager.Instance()->LookupBattleCharaByEntityId(member.EntityId);
            if (owner == null)
                continue;
            
            var mountObject = owner->Mount.MountObject;
            if (owner->Mount.MountId == 0 || mountObject == null)
                continue;
            
            if (LocalPlayerState.DistanceTo3DSquared(mountObject->Position) > MAX_RIDE_DISTANCE_SQ)
                continue;
            
            if (!TryGetEmptySeat(owner, mountObject, out var seatIndex))
                continue;
            
            TargetCommand.Set(owner->EntityId);
            MountCommand.RidePillion(owner->EntityId, seatIndex);

            var message = Lang.GetSe
            (
                "FastRidePillion-Notification-Rode",
                new PlayerPayload(owner->NameString, owner->HomeWorld),
                seatIndex + 1
            );
            NotifyHelper.Toast(message);
            return;
        }

        var failMessage = Lang.Get("FastRidePillion-Notification-NoAvailable");
        NotifyHelper.ToastError(failMessage);
    }

    private void AgentOpenContextMenuDetour
    (
        AgentContext* agent,
        bool          bindToOwner,
        bool          closeExisting
    )
    {
        if (TryRidePillion(agent))
            return;

        OpenContextMenuHook.Original(agent, bindToOwner, closeExisting);
    }

    private static bool TryRidePillion
    (
        AgentContext* agent
    )
    {
        var localPlayer = GameControl.Instance()->LocalPlayer;
        if (localPlayer == null || localPlayer->Mount.MountId != 0)
            return false;

        var targetObjectID = agent->TargetObjectId;
        if (targetObjectID.ObjectId == 0 || targetObjectID.Type != 0)
            return false;

        var group = GroupManager.Instance()->GetGroup();
        if (group == null || !group->IsEntityIdInParty(targetObjectID.ObjectId))
            return false;

        var owner = CharacterManager.Instance()->LookupBattleCharaByEntityId(targetObjectID.ObjectId);
        if (owner == null)
            return false;

        var mountObject = owner->Mount.MountObject;
        if (owner->Mount.MountId == 0 || mountObject == null)
            return false;

        if (LocalPlayerState.DistanceTo3DSquared(mountObject->Position) > MAX_RIDE_DISTANCE_SQ)
            return false;

        if (!TryGetEmptySeat(owner, mountObject, out var seatIndex))
            return false;

        TargetCommand.Set(owner->EntityId);
        MountCommand.RidePillion(owner->EntityId, seatIndex);

        var message = Lang.GetSe
        (
            "FastRidePillion-Notification-Rode",
            new PlayerPayload(owner->NameString, owner->HomeWorld),
            seatIndex + 1
        );
        NotifyHelper.Toast(message);
        return true;
    }

    private static bool TryGetEmptySeat
    (
        BattleChara* owner,
        Character*   mountObject,
        out uint     seatIndex
    )
    {
        seatIndex = 0;

        if (!LuminaGetter.TryGetRow<Mount>(owner->Mount.MountId, out var mountRow) || mountRow.ExtraSeats == 0)
            return false;

        var charas = CharacterManager.Instance()->BattleCharas;
        for (uint seat = 0; seat < mountRow.ExtraSeats; seat++)
        {
            if (IsSeatOccupied(charas, mountObject, seat))
                continue;

            seatIndex = seat;
            return true;
        }

        return false;
    }

    private static bool IsSeatOccupied
    (
        ReadOnlySpan<Pointer<BattleChara>> charas,
        Character*                         mountObject,
        uint                               seat
    )
    {
        foreach (var charaPointer in charas)
        {
            var chara = charaPointer.Value;
            if (chara == null || chara->Mode != CharacterModes.RidingPillion || chara->ChildObject != mountObject)
                continue;

            if (chara->ModeParam == seat)
                return true;
        }

        return false;
    }

    #region 常量

    private const string COMMAND = "ridepillion";

    private const float MAX_RIDE_DISTANCE_SQ = 25f;

    #endregion
}
