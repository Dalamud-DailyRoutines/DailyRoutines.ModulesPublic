using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using OmenTools.Interop.Game.Models;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoCancelNPCEmote : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoCancelNPCEmoteTitle"),
        Description = Lang.Get("AutoCancelNPCEmoteDescription"),
        Category    = ModuleCategory.System
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };
    
    private static readonly CompSig WaitForBaseSig = 
        new("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 48 8B D9 48 8B 49 ?? E8 ?? ?? ?? ?? 48 8B 35");

    private delegate nint EventSceneScriptDelegate(EventSceneModuleImplBase* scene);
    
    private Hook<EventSceneScriptDelegate>? WaitForActionTimelineHook;
    private Hook<EventSceneScriptDelegate>? WaitForActionTimelineLoadHook;
    private Hook<EventSceneScriptDelegate>? PlayActionTimelineHook;
    private Hook<EventSceneScriptDelegate>? PlayEmoteHook;
    private Hook<EventSceneScriptDelegate>? CancelEmoteHook;
    private Hook<EventSceneScriptDelegate>? WaitForEmoteHook;
    private Hook<EventSceneScriptDelegate>? IsEmotingHook;

    protected override void Init()
    {
        var baseAddress = WaitForBaseSig.ScanText();

        WaitForActionTimelineHook ??=
            DService.Instance().Hook.HookFromAddress<EventSceneScriptDelegate>(baseAddress.GetLuaFunctionByName("WaitForActionTimeline"), EventSceneScriptDetour);
        WaitForActionTimelineHook.Enable();

        PlayActionTimelineHook ??=
            DService.Instance().Hook.HookFromAddress<EventSceneScriptDelegate>(baseAddress.GetLuaFunctionByName("PlayActionTimeline"), EventSceneScriptDetour);
        PlayActionTimelineHook.Enable();

        WaitForActionTimelineLoadHook ??=
            DService.Instance().Hook.HookFromAddress<EventSceneScriptDelegate>
                (baseAddress.GetLuaFunctionByName("WaitForActionTimelineLoad"), EventSceneScriptDetour);
        WaitForActionTimelineLoadHook.Enable();

        PlayEmoteHook ??= DService.Instance().Hook.HookFromAddress<EventSceneScriptDelegate>(baseAddress.GetLuaFunctionByName("PlayEmote"), EventSceneScriptDetour);
        PlayEmoteHook.Enable();

        CancelEmoteHook ??= DService.Instance().Hook.HookFromAddress<EventSceneScriptDelegate>
            (baseAddress.GetLuaFunctionByName("CancelEmote"), EventSceneScriptDetour);
        CancelEmoteHook.Enable();

        WaitForEmoteHook ??= DService.Instance().Hook.HookFromAddress<EventSceneScriptDelegate>
            (baseAddress.GetLuaFunctionByName("WaitForEmote"), EventSceneScriptDetour);
        WaitForEmoteHook.Enable();

        IsEmotingHook ??= DService.Instance().Hook.HookFromAddress<EventSceneScriptDelegate>
            (baseAddress.GetLuaFunctionByName("IsEmoting"), EventSceneScriptNoDetour);
        IsEmotingHook.Enable();
    }

    private static nint EventSceneScriptDetour(EventSceneModuleImplBase* scene) => 1;

    private static nint EventSceneScriptNoDetour(EventSceneModuleImplBase* scene) => 0;
}
