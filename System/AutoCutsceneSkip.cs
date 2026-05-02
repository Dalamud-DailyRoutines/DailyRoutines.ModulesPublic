using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Agent;
using Dalamud.Game.Agent.AgentArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Interface.Components;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Common.Lua;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.Interop.Game;
using OmenTools.Interop.Game.Models;
using OmenTools.Interop.Game.Models.Native;
using OmenTools.Interop.Windows.Helpers;
using OmenTools.OmenService;
using AgentId = Dalamud.Game.Agent.AgentId;
using LuaFunctionDelegate = OmenTools.Interop.Game.Models.Native.LuaFunctionDelegate;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoCutsceneSkip : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoCutsceneSkipTitle"),
        Description = Lang.Get("AutoCutsceneSkipDescription"),
        Category    = ModuleCategory.System
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private static readonly CompSig                            CutsceneHandleInputSig = new("E8 ?? ?? ?? ?? 44 0F B6 E0 48 8B 4E 08");
    private delegate        byte                               CutsceneHandleInputDelegate(nint a1, float a2);
    private                 Hook<CutsceneHandleInputDelegate>? CutsceneHandleInputHook;

    private static readonly CompSig PlayCutsceneSig = new("40 53 55 57 41 56 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B 59");
    private delegate        nint PlayCutsceneDelegate(EventFramework* a1, lua_State* state);
    private                 Hook<PlayCutsceneDelegate>? PlayCutsceneHook;

    private static readonly CompSig                       IsCutsceneSeenSig = new("E8 ?? ?? ?? ?? 33 D2 0F B6 CB 3A C3");
    private delegate        bool                          IsCutsceneSeenDelegate(UIState* state, uint cutsceneID);
    private                 Hook<IsCutsceneSeenDelegate>? IsCutsceneSeenHook;
    
    private static readonly CompSig LuaBaseSig01 = new
    (
        "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 48 8B B9 ?? ?? ?? ?? 48 8B D9 48 8B 4F ?? E8 ?? ?? ?? ?? 48 8D 8B ?? ?? ?? ?? 8B F0 E8 ?? ?? ?? ?? 48 8B 4F ?? 48 8B D0 E8 ?? ?? ?? ?? 48 8B 4F ?? BA ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B 4F ?? BA ?? ?? ?? ?? E8 ?? ?? ?? ?? 85 C0 74 ?? 4C 8D 0D ?? ?? ?? ?? 48 8B CF 4C 8D 05 ?? ?? ?? ?? 8D 56 ?? E8 ?? ?? ?? ?? 4C 8D 0D ?? ?? ?? ?? 48 8B CF 4C 8D 05 ?? ?? ?? ?? 8D 56 ?? E8 ?? ?? ?? ?? 4C 8D 0D ?? ?? ?? ?? 48 8B CF 4C 8D 05 ?? ?? ?? ?? 8D 56 ?? E8 ?? ?? ?? ?? 48 8B 4F ?? BA ?? ?? ?? ?? 48 8B 5C 24 ?? 48 8B 74 24 ?? 48 83 C4 ?? 5F E9 ?? ?? ?? ?? CC CC CC CC CC CC CC CC CC CC CC CC 48 89 5C 24"
    );

    private Hook<LuaFunctionDelegate>? PlayCutsceneLuaHook;
    

    private static readonly CompSig LuaBaseSig02 = new("40 55 56 57 41 55 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 44 24");

    private Hook<LuaFunctionDelegate>? PlayStaffRollHook;
    private Hook<LuaFunctionDelegate>? PlayToBeContinuedHook;

    private delegate void PushAgentResultToLuaDelegate(void* agent);
    private readonly PushAgentResultToLuaDelegate PushAgentResultToLua =
        new CompSig("40 53 48 83 EC ?? 0F B6 41 ?? 48 8B D9 A8 ?? 74 ?? 24 ?? 88 41 ?? 48 83 3D").GetDelegate<PushAgentResultToLuaDelegate>();

    private readonly MemoryPatch cutsceneUnskippablePatch =
        new("75 ?? 48 8B 4B ?? 48 8B 01 FF 50 ?? 48 8B C8 BA ?? ?? ?? ?? E8 ?? ?? ?? ?? 80 7B", [0xEB]);

    private Config config = null!;

    private readonly ZoneSelectCombo whitelistZoneCombo = new("Whitelist");
    private readonly ZoneSelectCombo blacklistZoneCombo = new("Blacklist");

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        whitelistZoneCombo.SelectedIDs = config.WhitelistZones;
        blacklistZoneCombo.SelectedIDs = config.BlacklistZones;

        cutsceneUnskippablePatch.Set(true);
        
        CutsceneHandleInputHook ??= CutsceneHandleInputSig.GetHook<CutsceneHandleInputDelegate>(CutsceneHandleInputDetour);
        PlayCutsceneHook        ??= PlayCutsceneSig.GetHook<PlayCutsceneDelegate>(PlayCutsceneDetour);
        IsCutsceneSeenHook      ??= IsCutsceneSeenSig.GetHook<IsCutsceneSeenDelegate>(IsCutsceneSeenDetour);

        var baseAddress01 = LuaBaseSig01.ScanText();
        PlayCutsceneLuaHook ??= DService.Instance().Hook.HookFromAddress<LuaFunctionDelegate>
        (
            baseAddress01.GetLuaFunctionByName("PlayCutScene"),
            LuaFunctionDetour
        );
        
        var baseAddress02 = LuaBaseSig02.ScanText();
        PlayStaffRollHook ??= DService.Instance().Hook.HookFromAddress<LuaFunctionDelegate>
        (
            baseAddress02.GetLuaFunctionByName("PlayStaffRoll"),
            LuaFunction2Detour
        );
        PlayToBeContinuedHook ??= DService.Instance().Hook.HookFromAddress<LuaFunctionDelegate>
        (
            baseAddress02.GetLuaFunctionByName("PlayToBeContinued"),
            LuaFunction2Detour
        );

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
    }

    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        DService.Instance().AgentLifecycle.UnregisterListener(OnAgent);
        cutsceneUnskippablePatch.Dispose();
    }

    protected override void ConfigUI()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("WorkMode")}:");

        ImGui.SameLine();
        if (ImGuiComponents.ToggleButton("WorkMode", ref config.WorkMode))
            config.Save(this);

        ImGui.SameLine();
        ImGui.TextUnformatted(Lang.Get(config.WorkMode ? "Whitelist" : "Blacklist"));

        ImGuiOm.HelpMarker(Lang.Get("AutoCutsceneSkip-WorkModeHelp"));

        ImGui.SameLine();
        ImGui.SetNextItemWidth(200f * GlobalUIScale);

        if (config.WorkMode)
        {
            if (whitelistZoneCombo.DrawCheckbox())
            {
                config.WhitelistZones = whitelistZoneCombo.SelectedIDs;
                config.Save(this);
            }
        }
        else
        {
            if (blacklistZoneCombo.DrawCheckbox())
            {
                config.BlacklistZones = blacklistZoneCombo.SelectedIDs;
                config.Save(this);
            }
        }
    }

    private void OnZoneChanged(uint u)
    {
        var isValidCurrentZone = !IsProhibitToSkipInZone();

        CutsceneHandleInputHook.Toggle(isValidCurrentZone);
        PlayCutsceneHook.Toggle(isValidCurrentZone);
        PlayCutsceneLuaHook.Toggle(isValidCurrentZone);
        IsCutsceneSeenHook.Toggle(isValidCurrentZone);
        PlayStaffRollHook.Toggle(isValidCurrentZone);
        PlayToBeContinuedHook.Toggle(isValidCurrentZone);
        
        if (isValidCurrentZone)
            DService.Instance().AgentLifecycle.RegisterListener(AgentEvent.PostReceiveEvent, AgentId.PointMenu, OnAgent);
        else
            DService.Instance().AgentLifecycle.UnregisterListener(OnAgent);
    }

    private void OnAgent(AgentEvent type, AgentArgs args)
    {
        var receiveEventArgs = args as AgentReceiveEventArgs;
        var agent            = (AgentPointMenu*)receiveEventArgs.Agent.Address;
        var atkValues        = (AtkValue*)receiveEventArgs.AtkValues;

        if (atkValues[0].Int != 12) return;
        if (agent->Context == null) return;

        var index = agent->FindFirstUncompletedEntry();
        if (index < 0)
        {
            agent->AgentInterface.Hide();
            return;
        }

        agent->SelectedIndex = index;

        agent->PendingResultFlags |= AgentPointMenu.PendingResultFlag.HasPendingResult;
        PushAgentResultToLua(agent);
        
        agent->AgentInterface.Hide();
    }

    private byte CutsceneHandleInputDetour(nint a1, float a2)
    {
        if (!DService.Instance().Condition[ConditionFlag.OccupiedInCutSceneEvent])
            return CutsceneHandleInputHook.Original(a1, a2);

        if (*(ulong*)(a1 + 56) != 0 && JournalResult == null && SatisfactionSupplyResult == null)
        {
            KeyEmulationHelper.SendKeypress(Keys.Escape);
            if (SelectString->IsAddonAndNodesReady())
                SelectString->Callback(0);
        }

        return CutsceneHandleInputHook.Original(a1, a2);
    }

    private static nint PlayCutsceneDetour(EventFramework* framework, lua_State* state) => 1;

    private static ulong LuaFunctionDetour(lua_State* state)
    {
        var value = state->top;
        value->tt      =  2;
        value->value.n =  1;
        state->top     += 1;
        return 1;
    }

    private static ulong LuaFunction2Detour(lua_State* _) => 1;

    private static bool IsCutsceneSeenDetour(UIState* state, uint cutsceneID) => true;

    private bool IsProhibitToSkipInZone()
    {
        var currentZone = GameState.TerritoryType;
        return config.WorkMode switch
        {
            true  => !config.WhitelistZones.Contains(currentZone),
            false => config.BlacklistZones.Contains(currentZone)
        };
    }

    private class Config : ModuleConfig
    {
        public HashSet<uint> BlacklistZones = [];

        public HashSet<uint> WhitelistZones = [];

        // false - 黑名单; true - 白名单
        public bool WorkMode;
    }
}
