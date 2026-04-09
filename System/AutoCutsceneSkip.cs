using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Interface.Components;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Common.Lua;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.Interop.Game;
using OmenTools.Interop.Game.Models;
using OmenTools.Interop.Windows.Helpers;
using OmenTools.OmenService;
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
    private          Hook<CutsceneHandleInputDelegate>? CutsceneHandleInputHook;

    private static readonly CompSig PlayCutsceneSig =
        new("40 53 55 57 41 56 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B 59");
    private delegate nint                               PlayCutsceneDelegate(EventFramework* a1, lua_State* state);
    private Hook<PlayCutsceneDelegate>? PlayCutsceneHook;

    private static readonly CompSig                    PlayCutsceneLuaSig = new("48 89 5C 24 ?? 57 48 83 EC 50 48 8B F9 48 8B D1");
    private          Hook<LuaFunctionDelegate>? PlayCutsceneLuaHook;

    private static readonly CompSig IsCutsceneSeenSig = new("E8 ?? ?? ?? ?? 33 D2 0F B6 CB 3A C3");
    private delegate        bool    IsCutsceneSeenDelegate(UIState* state, uint cutsceneID);
    private          Hook<IsCutsceneSeenDelegate>? IsCutsceneSeenHook;

    private static readonly CompSig PlayStaffRollSig =
        new("40 53 48 83 EC 20 48 8B D9 E8 ?? ?? ?? ?? 48 8B D3 48 8B 88 ?? ?? ?? ?? 48 8B 01 48 83 C4 20 5B 48 FF A0 30 04 00 00");
    private Hook<LuaFunctionDelegate>? PlayStaffRollHook;

    private static readonly CompSig PlayToBeContinuedSig =
        new("40 53 48 83 EC 20 48 8B D9 E8 ?? ?? ?? ?? 48 8B D3 48 8B 88 ?? ?? ?? ?? 48 8B 01 48 83 C4 20 5B 48 FF A0 38 04 00 00");
    private Hook<LuaFunctionDelegate>? PlayToBeContinuedHook;

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
        PlayCutsceneLuaHook     ??= PlayCutsceneLuaSig.GetHook<LuaFunctionDelegate>(LuaFunctionDetour);
        IsCutsceneSeenHook      ??= IsCutsceneSeenSig.GetHook<IsCutsceneSeenDelegate>(IsCutsceneSeenDetour);
        PlayStaffRollHook       ??= PlayStaffRollSig.GetHook<LuaFunctionDelegate>(LuaFunction2Detour);
        PlayToBeContinuedHook   ??= PlayToBeContinuedSig.GetHook<LuaFunctionDelegate>(LuaFunction2Detour);

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
    }
    
    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
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

    private void OnZoneChanged(ushort zone)
    {
        var isValidCurrentZone = !IsProhibitToSkipInZone();

        CutsceneHandleInputHook.Toggle(isValidCurrentZone);
        PlayCutsceneHook.Toggle(isValidCurrentZone);
        PlayCutsceneLuaHook.Toggle(isValidCurrentZone);
        IsCutsceneSeenHook.Toggle(isValidCurrentZone);
        PlayStaffRollHook.Toggle(isValidCurrentZone);
        PlayToBeContinuedHook.Toggle(isValidCurrentZone);
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
