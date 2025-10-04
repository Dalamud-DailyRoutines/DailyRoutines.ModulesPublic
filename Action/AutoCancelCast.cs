using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoCancelCast : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoCancelCastTitle"),
        Description = GetLoc("AutoCancelCastDescription"),
        Category    = ModuleCategories.Action,
    };

    private static readonly HashSet<ObjectKind> ValidObjectKinds = [ObjectKind.Player, ObjectKind.BattleNpc];

    private static readonly HashSet<ConditionFlag> ValidConditions = [ConditionFlag.Casting, ConditionFlag.Casting87];

    private static HashSet<uint> TargetAreaActions { get; } =
        LuminaGetter.Get<LuminaAction>()
                    .Where(x => x.TargetArea)
                    .Select(x => x.RowId).ToHashSet();
    
    private static bool IsOnCasting;

    protected override void Init()
    {
        DService.Condition.ConditionChange += OnConditionChanged;
        FrameworkManager.Reg(OnUpdate);
    }

    private static void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (!ValidConditions.Contains(flag)) return;
        
        IsOnCasting = value;
    }

    private static void OnUpdate(IFramework _)
    {
        if (!IsOnCasting) return;
        if (!IsCasting)
        {
            IsOnCasting = false;
            return;
        }

        var player = DService.ObjectTable.LocalPlayer;
        if (player.CastActionType != ActionType.Action      ||
            TargetAreaActions.Contains(player.CastActionId) ||
            !LuminaGetter.TryGetRow(player.CastActionId, out LuminaAction actionRow))
        {
            IsOnCasting = false;
            return;
        }

        var obj = player.CastTargetObject;
        if (obj is not IBattleChara battleChara || !ValidObjectKinds.Contains(battleChara.ObjectKind)) return;

        if (!battleChara.IsTargetable || (actionRow.DeadTargetBehaviour == 0 && (battleChara.IsDead ||  battleChara.CurrentHp == 0)))
        {
            ExecuteCancast();
            return;
        }
        
        if (ActionManager.CanUseActionOnTarget(player.CastActionId, obj.ToStruct()))
            return;
        
        ExecuteCancast();
        
        return;

        void ExecuteCancast()
        {
            if (Throttler.Throttle("AutoCancelCast-CancelCast", 100))
                ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.CancelCast);
        }
    }

    protected override void Uninit()
    {
        DService.Condition.ConditionChange -= OnConditionChanged;
        FrameworkManager.Unreg(OnUpdate);
    }
}
