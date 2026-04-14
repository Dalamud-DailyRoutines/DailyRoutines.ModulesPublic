using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Hooking;
using OmenTools.Interop.Game.Models;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic;

public class SoundEffectThrottler : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("SoundEffectThrottlerTitle"),
        Description = Lang.Get("SoundEffectThrottlerDescription"),
        Category    = ModuleCategory.System
    };
    
    private static readonly CompSig                        PlaySoundEffectSig = new("E9 ?? ?? ?? ?? C6 41 28 01");
    private delegate        void                           PlaySoundEffectDelegate(uint sound, nint a2, nint a3, byte a4);
    private                 Hook<PlaySoundEffectDelegate>? PlaySoundEffectHook;

    private Config? config;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        PlaySoundEffectHook ??= PlaySoundEffectSig.GetHook<PlaySoundEffectDelegate>(PlaySoundEffectDetour);
        PlaySoundEffectHook.Enable();
    }

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(100f * GlobalUIScale);
        ImGui.InputUInt(Lang.Get("SoundEffectThrottler-Throttle"), ref config.Throttle);

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.Throttle = Math.Max(100, config.Throttle);
            config.Save(this);
        }

        ImGuiOm.HelpMarker(Lang.Get("SoundEffectThrottler-ThrottleHelp", config.Throttle));

        ImGui.SetNextItemWidth(100f * GlobalUIScale);
        ImGui.SliderInt(Lang.Get("SoundEffectThrottler-Volume"), ref config.Volume, 1, 3);
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);
    }

    private void PlaySoundEffectDetour(uint sound, nint a2, nint a3, byte a4)
    {
        var se = sound - 36;

        switch (se)
        {
            case <= 16 when Throttler.Shared.Throttle($"SoundEffectThrottler-{se}", config.Throttle):
                for (var i = 0; i < config.Volume; i++)
                    PlaySoundEffectHook.Original(sound, a2, a3, a4);

                break;
            case > 16:
                PlaySoundEffectHook.Original(sound, a2, a3, a4);
                break;
        }
    }
    
    private class Config : ModuleConfig
    {
        public uint Throttle = 1000;
        public int  Volume   = 3;
    }
}
