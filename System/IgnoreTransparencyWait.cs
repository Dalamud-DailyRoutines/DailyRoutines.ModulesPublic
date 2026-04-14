using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using OmenTools.Interop.Game.Models;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;

namespace DailyRoutines.ModulesPublic;

public unsafe class IgnoreTransparencyWait : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("IgnoreTransparencyWaitTitle"),
        Description = Lang.Get("IgnoreTransparencyWaitDescription"),
        Category    = ModuleCategory.System
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };
    
    private static readonly CompSig WaitForBaseSig = new("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 48 8B D9 48 8B 49 ?? E8 ?? ?? ?? ?? 48 8B 35");
    private delegate        nint    EventSceneScriptDelegate(EventSceneModuleImplBase* scene);
    private Hook<EventSceneScriptDelegate>? WaitForTransparencyHook;
    private Hook<EventSceneScriptDelegate>? WaitForMoveHook;
    private Hook<EventSceneScriptDelegate>? WaitForPathMoveHook;

    protected override void Init()
    {
        var baseAddress = WaitForBaseSig.ScanText();

        WaitForTransparencyHook ??=
            DService.Instance().Hook.HookFromAddress<EventSceneScriptDelegate>(baseAddress.GetLuaFunctionByName("WaitForTransparency"), EventSceneScriptDetour);
        WaitForTransparencyHook.Enable();

        WaitForMoveHook ??= DService.Instance().Hook.HookFromAddress<EventSceneScriptDelegate>
            (baseAddress.GetLuaFunctionByName("WaitForMove"), EventSceneScriptDetour);
        WaitForMoveHook.Enable();

        WaitForPathMoveHook ??=
            DService.Instance().Hook.HookFromAddress<EventSceneScriptDelegate>(baseAddress.GetLuaFunctionByName("WaitForPathMove"), EventSceneScriptDetour);
        WaitForPathMoveHook.Enable();
    }

    private static nint EventSceneScriptDetour(EventSceneModuleImplBase* scene) => 1;
}
