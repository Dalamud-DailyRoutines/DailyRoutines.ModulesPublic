using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.OmenService;
using OmenTools.Threading.TaskHelper;

namespace DailyRoutines.ModulesPublic;

public class AutoSummonPet : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoSummonPetTitle"),
        Description = Lang.Get("AutoSummonPetDescription"),
        Category    = ModuleCategory.Action
    };

    protected override void Init()
    {
        TaskHelper ??= new TaskHelper { TimeoutMS = 30_000 };

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        DService.Instance().DutyState.DutyRecommenced    += OnDutyRecommenced;
    }
    
    protected override void Uninit()
    {
        DService.Instance().DutyState.DutyRecommenced    -= OnDutyRecommenced;
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
    }

    // 重新挑战
    private void OnDutyRecommenced(object? sender, ushort e)
    {
        TaskHelper.Abort();
        TaskHelper.Enqueue(CheckCurrentJob);
    }

    // 进入副本
    private void OnZoneChanged(ushort zone)
    {
        TaskHelper.Abort();

        if (!GameState.IsInPVEActonZone) return;

        TaskHelper.DelayNext(1_000);
        TaskHelper.Enqueue(CheckCurrentJob);
    }

    private unsafe bool CheckCurrentJob()
    {
        if (DService.Instance().Condition.IsBetweenAreas         ||
            !UIModule.IsScreenReady()                            ||
            DService.Instance().Condition[ConditionFlag.Casting] ||
            DService.Instance().ObjectTable.LocalPlayer is not { IsTargetable: true } localPlayer) return false;

        if (!SummonActions.TryGetValue(LocalPlayerState.ClassJob, out var actionID))
        {
            TaskHelper.Abort();
            return true;
        }

        var state = CharacterManager.Instance()->LookupPetByOwnerObject(localPlayer.ToStruct()) != null;

        if (state)
        {
            TaskHelper.Abort();
            return true;
        }

        TaskHelper.Enqueue(() => UseActionManager.Instance().UseAction(ActionType.Action, actionID));
        TaskHelper.DelayNext(1_000);
        TaskHelper.Enqueue(CheckCurrentJob);
        return true;
    }
    
    #region 常量

    private static readonly FrozenDictionary<uint, uint> SummonActions = new Dictionary<uint, uint>()
    {
        [28] = 17215, // 学者
        [26] = 25798, // 秘术师 / 召唤师
        [27] = 25798
    }.ToFrozenDictionary();

    #endregion
}
