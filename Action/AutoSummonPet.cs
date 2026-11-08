using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

internal class AutoSummonPet : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoSummonPetTitle"),
        Description = Lang.Get("AutoSummonPetDescription"),
        Category    = ModuleCategory.Action
    };

    public override string ModuleName => nameof(AutoSummonPet);

    protected override void Init()
    {
        TaskHelper ??= new()
        {
            TimeoutMS = 5_000,
            MoveToNextCheckFunc = () =>
            {
                if (ICondition.Instance().IsBetweenAreas    ||
                    ICondition.Instance().IsOccupiedInEvent ||
                    LocalPlayerState.Object is not { IsTargetable: true })
                    return false;

                var summonAction = SummonActions.FirstOrDefault(x => x.ClassJob == LocalPlayerState.ClassJob);

                if (!UIModule.IsScreenReady()                    ||
                    ICondition.Instance()[ConditionFlag.Casting] ||
                    GameState.IsDutyCompleted                    ||
                    summonAction.Action == 0)
                {
                    TaskHelper.Abort();
                    return false;
                }

                return true;
            }
        };

        IClientState.Instance().TerritoryChanged += OnZoneChanged;
        IDutyState.Instance().DutyRecommenced    += OnDutyRecommenced;
    }

    protected override void Uninit()
    {
        IDutyState.Instance().DutyRecommenced    -= OnDutyRecommenced;
        IClientState.Instance().TerritoryChanged -= OnZoneChanged;
    }

    // 重新挑战
    private void OnDutyRecommenced
    (
        IDutyStateEventArgs args
    )
    {
        TaskHelper.Abort();
        TaskHelper.Enqueue(SummonPet);
    }

    // 进入副本
    private void OnZoneChanged
    (
        uint zone
    )
    {
        if (!GameState.IsInPVEActonZone)
            return;

        TaskHelper.Abort();
        TaskHelper.DelayNext(1_000);
        TaskHelper.Enqueue(SummonPet);
    }

    private unsafe bool SummonPet()
    {
        if (LocalPlayerState.Object is not { } localPlayer)
            return false;

        var summonAction = SummonActions.FirstOrDefault(x => x.ClassJob == LocalPlayerState.ClassJob);

        if (summonAction.Action == 0)
        {
            TaskHelper.Abort();
            return true;
        }

        if (CharacterManager.Instance()->LookupPetByOwnerObject(localPlayer.ToStruct()) != null)
        {
            TaskHelper.Abort();
            return true;
        }

        TaskHelper.Enqueue(() => UseActionManager.Instance().UseAction(ActionType.Action, summonAction.Action));
        TaskHelper.DelayNext(1_000);
        TaskHelper.Enqueue(SummonPet);
        return true;
    }

    #region 常量

    private static readonly (uint ClassJob, uint Action)[] SummonActions =
    [
        (28, 17215), // 学者
        (26, 25798), // 秘术师 / 召唤师
        (27, 25798)
    ];

    #endregion
}
