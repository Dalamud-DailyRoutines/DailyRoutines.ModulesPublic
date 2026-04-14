using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoDisableBattleBGM : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoDisableBattleBGMTitle"),
        Description = Lang.Get("AutoDisableBattleBGMDescription"),
        Category    = ModuleCategory.Combat
    };
    
    private static readonly CompSig                   IsInBattleStateSig = new("E8 ?? ?? ?? ?? 38 87 ?? ?? ?? ?? 75 09");
    private delegate        byte                      IsInBattleDelegate(BGMSystem* system, BGMSystem.Scene* scene);
    private                 Hook<IsInBattleDelegate>? IsInBattleStateHook;

    private Config config = null!;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        IsInBattleStateHook ??= IsInBattleStateSig.GetHook<IsInBattleDelegate>(IsInBattleStateDetour);
        IsInBattleStateHook.Enable();
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("AutoDisableBattleBGM-EnableInDuty"), ref config.EnableInDuty))
            config.Save(this);
        ImGuiOm.HelpMarker(Lang.Get("AutoDisableBattleBGM-EnableInDutyHelp"), 20f * GlobalUIScale);
    }

    private byte IsInBattleStateDetour(BGMSystem* system, BGMSystem.Scene* scene)
    {
        if (!config.EnableInDuty && GameState.ContentFinderCondition > 0)
            return IsInBattleStateHook.Original(system, scene);

        return 0;
    }
    
    private class Config : ModuleConfig
    {
        public bool EnableInDuty;
    }
}
