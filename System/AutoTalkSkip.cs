using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Common.Lua;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoTalkSkip : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoTalkSkipTitle"),
        Description = GetLoc("AutoTalkSkipDescription"),
        Category    = ModuleCategories.System,
    };

    private delegate nint TalkDelegate(EventSceneModuleImplBase* scene);
    
    // Base0: 48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 48 8B F9 48 8B EA 48 8B 49 ?? E8
    // Base1: 40 55 56 57 41 55 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 44 24 ??
    private static readonly CompSig TalkSig =
        new("40 53 48 83 EC 20 48 8B D9 E8 ?? ?? ?? ?? 48 8B D3 48 8B 88 ?? ?? ?? ?? 48 8B 01 48 83 C4 20 5B 48 FF A0 A8 00 00 00");
    private static   Hook<TalkDelegate>? TalkHook;

    private static readonly CompSig TalkAsyncSig =
        new("40 53 48 83 EC 20 48 8B D9 E8 ?? ?? ?? ?? 48 8B D3 48 8B 88 ?? ?? ?? ?? 48 8B 01 48 83 C4 20 5B 48 FF A0 B0 00 00 00");
    private static Hook<TalkDelegate>? TalkAsyncHook;

    private static readonly CompSig SystemTalkSig =
        new("40 53 48 83 EC 20 48 8B D9 E8 ?? ?? ?? ?? 48 8B D3 48 8B 88 ?? ?? ?? ?? 48 8B 01 48 83 C4 20 5B 48 FF A0 B8 00 00 00");
    private static Hook<TalkDelegate>? SystemTalkHook;
    
    // Base: 48 89 54 24 ?? 53 56 57 41 54 41 55 48 83 EC ?? 48 8B D9
    private static readonly CompSig ShortTalkSig =
        new("40 53 48 83 EC 20 48 8B D9 E8 ?? ?? ?? ?? 48 8B D3 48 8B 88 ?? ?? ?? ?? 48 8B 01 48 83 C4 20 5B 48 FF A0 90 03 00 00");
    private static Hook<TalkDelegate>? ShortTalkHook;
    
    private static readonly CompSig ShortTalkWithLineVoiceSig =
        new("40 53 48 83 EC 20 48 8B D9 E8 ?? ?? ?? ?? 48 8B D3 48 8B 88 ?? ?? ?? ?? 48 8B 01 48 83 C4 20 5B 48 FF A0 98 03 00 00");
    private static Hook<TalkDelegate>? ShortTalkWithLineVoiceHook;
    
    private static readonly CompSig CraftLeveTalkSig = new("40 53 48 83 EC 50 48 8B D1 48 8D 4C 24 20 E8 ?? ?? ?? ?? BA 01 00 00 00 48 8D 4C 24 20 E8 ?? ?? ?? ?? 48 8B 4C 24 28 4C 8B C0 BA 01 00 00 00 E8 ?? ?? ?? ?? 48 8B 4C 24 28 BA 02 00 00 00 48 8B 18 E8 ?? ?? ?? ?? 48 85 DB 74 2A 8B D0 48 8B CB E8 ?? ?? ?? ?? 33 D2 48 8D 4C 24 20 E8 ?? ?? ?? ?? 48 8D 4C 24 20 8B D8 E8 ?? ?? ?? ?? 8B C3 48 83 C4 50 5B C3 48 8D 4C 24 20 E8 ?? ?? ?? ?? 8B C3 48 83 C4 50 5B C3 CC CC CC CC CC CC CC CC CC 40 53 48 81 EC 50 01 00 00");
    private static Hook<LuaFunctionDelegate>? CraftLeveTalkHook;
    
    private static readonly CompSig GuildleveAssignmentTalkSig = new("40 53 48 83 EC ?? 48 8B D1 48 8D 4C 24 ?? E8 ?? ?? ?? ?? BA ?? ?? ?? ?? 48 8D 4C 24 ?? E8 ?? ?? ?? ?? 48 8B 4C 24 ?? 4C 8B C0 BA ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B 4C 24 ?? BA ?? ?? ?? ?? 48 8B 18 E8 ?? ?? ?? ?? 48 85 DB 74 ?? 8B D0 48 8B CB E8 ?? ?? ?? ?? 33 D2 48 8D 4C 24 ?? E8 ?? ?? ?? ?? 48 8D 4C 24 ?? 8B D8 E8 ?? ?? ?? ?? 8B C3 48 83 C4 ?? 5B C3 48 8D 4C 24 ?? E8 ?? ?? ?? ?? 8B C3 48 83 C4 ?? 5B C3 CC CC CC CC CC CC CC CC CC 40 53 56");
    private static Hook<LuaFunctionDelegate>? GuildleveAssignmentTalkHook;

    private static readonly CompSig LogMessageNoSkipSig =
        new("4C 8B DC 55 53 41 57 49 8D 6B ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 45 ?? 48 8B D1");
    private static Hook<LuaFunctionDelegate>? LogMessageNoSkipHook;

    protected override void Init()
    {
        TalkHook ??= TalkSig.GetHook<TalkDelegate>(TalkDetour);
        TalkHook.Enable();

        TalkAsyncHook ??= TalkAsyncSig.GetHook<TalkDelegate>(TalkDetour);
        TalkAsyncHook.Enable();
        
        SystemTalkHook ??= SystemTalkSig.GetHook<TalkDelegate>(TalkDetour);
        SystemTalkHook.Enable();
        
        ShortTalkHook ??= ShortTalkSig.GetHook<TalkDelegate>(TalkDetour);
        ShortTalkHook.Enable();
        
        ShortTalkWithLineVoiceHook ??= ShortTalkWithLineVoiceSig.GetHook<TalkDelegate>(TalkDetour);
        ShortTalkWithLineVoiceHook.Enable();
        
        CraftLeveTalkHook ??= CraftLeveTalkSig.GetHook<LuaFunctionDelegate>(LuaStateTalkDetour);
        CraftLeveTalkHook.Enable();
        
        GuildleveAssignmentTalkHook ??= GuildleveAssignmentTalkSig.GetHook<LuaFunctionDelegate>(LuaStateTalkDetour);
        GuildleveAssignmentTalkHook.Enable();
        
        LogMessageNoSkipHook ??= LogMessageNoSkipSig.GetHook<LuaFunctionDelegate>(LuaStateTalkDetour);
        LogMessageNoSkipHook.Enable();
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, "Talk", OnAddon);
    }

    protected override void Uninit() => 
        DService.AddonLifecycle.UnregisterListener(OnAddon);

    private static void OnAddon(AddonEvent type, AddonArgs args)
    {
        var addon = Talk;
        if (addon == null) return;
        
        var evt = stackalloc AtkEvent[1]
        {
            new()
            {
                Listener = (AtkEventListener*)addon,
                State    = new() { StateFlags = (AtkEventStateFlags)132 },
                Target   = &AtkStage.Instance()->AtkEventTarget,
            },
        };
        
        var data = stackalloc AtkEventData[1];
        addon->ReceiveEvent(AtkEventType.MouseClick, 0, evt, data);
    }

    private static ulong LuaStateTalkDetour(lua_State* state)
    {
        var value = state->top;
        value->tt      =  2;
        value->value.n =  1;
        state->top     += 1;
        
        return 1;
    }
    
    private static nint TalkDetour(EventSceneModuleImplBase* scene) => 1;
}
