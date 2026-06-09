using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using InteropGenerator.Runtime;
using OmenTools.Interop.Game;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoDisplayStatusFullTime : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoDisplayStatusFullTimeTitle"),
        Description = Lang.Get("AutoDisplayStatusFullTimeDescription"),
        Category    = ModuleCategory.Combat
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private Hook<RaptureTextModule.Delegates.FormatTimeSpan> FormatTimeSpanHook;

    // 把状态效果更新间隔始终改为实时更新
    private MemoryPatch updateIntervalPatch = new("85 D2 74 ?? 83 FA ?? 73 ?? 41 3B D0", [0xB0, 0x01, 0xC3, 0x90]);

    private Config config = null!;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();
        
        updateIntervalPatch.Enable();
        
        FormatTimeSpanHook = DService.Instance().Hook.HookFromMemberFunction<RaptureTextModule.Delegates.FormatTimeSpan>
        (
            typeof(RaptureTextModule.MemberFunctionPointers),
            "FormatTimeSpan",
            FormatTimeSpanDetour
        );
        FormatTimeSpanHook.Enable();
    }

    protected override void ConfigUI()
    {
        using (var combo = ImRaii.Combo($"{Lang.Get("Mode")}", Lang.Get($"AutoDisplayStatusFullTime-Mode-{config.Mode}")))
        {
            if (combo)
            {
                foreach (var mode in Enum.GetValues<Mode>())
                {
                    if (ImGui.Selectable(Lang.Get($"AutoDisplayStatusFullTime-Mode-{mode}"), mode == config.Mode))
                    {
                        config.Mode = mode;
                        config.Save(this);
                    }
                }
            }
        }
    }

    private CStringPointer FormatTimeSpanDetour(RaptureTextModule* thisPtr, uint seconds, bool alternativeMinutesGlyph)
    {
        var formatted = string.Empty;

        switch (config.Mode)
        {
            case Mode.FullSecond:
                formatted = seconds.ToString();
                break;
            
            case Mode.FullMinute:
                var (minute0, second0) = Math.DivRem(seconds, 60);
                
                if (minute0 == 0)
                    goto case Mode.FullSecond;

                formatted = $"{minute0:D2}:{second0:D2}";
                break;

            case Mode.Full:
                var (hour1, minute1, second1) = (seconds / 3600, seconds / 60 % 60, seconds % 60);
                if (hour1 == 0)
                    goto case Mode.FullMinute;

                formatted = $"{hour1:D2}:{minute1:D2}:{second1:D2}";
                break;
        }

        if (string.IsNullOrEmpty(formatted))
            return FormatTimeSpanHook.Original(thisPtr, seconds, alternativeMinutesGlyph);
        
        using var utf8String = new Utf8String(formatted);
        
        var returnUtf8String = thisPtr->UnkStrings0[0];
        returnUtf8String.Clear();
        returnUtf8String.Copy(&utf8String);
        return returnUtf8String.StringPtr;
    }

    private class Config : ModuleConfig
    {
        public Mode Mode = Mode.FullSecond;
    }
    
    private enum Mode
    {
        FullSecond,
        FullMinute,
        Full
    }
}
