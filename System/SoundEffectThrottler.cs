using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using System;

namespace DailyRoutines.Modules;

public class SoundEffectThrottler : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("SoundEffectThrottlerTitle"),
        Description = GetLoc("SoundEffectThrottlerDescription"),
        Category    = ModuleCategories.System,
    };

    private static readonly CompSig                        PlaySoundEffectSig = new("E9 ?? ?? ?? ?? C6 41 28 01");
    private delegate        void                           PlaySoundEffectDelegate(uint sound, nint a2, nint a3, byte a4);
    private static          Hook<PlaySoundEffectDelegate>? PlaySoundEffectHook;

    private static Config? ModuleConfig;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        PlaySoundEffectHook ??= PlaySoundEffectSig.GetHook<PlaySoundEffectDelegate>(PlaySoundEffectDetour);
        PlaySoundEffectHook.Enable();
    }

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(100f * GlobalFontScale);
        ImGui.InputInt(GetLoc("SoundEffectThrottler-Throttle"), ref ModuleConfig.Throttle, 0, 0);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            ModuleConfig.Throttle = Math.Max(100, ModuleConfig.Throttle);
            SaveConfig(ModuleConfig);
        }

        ImGuiOm.HelpMarker(GetLoc("SoundEffectThrottler-ThrottleHelp", ModuleConfig.Throttle));

        ImGui.SetNextItemWidth(100f * GlobalFontScale);
        ImGui.SliderInt(GetLoc("SoundEffectThrottler-Volume"), ref ModuleConfig.Volume, 1, 3);
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(ModuleConfig);
    }

    private static void PlaySoundEffectDetour(uint sound, nint a2, nint a3, byte a4)
    {
        var se = sound - 36;
        switch (se)
        {
            case <= 16 when Throttler.Throttle($"SoundEffectThrottler-{se}", ModuleConfig.Throttle):
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
