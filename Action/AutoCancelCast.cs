using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.Info.Game.Data;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoCancelCast : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoCancelCastTitle"),
        Description = Lang.Get("AutoCancelCastDescription"),
        Category    = ModuleCategory.Action
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    protected override void Init() =>
        DService.Instance().Condition.ConditionChange += OnConditionChanged;

    protected override void Uninit()
    {
        DService.Instance().Condition.ConditionChange -= OnConditionChanged;
        FrameworkManager.Instance().Unreg(OnUpdate);
    }

    private static void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (!ValidConditions.Contains(flag)) return;

        if (value)
            FrameworkManager.Instance().Reg(OnUpdate);
        else
            FrameworkManager.Instance().Unreg(OnUpdate);
    }

    private static void OnUpdate(IFramework _)
    {
        if (!DService.Instance().Condition.IsCasting)
        {
            FrameworkManager.Instance().Unreg(OnUpdate);
            return;
        }

        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return;

        if (localPlayer.CastActionType != ActionType.Action                ||
            Sheets.TargetAreaActions.ContainsKey(localPlayer.CastActionID) ||
            !LuminaGetter.TryGetRow(localPlayer.CastActionID, out LuminaAction actionRow))
        {
            FrameworkManager.Instance().Unreg(OnUpdate);
            return;
        }

        var obj = localPlayer.CastTargetObject;

        if (obj is not IBattleChara battleChara || !ValidObjectKinds.Contains(battleChara.ObjectKind))
        {
            FrameworkManager.Instance().Unreg(OnUpdate);
            return;
        }

        if (!battleChara.IsTargetable)
        {
            ExecuteCancast();
            return;
        }

        if (actionRow.DeadTargetBehaviour == 0 && (battleChara.IsDead || battleChara.CurrentHp == 0))
        {
            ExecuteCancast();
            return;
        }

        if (ActionManager.CanUseActionOnTarget(localPlayer.CastActionID, obj.ToStruct()))
            return;

        ExecuteCancast();

        return;

        void ExecuteCancast()
        {
            if (Throttler.Shared.Throttle("AutoCancelCast-CancelCast", 100))
                ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.CancelCast);
        }
    }
    
    #region 常量
    
    private static readonly FrozenSet<ObjectKind> ValidObjectKinds =
    [
        ObjectKind.Pc,
        ObjectKind.BattleNpc
    ];

    private static readonly FrozenSet<ConditionFlag> ValidConditions =
    [
        ConditionFlag.Casting
    ];
    
    #endregion
}
