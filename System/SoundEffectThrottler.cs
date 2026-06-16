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

    private long lastPlayTick;
    
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
    }

    private void PlaySoundEffectDetour(uint sound, nint a2, nint a3, byte a4)
    {
        var se = sound - 36;

        switch (se)
        {
            case <= 16:

                if (Environment.TickCount64 == lastPlayTick)
                {
                    PlaySoundEffectHook.Original(sound, a2, a3, a4);
                    return;
                }
                
                if (Throttler.Shared.Throttle($"SoundEffectThrottler.SoundEffect{se}", config.Throttle))
                {
                    PlaySoundEffectHook.Original(sound, a2, a3, a4);
                    lastPlayTick = Environment.TickCount64;
                }

                break;
            case > 16:
                PlaySoundEffectHook.Original(sound, a2, a3, a4);
                break;
        }
    }
    
    private class Config : ModuleConfig
    {
        public uint Throttle = 1000;
    }
}
