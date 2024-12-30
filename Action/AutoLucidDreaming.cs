using System.Collections.Generic;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using Microsoft.VisualBasic.Logging;

namespace DailyRoutines.LucidDreaming;

public class AutoPLucidDreaming : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title       = GetLoc("AutoLucidDreamingTitle"),       // "自动释放醒梦"
        Description = GetLoc("AutoLucidDreamingDescription"), // "使用指定职业时，自动尝试释放醒梦（仅在蓝量低于8000时）"
        Category    = ModuleCategories.Action,
        Author      = ["Wotou"]
    };

    private readonly HashSet<uint> Jobs = [6, 7, 24, 25, 26, 27, 28, 33, 35, 36, 40, 42];

    private readonly uint LucidDreamingActionId = 7562;
    private Configs Config = null!;

    public override void Init()
    {
        TaskHelper ??= new TaskHelper { TimeLimitMS = 30_000 };
        Config     =   LoadConfig<Configs>() ?? new();

        DService.ClientState.TerritoryChanged += OnTerritoryChanged;
        DService.DutyState.DutyRecommenced    += OnDutyRecommenced;
        DService.Condition.ConditionChange    += OnConditionChanged;
        DService.ClientState.LevelChanged     += OnLevelChanged;
        DService.ClientState.ClassJobChanged  += OnClassJobChanged;
        
        TaskHelper.Enqueue(MainProcess);
    }

    public override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("AutoPeloton-OnlyInDuty"), ref Config.OnlyInDuty)) // "只在副本中使用"
        {
            SaveConfig(Config);
            TaskHelper.Abort();
            TaskHelper.Enqueue(MainProcess);
        }

        if (ImGui.Checkbox(GetLoc("AutoLucidDreaming-OnlyInCombat"), ref Config.OnlyInCombat)) // "只在战斗中使用"
        {
            SaveConfig(Config);
            TaskHelper.Abort();
            TaskHelper.Enqueue(MainProcess);
        }
        ImGui.SetNextItemWidth(125f * GlobalFontScale);
        if (ImGui.InputInt(GetLoc("AutoLucidDreaming-LucidDreamingThreshold"), ref Config.LucidDreamingThreshold))
        {
            SaveConfig(Config);
            TaskHelper.Abort();
            TaskHelper.Enqueue(MainProcess);
        }
    }

    public override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnTerritoryChanged;
        DService.DutyState.DutyRecommenced    -= OnDutyRecommenced;
        DService.Condition.ConditionChange    -= OnConditionChanged;
        DService.ClientState.LevelChanged     -= OnLevelChanged;
        DService.ClientState.ClassJobChanged  -= OnClassJobChanged;
        if (Config != null) SaveConfig(Config);
        
        base.Uninit();
    }

    private void OnDutyRecommenced(object? sender, ushort e)
    {
        TaskHelper.Abort();
        TaskHelper.Enqueue(MainProcess);
    }

    private unsafe void OnTerritoryChanged(ushort zone)
    {
        TaskHelper.Abort();
        if (Config.OnlyInDuty && GameMain.Instance()->CurrentContentFinderConditionId == 0) return;
        TaskHelper.Enqueue(MainProcess);
    }

    private void OnLevelChanged(uint classJobId, uint level)
    {
        TaskHelper.Abort();
        if (level < 14) return;
        TaskHelper.Enqueue(MainProcess);
    }

    private void OnClassJobChanged(uint classJobId)
    {
        TaskHelper.Abort();
        if (!Jobs.Contains(classJobId)) return;
        TaskHelper.Enqueue(MainProcess);
    }

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag is not ConditionFlag.InCombat) return;
        TaskHelper.Abort();
        if (!value) TaskHelper.Enqueue(MainProcess);
    }

    private bool Cycle(int delayMs = 0)
    {
        if (delayMs > 0) TaskHelper.DelayNext(delayMs);
        TaskHelper.Enqueue(MainProcess);
        return true;
    }

    private unsafe bool? MainProcess()
    {
        if (DService.ClientState.LocalPlayer is not { } localPlayer) return Cycle(1_000);
        if (localPlayer.CurrentMp >= Config.LucidDreamingThreshold) return Cycle(1_000);
        if (!DService.Condition[ConditionFlag.InCombat] && Config.OnlyInCombat) return Cycle(1_000);

        if (!Jobs.Contains(localPlayer.ClassJob.Id)) return true;
        if (Config.OnlyInDuty && GameMain.Instance()->CurrentContentFinderConditionId == 0) return true;
        if (GameMain.IsInPvPArea() || GameMain.IsInPvPInstance()) return true;
        
        TaskHelper.Enqueue(UseLucidDreaming, "UseLucidDreaming", 1_000, true, 1);
        return Cycle(1_000);
    }

    private unsafe bool? UseLucidDreaming()
    {
        if (DService.ClientState.LocalPlayer is not { } localPlayer) return false;
        
        var actionManager = ActionManager.Instance();
        if (actionManager->GetActionStatus(ActionType.Action, LucidDreamingActionId) != 0) return true;
        
        TaskHelper.Enqueue(() => UseActionManager.UseAction(ActionType.Action, LucidDreamingActionId),
                           $"UseAction_{LucidDreamingActionId}", 1_000, true, 1);
        return true;
    }

    private class Configs : ModuleConfiguration
    {
        public bool OnlyInDuty             = false;
        public bool OnlyInCombat           = true;
        public int  LucidDreamingThreshold = 8000;
    }
}
