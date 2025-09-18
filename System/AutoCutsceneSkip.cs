using System.Collections.Generic;
using System.Windows.Forms;
using DailyRoutines.Abstracts;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Interface.Components;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Common.Lua;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoCutsceneSkip : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoCutsceneSkipTitle"),
        Description = GetLoc("AutoCutsceneSkipDescription"),
        Category    = ModuleCategories.System,
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private static readonly CompSig CutsceneHandleInputSig = new("E8 ?? ?? ?? ?? 44 0F B6 E0 48 8B 4E 08");
    private delegate byte CutsceneHandleInputDelegate(nint a1, float a2);
    private static Hook<CutsceneHandleInputDelegate>? CutsceneHandleInputHook;

    private static readonly CompSig PlayCutsceneSig =
        new("40 53 55 57 41 56 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B 59");
    private delegate nint PlayCutsceneDelegate(EventFramework* a1, lua_State* state);
    private static Hook<PlayCutsceneDelegate>? PlayCutsceneHook;
    
    private static readonly CompSig PlayCutsceneLuaSig = new("48 89 5C 24 ?? 57 48 83 EC 50 48 8B F9 48 8B D1");
    private static Hook<LuaFunctionDelegate>? PlayCutsceneLuaHook;

    private static readonly CompSig IsCutsceneSeenSig = new("E8 ?? ?? ?? ?? 33 D2 0F B6 CB 3A C3");
    private delegate bool IsCutsceneSeenDelegate(UIState* state, uint cutsceneID);
    private static Hook<IsCutsceneSeenDelegate>? IsCutsceneSeenHook;

    private static readonly CompSig PlayStaffRollSig =
        new("40 53 48 83 EC 20 48 8B D9 E8 ?? ?? ?? ?? 48 8B D3 48 8B 88 ?? ?? ?? ?? 48 8B 01 48 83 C4 20 5B 48 FF A0 30 04 00 00");
    private static Hook<LuaFunctionDelegate>? PlayStaffRollHook;
    
    private static readonly CompSig PlayToBeContinuedSig = 
        new("40 53 48 83 EC 20 48 8B D9 E8 ?? ?? ?? ?? 48 8B D3 48 8B 88 ?? ?? ?? ?? 48 8B 01 48 83 C4 20 5B 48 FF A0 38 04 00 00");
    private static Hook<LuaFunctionDelegate>? PlayToBeContinuedHook;
    
    private static readonly MemoryPatch CutsceneUnskippablePatch =
        new("75 ?? 48 8B 4B ?? 48 8B 01 FF 50 ?? 48 8B C8 BA ?? ?? ?? ?? E8 ?? ?? ?? ?? 80 7B", [0xEB]);

    private static Config ModuleConfig = null!;

    private static string ZoneSearchInput = string.Empty;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        CutsceneUnskippablePatch.Set(true);

        CutsceneHandleInputHook ??= CutsceneHandleInputSig.GetHook<CutsceneHandleInputDelegate>(CutsceneHandleInputDetour);
        PlayCutsceneHook        ??= PlayCutsceneSig.GetHook<PlayCutsceneDelegate>(PlayCutsceneDetour);
        PlayCutsceneLuaHook     ??= PlayCutsceneLuaSig.GetHook<LuaFunctionDelegate>(LuaFunctionDetour);
        IsCutsceneSeenHook      ??= IsCutsceneSeenSig.GetHook<IsCutsceneSeenDelegate>(IsCutsceneSeenDetour);
        PlayStaffRollHook       ??= PlayStaffRollSig.GetHook<LuaFunctionDelegate>(LuaFunction2Detour);
        PlayToBeContinuedHook   ??= PlayToBeContinuedSig.GetHook<LuaFunctionDelegate>(LuaFunction2Detour);

        DService.ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
    }

    protected override void ConfigUI()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("WorkMode")}:");

        ImGui.SameLine();
        if (ImGuiComponents.ToggleButton("WorkMode", ref ModuleConfig.WorkMode))
            ModuleConfig.Save(this);

        ImGui.SameLine();
        ImGui.Text(GetLoc(ModuleConfig.WorkMode ? "Whitelist" : "Blacklist"));

        ImGuiOm.HelpMarker(GetLoc("AutoCutsceneSkip-WorkModeHelp"));

        ImGui.SameLine();
        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        if (ModuleConfig.WorkMode)
        {
            if (ZoneSelectCombo(ref ModuleConfig.WhitelistZones, ref ZoneSearchInput))
                ModuleConfig.Save(this);
        }
        else
        {
            if (ZoneSelectCombo(ref ModuleConfig.BlacklistZones, ref ZoneSearchInput))
                ModuleConfig.Save(this);
        }
    }

    private static void OnZoneChanged(ushort zone)
    {
        var isValidCurrentZone = !IsProhibitToSkipInZone();

        CutsceneHandleInputHook.Toggle(isValidCurrentZone);
        PlayCutsceneHook.Toggle(isValidCurrentZone);
        PlayCutsceneLuaHook.Toggle(isValidCurrentZone);
        IsCutsceneSeenHook.Toggle(isValidCurrentZone);
        PlayStaffRollHook.Toggle(isValidCurrentZone);
        PlayToBeContinuedHook.Toggle(isValidCurrentZone);
    }

    private static byte CutsceneHandleInputDetour(nint a1, float a2)
    {
        if (!DService.Condition[ConditionFlag.OccupiedInCutSceneEvent])
            return CutsceneHandleInputHook.Original(a1, a2);

        if (*(ulong*)(a1 + 56) != 0 && JournalResult == null && SatisfactionSupplyResult == null)
        {
            SendKeypress(Keys.Escape);
            if (IsAddonAndNodesReady(SelectString)) 
                Callback(SelectString, true, 0);
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

    private static bool IsProhibitToSkipInZone()
    {
        var currentZone = DService.ClientState.TerritoryType;
        return ModuleConfig.WorkMode switch
        {
            true => !ModuleConfig.WhitelistZones.Contains(currentZone),
            false => ModuleConfig.BlacklistZones.Contains(currentZone)
        };
    }

    protected override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        CutsceneUnskippablePatch.Dispose();
        
        base.Uninit();
    }

    private class Config : ModuleConfiguration
    {
        // false - 黑名单; true - 白名单
        public bool WorkMode;
        public HashSet<uint> BlacklistZones = [];
        public HashSet<uint> WhitelistZones = [];
    }
}
