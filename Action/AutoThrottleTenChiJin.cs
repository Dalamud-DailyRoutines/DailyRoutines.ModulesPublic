using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using System.Collections.Generic;

namespace DailyRoutines.Modules;

public unsafe class AutoThrottleTenChiJin : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoThrottleTenChiJinTitle"),
        Description = GetLoc("AutoThrottleTenChiJinDescription"),
        Category = ModuleCategories.Action,
    };

    private static readonly Throttler<uint> ShinobiThrottler = new();

    private static readonly Dictionary<uint, uint> ShinobiActions = new()
    {
        [2259] = 18805,
        [2261] = 18806,
        [2263] = 18807,
    };

    private static readonly Dictionary<uint, uint> ShinobiActionsReversed = new()
    {
        [18805] = 2259,
        [18806] = 2261,
        [18807] = 2263,
    };

    private static bool IsDisableOtherActions = true;

    public override void Init()
    {
        AddConfig(nameof(IsDisableOtherActions), true);
        IsDisableOtherActions = GetConfig<bool>(nameof(IsDisableOtherActions));

        UseActionManager.Register(OnPreUseAction);
        UseActionManager.Register(OnPreIsActionOffCooldown);
    }

    public override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("AutoThrottleTenChiJin-DisableOtherActions"), ref IsDisableOtherActions))
            UpdateConfig(nameof(IsDisableOtherActions), IsDisableOtherActions);
    }

    private static void OnPreUseAction(
        ref bool isPrevented,
        ref ActionType actionType, ref uint actionID, ref ulong targetID, ref uint extraParam,
        ref ActionManager.UseActionMode queueState, ref uint comboRouteID, ref bool* outOptAreaTargeted)
    {
        if (actionType != ActionType.Action) return;

        var manager = ActionManager.Instance();
        if (manager == null) return;

        // 忍术
        if (actionID == 2260)
        {
            if (manager->GetActionStatus(actionType, actionID) != 0) 
                ShinobiThrottler.Clear();
            return;
        }

        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null || localPlayer->ClassJob != 30) return;

        var isShinobiStart = ShinobiActions.TryGetValue(actionID, out var counterpartProcess);
        var isShinobiProcessing = ShinobiActionsReversed.TryGetValue(actionID, out var counterpartStart);

        if (IsDisableOtherActions && localPlayer->StatusManager.HasStatus(496) &&
            !isShinobiStart && !isShinobiProcessing)
        {
            isPrevented = true;
            return;
        }

        if (!isShinobiProcessing && !isShinobiStart) return;

        // 还在 GCD
        var adjustedID = manager->GetAdjustedActionId(actionID);
        if (manager->GetActionStatus(actionType, adjustedID) != 0) return;

        isPrevented = !ShinobiThrottler.Throttle(actionID, 5_500) |
                      !ShinobiThrottler.Throttle(isShinobiStart ? counterpartProcess : counterpartStart, 5_500);
    }

    private static void OnPreIsActionOffCooldown(
        ref bool isPrevented, ActionType actionType, uint actionID, ref float queueTime)
    {
        if (actionType != ActionType.Action) return;
        if (!ShinobiActions.ContainsKey(actionID) && !ShinobiActionsReversed.ContainsKey(actionID)) return;

        queueTime = 0.1f;
    }

    public override void Uninit()
    {
        UseActionManager.Unregister(OnPreUseAction);
        UseActionManager.Unregister(OnPreIsActionOffCooldown);

        ShinobiThrottler.Clear();
        base.Uninit();
    }
}
