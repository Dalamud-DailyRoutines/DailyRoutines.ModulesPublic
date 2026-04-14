using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoFateSync : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoFateSyncTitle"),
        Description = Lang.Get("AutoFateSyncDescription"),
        Category    = ModuleCategory.Combat
    };
    
    private Config config = null!;

    private CancellationTokenSource? cancelSource;

    protected override void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 30_000 };

        config = Config.Load(this) ?? new();

        cancelSource ??= new();

        GameState.Instance().EnterFate += OnEnterFate;
    }

    protected override void Uninit()
    {
        GameState.Instance().EnterFate -= OnEnterFate;
        FrameworkManager.Instance().Unreg(OnFlying);

        if (cancelSource != null)
        {
            if (!cancelSource.IsCancellationRequested)
                cancelSource.Cancel();
            cancelSource.Dispose();
            cancelSource = null;
        }
    }

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(50f * GlobalUIScale);
        if (ImGui.InputFloat(Lang.Get("AutoFateSync-Delay"), ref config.Delay, format: "%.1f"))
            config.Delay = Math.Max(0, config.Delay);

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.Save(this);
            cancelSource.Cancel();
        }

        ImGuiOm.HelpMarker(Lang.Get("AutoFateSync-DelayHelp"));

        if (ImGui.Checkbox(Lang.Get("AutoFateSync-IgnoreMounting"), ref config.IgnoreMounting))
            config.Save(this);
        ImGuiOm.HelpMarker(Lang.Get("AutoFateSync-IgnoreMountingHelp"));

        if (ImGui.Checkbox(Lang.Get("AutoFateSync-AutoTankStance"), ref config.AutoTankStance))
            config.Save(this);
    }

    private void OnEnterFate(uint fateID) =>
        HandleFateEnter();

    private unsafe void HandleFateEnter()
    {
        if (config.IgnoreMounting && (DService.Instance().Condition[ConditionFlag.InFlight] || DService.Instance().Condition.IsOnMount))
        {
            FrameworkManager.Instance().Reg(OnFlying, 500);
            return;
        }

        var manager = FateManager.Instance();

        if (config.Delay > 0)
        {
            DService.Instance().Framework.RunOnTick
            (
                () =>
                {
                    if (manager->CurrentFate == null || DService.Instance().ObjectTable.LocalPlayer == null) return;

                    ExecuteFateLevelSync(manager->CurrentFate->FateId);
                },
                TimeSpan.FromSeconds(config.Delay),
                0,
                cancelSource.Token
            );

            return;
        }

        ExecuteFateLevelSync(manager->CurrentFate->FateId);
    }

    private unsafe void OnFlying(IFramework _)
    {
        var currentFate = FateManager.Instance()->CurrentFate;

        if (currentFate == null || DService.Instance().ObjectTable.LocalPlayer == null)
        {
            FrameworkManager.Instance().Unreg(OnFlying);
            return;
        }

        if (DService.Instance().Condition[ConditionFlag.InFlight] || DService.Instance().Condition.IsOnMount) return;

        ExecuteFateLevelSync(currentFate->FateId);
        FrameworkManager.Instance().Unreg(OnFlying);
    }

    private unsafe void ExecuteFateLevelSync(ushort fateID)
    {
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.FateLevelSync, fateID, 1);

        TaskHelper.Abort();

        if (config.AutoTankStance)
        {
            TaskHelper.Enqueue
            (() => !DService.Instance().Condition.IsOnMount              &&
                   !DService.Instance().Condition[ConditionFlag.Jumping] &&
                   ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, 2) == 0
            );
            TaskHelper.Enqueue
            (() =>
                {
                    if (FateManager.Instance()->CurrentFate == null ||
                        !LuminaGetter.TryGetRow<Fate>(fateID, out var data))
                        return true;
                    if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer)
                        return false;
                    if (!TankStanceActions.TryGetValue(localPlayer.ClassJob.RowId, out var jobInfo))
                        return false;
                    if (localPlayer.Level > data.ClassJobLevelMax)
                        return false;
                    if (LocalPlayerState.HasStatus(jobInfo.StatusID, out _))
                        return true;

                    UseActionManager.Instance().UseAction(ActionType.Action, jobInfo.ActionID);
                    return true;
                }
            );
        }
    }

    private class Config : ModuleConfig
    {
        public bool  AutoTankStance;
        public float Delay          = 3f;
        public bool  IgnoreMounting = true;
    }
    
    #region 常量

    private static readonly FrozenDictionary<uint, (uint ActionID, uint StatusID)> TankStanceActions = 
        new Dictionary<uint, (uint ActionID, uint StatusID)>
        {
            // 剑术师 / 骑士
            [1]  = (28, 79),
            [19] = (28, 79),
            // 斧术师 / 战士
            [3]  = (48, 91),
            [21] = (48, 91),
            // 暗黑骑士
            [32] = (3629, 743),
            // 绝枪战士
            [37] = (16142, 1833)
        }.ToFrozenDictionary();

    #endregion
}
