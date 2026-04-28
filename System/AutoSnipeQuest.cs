using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Common.Lua;
using OmenTools.Interop.Game.Models;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoSnipeQuest : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoSnipeQuestTitle"),
        Description = Lang.Get("AutoSnipeQuestDescription"),
        Category    = ModuleCategory.System
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };
    
    private static readonly CompSig EnqueueSnipeTaskSig = new("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 48 8B F9 48 8D 4C 24 ?? E8 ?? ?? ?? ?? 48 8B 4C 24");
    private delegate        ulong   EnqueueSnipeTaskDelegate(EventSceneModuleImplBase* scene, lua_State* state);
    private                 Hook<EnqueueSnipeTaskDelegate> EnqueueSnipeTaskHook;

    protected override void Init()
    {
        EnqueueSnipeTaskHook ??= EnqueueSnipeTaskSig.GetHook<EnqueueSnipeTaskDelegate>(EnqueueSnipeTaskDetour);
        EnqueueSnipeTaskHook.Enable();
    }

    private static ulong EnqueueSnipeTaskDetour(EventSceneModuleImplBase* scene, lua_State* state)
    {
        var value = state->top;
        value->tt      =  3;
        value->value.n =  1;
        state->top     += 1;
        return 1;
    }
}
