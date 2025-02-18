using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using System;
using System.Threading;

namespace DailyRoutines.Modules;

public class AutoFateSync : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoFateSyncTitle"),
        Description = GetLoc("AutoFateSyncDescription"),
        Category = ModuleCategories.Combat,
    };

    private static readonly Throttler<string> Throttler = new();
    private static Config ModuleConfig = null!;

    private static readonly CompSig FateDirectorSetupSig = new("E8 ?? ?? ?? ?? 48 39 37");
    private delegate nint FateDirectorSetupDelegate(uint rowID, nint a2, nint a3);
    private static Hook<FateDirectorSetupDelegate>? FateDirectorSetupHook;

    private static CancellationTokenSource? CancelSource;

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        CancelSource ??= new();

        FateDirectorSetupHook ??= 
            DService.Hook.HookFromSignature<FateDirectorSetupDelegate>(FateDirectorSetupSig.Get(), FateDirectorSetupDetour);
        FateDirectorSetupHook.Enable();
    }

    public override void ConfigUI()
    {
        ImGui.SetNextItemWidth(50f * GlobalFontScale);
        if (ImGui.InputFloat(Lang.Get("AutoFateSync-Delay"), ref ModuleConfig.Delay, 0, 0, "%.1f"))
            ModuleConfig.Delay = Math.Max(0, ModuleConfig.Delay);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            SaveConfig(ModuleConfig);
            CancelSource.Cancel();
        }
        ImGuiOm.HelpMarker(Lang.Get("AutoFateSync-DelayHelp"));

        if (ImGui.Checkbox(Lang.Get("AutoFateSync-IgnoreMounting"), ref ModuleConfig.IgnoreMounting))
            SaveConfig(ModuleConfig);
        ImGuiOm.HelpMarker(Lang.Get("AutoFateSync-IgnoreMountingHelp"));
    }

    private static unsafe nint FateDirectorSetupDetour(uint rowID, nint a2, nint a3)
    {
        var original = FateDirectorSetupHook.Original(rowID, a2, a3);
        if (rowID == 102401 && FateManager.Instance()->CurrentFate != null) HandleFateEnter();
        return original;
    }

    private static unsafe void HandleFateEnter()
    {
        if (ModuleConfig.IgnoreMounting && (DService.Condition[ConditionFlag.InFlight] || IsOnMount))
        {
            FrameworkManager.Register(false, OnFlying);
            return;
        }

        if (ModuleConfig.Delay > 0)
        {
            DService.Framework.RunOnTick(() =>
            {
                var manager = FateManager.Instance();
                if (manager->CurrentFate == null || DService.ClientState.LocalPlayer == null) return;

                ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.FateLevelSync, manager->CurrentFate->FateId, 1);
            }, TimeSpan.FromSeconds(ModuleConfig.Delay), 0, CancelSource.Token);

            return;
        }

        ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.FateLevelSync, FateManager.Instance()->CurrentFate->FateId, 1);
    }

    private static unsafe void OnFlying(IFramework _)
    {
        if (!Throttler.Throttle("OnFlying")) return;

        var currentFate = FateManager.Instance()->CurrentFate;
        if (currentFate == null || DService.ClientState.LocalPlayer == null)
        {
            FrameworkManager.Unregister(OnFlying);
            return;
        }

        if (DService.Condition[ConditionFlag.InFlight] || IsOnMount) return;

        ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.FateLevelSync, currentFate->FateId, 1);
        FrameworkManager.Unregister(OnFlying);
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
    }
}
