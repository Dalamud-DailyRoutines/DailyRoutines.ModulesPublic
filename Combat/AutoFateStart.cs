using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;
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

    protected override void Init() =>
        FrameworkManager.Instance().Reg(OnUpdate, throttleMS: 2_000);
    
    protected override void Uninit() =>
        FrameworkManager.Instance().Unreg(OnUpdate);

    private static void OnUpdate(IFramework framework)
    {
        if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.Overworld ||
            GameState.IsInPVPArea                                            ||
            LocalPlayerState.ClassJobData.DohDolJobIndex != -1               ||
            DService.Instance().Condition[ConditionFlag.InCombat]            ||
            FateManager.Instance()->CurrentFate != null)
            return;

        foreach (var gameObjectID in FateManager.Instance()->FateStartNPCs)
        {
            var entityID = gameObjectID.ObjectId;

            var chara = CharacterManager.Instance()->LookupBattleCharaByEntityId(entityID);
            if (chara == null || chara->FateId == 0) continue;
            
            ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.StartFate, chara->FateId, entityID);
            if (Throttler.Shared.Throttle($"AutoFateStart-Fate-{chara->FateId}", 60_000))
                NotifyHelper.Instance().Chat(Lang.Get("AutoFateStart-StartNotice", LuminaWrapper.GetFateName(chara->FateId)));
        }
    }
}
