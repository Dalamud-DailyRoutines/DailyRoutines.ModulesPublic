using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoDisableBattleBGM : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoDisableBattleBGMTitle"),
        Description = GetLoc("AutoDisableBattleBGMDescription"),
        Category    = ModuleCategories.Combat
    };
    
    private static readonly CompSig                   IsInBattleStateSig = new("E8 ?? ?? ?? ?? 38 87 ?? ?? ?? ?? 75 09");
    private delegate        byte                      IsInBattleDelegate(BGMSystem* system, BGMSystem.Scene* scene);
    private static          Hook<IsInBattleDelegate>? IsInBattleStateHook;

    private static Config ModuleConfig = null!;
    
    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        IsInBattleStateHook ??= IsInBattleStateSig.GetHook<IsInBattleDelegate>(IsInBattleStateDetour);
        IsInBattleStateHook.Enable();
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("AutoDisableBattleBGM-EnableInDuty"), ref ModuleConfig.EnableInDuty))
            SaveConfig(ModuleConfig);
        ImGuiOm.HelpMarker(GetLoc("AutoDisableBattleBGM-EnableInDutyHelp"), 20f * GlobalFontScale);
    }

    private static byte IsInBattleStateDetour(BGMSystem* system, BGMSystem.Scene* scene)
    {
        if (ModuleConfig.EnableInDuty && GameState.ContentFinderCondition > 0)
            return IsInBattleStateHook.Original(system, scene);
        
        return 0;
    }

    private class Config : ModuleConfiguration
    {
        public bool EnableInDuty;
    }
}
