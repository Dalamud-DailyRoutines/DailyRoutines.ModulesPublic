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
    

    private static readonly CompSig TalkBaseSig0 = new("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 48 8B F9 48 8B EA 48 8B 49 ?? E8");

    private static Hook<TalkDelegate>? TalkHook;
    private static Hook<TalkDelegate>? TalkAsyncHook;

    private static readonly CompSig TalkBaseSig1 = new("40 55 56 57 41 55 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 44 24");

    private static Hook<TalkDelegate>?        SystemTalkHook;
    private static Hook<LuaFunctionDelegate>? LogMessageNoSkipHook;
    
    private static readonly CompSig TalkBaseSig2 = new("48 89 54 24 ?? 53 56 57 41 54 41 55 48 83 EC ?? 48 8B D9");
    
    private static Hook<TalkDelegate>? ShortTalkHook;
    private static Hook<TalkDelegate>? ShortTalkWithLineVoiceHook;
    
    private static readonly CompSig TalkBaseSig3 = new("48 89 54 24 ?? 53 57 41 54 41 55 41 56 48 83 EC ?? 48 8B D9 48 8B FA 48 8B 49 ?? E8 ?? ?? ?? ?? 4C 8B 25 ?? ?? ?? ?? 44 8B F0 48 8B 4B ?? 49 8B D4 4C 8B 2D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B 4B ?? BA ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B 4B ?? BA ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B 4B ?? BA ?? ?? ?? ?? 85 C0 74 ?? E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 48 89 6C 24 ?? 4C 89 7C 24 ?? E8 ?? ?? ?? ?? 48 8B 4B ?? 49 8B D4 E8 ?? ?? ?? ?? 48 8B 4B ?? 45 33 C0 33 D2 E8 ?? ?? ?? ?? 41 83 C6 ?? 4C 8D 05 ?? ?? ?? ?? 41 8B D6 4D 8B CC 48 8B CB E8 ?? ?? ?? ?? 41 B9 ?? ?? ?? ?? 4C 8D 05 ?? ?? ?? ?? 41 8B D6 48 8B CB E8 ?? ?? ?? ?? 4C 8D 0D ?? ?? ?? ?? 41 8B D6 4C 8D 05 ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? 4C 8D 0D ?? ?? ?? ?? 41 8B D6 4C 8D 05 ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? 4C 8D 0D ?? ?? ?? ?? 41 8B D6 4C 8D 05 ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? 4C 8D 0D ?? ?? ?? ?? 41 8B D6 4C 8D 05 ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? 4C 8D 0D ?? ?? ?? ?? 41 8B D6 4C 8D 05 ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? 4C 8D 0D ?? ?? ?? ?? 41 8B D6 4C 8D 05 ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? 4C 8D 0D ?? ?? ?? ?? 41 8B D6 4C 8D 05 ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? E8");

    private static Hook<LuaFunctionDelegate>? CraftLeveTalkHook;
    
    private static readonly CompSig TalkBaseSig4 = new("E8 ?? ?? ?? ?? 48 8B 4F 08 48 8D 57 10 01 47 3C E8 ?? ?? ?? ?? 48 8B 4F 08 48 8D 57 10 01 47 3C E8 ?? ?? ?? ?? 48 8B 4F 08 48 8D 57 10 01 47 3C E8 ?? ?? ?? ?? 48 8B 4F 08 48 8D 57 10 01 47 3C E8 ?? ?? ?? ?? 48 8B 4F 08 48 8D 57 10 01 47 3C E8 ?? ?? ?? ?? 48 8B 4F 08 48 8D 57 10 01 47 3C E8 ?? ?? ?? ?? 48 8B 4F 08 48 8D 57 10 01 47 3C E8 ?? ?? ?? ?? 48 8B 4F 08 48 8D 57 10 01 47 3C E8 ?? ?? ?? ?? 48 8B 4F 08 48 8D 57 10 01 47 3C E8 ?? ?? ?? ?? FF C0 ");
    
    private static Hook<LuaFunctionDelegate>? GuildleveAssignmentTalkHook;
    
    protected override void Init()
    {
        var baseAddress0 = TalkBaseSig0.ScanText();
        
        TalkHook ??= DService.Hook.HookFromAddress<TalkDelegate>(GetLuaFunctionByName(baseAddress0, "Talk"), TalkDetour);
        TalkHook.Enable();

        TalkAsyncHook ??= DService.Hook.HookFromAddress<TalkDelegate>(GetLuaFunctionByName(baseAddress0, "TalkAsync"), TalkDetour);
        TalkAsyncHook.Enable();
        
        var baseAddress1 = TalkBaseSig1.ScanText();
        
        SystemTalkHook ??= DService.Hook.HookFromAddress<TalkDelegate>(GetLuaFunctionByName(baseAddress1, "SystemTalk"), TalkDetour);
        SystemTalkHook.Enable();
        
        LogMessageNoSkipHook ??= DService.Hook.HookFromAddress<LuaFunctionDelegate>(GetLuaFunctionByName(baseAddress1, "LogMessageNoSkip"), LuaStateTalkDetour);
        LogMessageNoSkipHook.Enable();
        
        var baseAddress2 = TalkBaseSig2.ScanText();
        
        ShortTalkHook ??= DService.Hook.HookFromAddress<TalkDelegate>(GetLuaFunctionByName(baseAddress2, "ShortTalk"), TalkDetour);
        ShortTalkHook.Enable();
        
        ShortTalkWithLineVoiceHook ??= DService.Hook.HookFromAddress<TalkDelegate>(GetLuaFunctionByName(baseAddress2, "ShortTalkWithLineVoice"), TalkDetour);
        ShortTalkWithLineVoiceHook.Enable();
        
        var baseAddress3 = TalkBaseSig3.ScanText();
        
        CraftLeveTalkHook ??= DService.Hook.HookFromAddress<LuaFunctionDelegate>(GetLuaFunctionByName(baseAddress3, "CraftLeveTalk"), LuaStateTalkDetour);
        CraftLeveTalkHook.Enable();
        
        var baseAddress4 = TalkBaseSig4.ScanText();

        GuildleveAssignmentTalkHook ??=
            DService.Hook.HookFromAddress<LuaFunctionDelegate>(GetLuaFunctionByName(baseAddress4, "GuildleveAssignmentTalk"), LuaStateTalkDetour);
        GuildleveAssignmentTalkHook.Enable();
        
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
        for(var i = 0; i < sizeof(AtkEventData); i++)
            ((byte*)data)[i] = 0;
        
        addon->ReceiveEvent(AtkEventType.MouseDown, 0, evt, data);
        addon->ReceiveEvent(AtkEventType.MouseClick, 0, evt, data);
        addon->ReceiveEvent(AtkEventType.MouseUp, 0, evt, data);
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
