using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;

namespace DailyRoutines.Modules;

public class AutoFateSync : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoFateSyncTitle"),
        Description = GetLoc("AutoFateSyncDescription"),
        Category    = ModuleCategories.Combat,
    };

    private static readonly Throttler<string> Throttler = new();
    private static Config ModuleConfig = null!;

    private static readonly CompSig FateDirectorSetupSig = new("E8 ?? ?? ?? ?? 48 39 37");
    private delegate nint FateDirectorSetupDelegate(uint rowID, nint a2, nint a3);
    private static Hook<FateDirectorSetupDelegate>? FateDirectorSetupHook;

    private static CancellationTokenSource? CancelSource;

    private static readonly uint[] TankStanceStatuses = [79, 91, 743, 1833];
    private static readonly Dictionary<uint, uint> TankStanceActions = new()
    {
        // 剑术师 / 骑士
        { 1, 28 },
        { 19, 28 },
        // 斧术师 / 战士
        { 3, 48 },
        { 21, 48 },
        // 暗黑骑士
        { 32, 3629 },
        // 绝枪战士
        { 37, 16142 }
    };
    
    public override void Init()
    {
        TaskHelper ??= new TaskHelper { TimeLimitMS = 30_000 };
        ModuleConfig = LoadConfig<Config>() ?? new();
        CancelSource ??= new();
        
        FateDirectorSetupHook ??= 
            DService.Hook.HookFromSignature<FateDirectorSetupDelegate>(FateDirectorSetupSig.Get(), FateDirectorSetupDetour);
        FateDirectorSetupHook.Enable();
    }

    public override void ConfigUI()
    {
        ImGui.SetNextItemWidth(50f * GlobalFontScale);
        if (ImGui.InputFloat(GetLoc("AutoFateSync-Delay"), ref ModuleConfig.Delay, 0, 0, "%.1f"))
            ModuleConfig.Delay = Math.Max(0, ModuleConfig.Delay);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            SaveConfig(ModuleConfig);
            CancelSource.Cancel();
        }
        ImGuiOm.HelpMarker(GetLoc("AutoFateSync-DelayHelp"));

        if (ImGui.Checkbox(GetLoc("AutoFateSync-IgnoreMounting"), ref ModuleConfig.IgnoreMounting))
            SaveConfig(ModuleConfig);
        ImGuiOm.HelpMarker(GetLoc("AutoFateSync-IgnoreMountingHelp"));
        
        if (ImGui.Checkbox(GetLoc("AutoFateSync-AutoTankStance"), ref ModuleConfig.AutoTankStance))
            SaveConfig(ModuleConfig);
    }

    private unsafe nint FateDirectorSetupDetour(uint rowID, nint a2, nint a3)
    {
        var original = FateDirectorSetupHook.Original(rowID, a2, a3);
        if (rowID == 102401 && FateManager.Instance()->CurrentFate != null) 
            HandleFateEnter();
        return original;
    }

    private unsafe void HandleFateEnter()
    {
        if (ModuleConfig.IgnoreMounting && (DService.Condition[ConditionFlag.InFlight] || IsOnMount))
        {
            FrameworkManager.Register(OnFlying, throttleMS: 500);
            return;
        }

        var manager = FateManager.Instance();
        
        if (ModuleConfig.Delay > 0)
        {
            DService.Framework.RunOnTick(() =>
            {
                if (manager->CurrentFate == null || DService.ObjectTable.LocalPlayer == null) return;

                ExecuteFateLevelSync(manager->CurrentFate->FateId);
            }, TimeSpan.FromSeconds(ModuleConfig.Delay), 0, CancelSource.Token);
            return;
        }
        
        ExecuteFateLevelSync(manager->CurrentFate->FateId);
    }

    private unsafe void OnFlying(IFramework _)
    {
        var currentFate = FateManager.Instance()->CurrentFate;
        if (currentFate == null || DService.ObjectTable.LocalPlayer == null)
        {
            FrameworkManager.Unregister(OnFlying);
            return;
        }

        if (DService.Condition[ConditionFlag.InFlight] || IsOnMount) return;

        ExecuteFateLevelSync(currentFate->FateId);
        FrameworkManager.Unregister(OnFlying);
    }

    private unsafe void ExecuteFateLevelSync(ushort fateID)
    {
        ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.FateLevelSync, fateID, 1);
        if (ModuleConfig.AutoTankStance)
        {
            TaskHelper.Abort();
            TaskHelper.Enqueue(() => !IsOnMount && !DService.Condition[ConditionFlag.Jumping] &&
                                     ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, 2) == 0);
            TaskHelper.Enqueue(() =>
            {
                if (FateManager.Instance()->CurrentFate == null || !LuminaGetter.TryGetRow<Fate>(fateID, out var data)) return true;
                if (DService.ObjectTable.LocalPlayer is not { } localPlayer) return false;
                if (!TankStanceActions.TryGetValue(localPlayer.ClassJob.RowId, out var actionID)) return false;
                if (localPlayer.Level > data.ClassJobLevelMax) return false;
                
                var battlePlayer = localPlayer.ToStruct();
                if (!TankStanceStatuses.Any(status => battlePlayer->StatusManager.HasStatus(status)))
                    UseActionManager.UseAction(ActionType.Action, actionID);

                return true;
            });
        }
    }

    public override void Uninit()
    {
        CancelSource?.Cancel();
        CancelSource?.Dispose();
        CancelSource = null;
        
        base.Uninit();
    }

    private class Config : ModuleConfiguration
    {
        public bool IgnoreMounting = true;
        public float Delay = 3f;
        public bool AutoTankStance;
    }
}
