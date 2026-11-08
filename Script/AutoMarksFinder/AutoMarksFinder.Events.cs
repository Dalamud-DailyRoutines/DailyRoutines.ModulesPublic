using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using FFXIVClientStructs.FFXIV.Client.Network;
using GameObjectId = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectId;

namespace DailyRoutines.ModulesPublic;

internal partial class AutoMarksFinder
{
    private Hook<PacketDispatcher.Delegates.HandleSpawnNpcPacket>?     OnSpawnNPCHook;
    private Hook<PacketDispatcher.Delegates.HandleActorControlPacket>? OnActorControlHook;

    private unsafe void OnSpawnNPCDetour(uint targetID, SpawnNpcPacket* packet)
    {
        if (packet == null)
        {
            OnSpawnNPCHook.Original(targetID, packet);
            return;
        }

        var baseID   = packet->Common.BaseId;
        var position = packet->Common.Position;
        var isDead   = packet->Common.CharacterMode == CharacterModes.Dead || packet->Common.HealthPoints == 0;
        OnSpawnNPCHook.Original(targetID, packet);

        aliveMonitor?.ObserveMonster(targetID, baseID, position, isDead);
        if (!isDead)
            scanner?.ObserveMonster(baseID, position);
    }

    private void OnActorControlDetour
    (
        uint         entityID,
        uint         category,
        uint         arg1,
        uint         arg2,
        uint         arg3,
        uint         arg4,
        uint         arg5,
        uint         arg6,
        uint         arg7,
        uint         arg8,
        GameObjectId targetID,
        bool         isRecorded
    )
    {
        OnActorControlHook.Original(entityID, category, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, targetID, isRecorded);

        switch (category)
        {
            case ACTOR_CONTROL_DEATH:
                aliveMonitor?.OnConfirmedDeath(entityID);
                break;
            case ACTOR_CONTROL_SET_MODE:
                aliveMonitor?.OnCharacterModeChanged(entityID);
                break;
        }
    }

    #region 常量

    private const uint ACTOR_CONTROL_SET_MODE = 2;
    private const uint ACTOR_CONTROL_DEATH    = 14;

    #endregion
}
