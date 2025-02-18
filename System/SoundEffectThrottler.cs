using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using System;

namespace DailyRoutines.Modules;

public class SoundEffectThrottler : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("SoundEffectThrottlerTitle"),
        Description = GetLoc("SoundEffectThrottlerDescription"),
        Category = ModuleCategories.System,
    };

    private static readonly CompSig PlaySoundEffectSig =
        new("40 53 41 55 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? F6 05");
    private delegate void PlaySoundEffectDelegate(uint sound, nint a2, nint a3, byte a4);
    private static Hook<PlaySoundEffectDelegate>? PlaySoundEffectHook;

    private static Config? ModuleConfig;

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        PlaySoundEffectHook ??= 
            DService.Hook.HookFromSignature<PlaySoundEffectDelegate>(PlaySoundEffectSig.Get(), PlaySoundEffectDetour);
        PlaySoundEffectHook.Enable();
    }

    public override void ConfigUI()
    {
        ImGui.SetNextItemWidth(100f * GlobalFontScale);
        ImGui.InputInt(Lang.Get("SoundEffectThrottler-Throttle"), ref ModuleConfig.Throttle, 0, 0);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            ModuleConfig.Throttle = Math.Max(100, ModuleConfig.Throttle);
            SaveConfig(ModuleConfig);
        }

        ImGuiOm.HelpMarker(Lang.Get("SoundEffectThrottler-ThrottleHelp", ModuleConfig.Throttle));

        ImGui.SetNextItemWidth(100f * GlobalFontScale);
        ImGui.SliderInt(Lang.Get("SoundEffectThrottler-Volume"), ref ModuleConfig.Volume, 1, 3);
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(ModuleConfig);
    }

    private static void PlaySoundEffectDetour(uint sound, nint a2, nint a3, byte a4)
    {
        var se = sound - 36;
        switch (se)
        {
            case <= 16 when Throttler.Throttle($"SoundEffectThorttler-{se}", ModuleConfig.Throttle):
                for (var i = 0; i < ModuleConfig.Volume; i++)
                    PlaySoundEffectHook.Original(sound, a2, a3, a4);

                break;
            case > 16:
                PlaySoundEffectHook.Original(sound, a2, a3, a4);
                break;
        }
    }

    private class Config : ModuleConfiguration
    {
        public int Throttle = 1000;
        public int Volume = 3;
    }
}
