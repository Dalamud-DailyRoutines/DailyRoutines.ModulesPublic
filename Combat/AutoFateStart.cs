using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using OmenTools.Threading;
using FateState = Dalamud.Game.ClientState.Fates.FateState;
using TerritoryIntendedUse = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoFateStart : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoFateStartTitle"),
        Description = Lang.Get("AutoFateStartDescription"),
        Category    = ModuleCategory.Combat
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };
    
    private static readonly CompSig HandleSpawnNPCPacketSig = new
    (
        "48 89 5C 24 ?? 57 48 81 EC ?? ?? ?? ?? 48 8B DA 8B F9 E8 ?? ?? ?? ?? 3C ?? 75 ?? E8 ?? ?? ?? ?? 3C ?? 75 ?? 80 BB ?? ?? ?? ?? ?? 75 ?? 8B 05 ?? ?? ?? ?? 39 43 ?? 0F 85 ?? ?? ?? ?? 0F B6 53 ?? 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 0F B6 53 ?? 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8D 44 24 ?? C7 44 24 ?? ?? ?? ?? ?? BA ?? ?? ?? ?? 66 90 48 8D 80 ?? ?? ?? ?? 0F 10 03 0F 10 4B ?? 48 8D 9B ?? ?? ?? ?? 0F 11 40 ?? 0F 10 43 ?? 0F 11 48 ?? 0F 10 4B ?? 0F 11 40 ?? 0F 10 43 ?? 0F 11 48 ?? 0F 10 4B ?? 0F 11 40 ?? 0F 10 43 ?? 0F 11 48 ?? 0F 10 4B ?? 0F 11 40 ?? 0F 11 48 ?? 48 83 EA ?? 75 ?? 0F 10 03"
    );
    private delegate void HandleSpawnNPCPacketDelegate(uint targetID, SpawnNpcPacket* packet);
    private Hook<HandleSpawnNPCPacketDelegate>? HandleSpawnNPCPacketHook;

    protected override void Init()
    {
        HandleSpawnNPCPacketHook ??= HandleSpawnNPCPacketSig.GetHook<HandleSpawnNPCPacketDelegate>(HandleSpawnNPCPacketDetour);

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
    }

    protected override void Uninit() =>
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;

    private void OnZoneChanged(uint u)
    {
        HandleSpawnNPCPacketHook.Disable();

        if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.Overworld || GameState.IsInPVPArea)
            return;

        HandleSpawnNPCPacketHook.Enable();
    }

    private void HandleSpawnNPCPacketDetour(uint targetID, SpawnNpcPacket* packet)
    {
        HandleSpawnNPCPacketHook.Original(targetID, packet);

        if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.Overworld || GameState.IsInPVPArea)
        {
            HandleSpawnNPCPacketHook.Disable();
            return;
        }

        if (LocalPlayerState.ClassJobData.DohDolJobIndex != -1)
            return;

        if (packet->Common.NameId <= 0 || packet->Common.BaseId <= 0 || packet->Common.ObjectKind != ObjectKind.BattleNpc)
            return;

        if (LuminaGetter.GetRow<Fate>(packet->Common.FateId) is not { ClassJobLevel: > 0, Name.IsEmpty: false } row)
            return;

        if (FateManager.Instance()->GetCurrentFateId() == packet->Common.FateId)
            return;

        if (DService.Instance().Fate.FirstOrDefault(x => x.FateId == packet->Common.FateId) is not { State: FateState.Preparation })
            return;

        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.FateStart, row.RowId, targetID);
        if (Throttler.Shared.Throttle($"AutoFateStart-Fate-{row.RowId}", 60_000))
            NotifyHelper.Instance().Chat(Lang.Get("AutoFateStart-StartNotice", row.Name));
    }
}
