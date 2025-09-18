using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DailyRoutines.ModulesPublic;

public class AutoUseEarthsReply : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoUseEarthsReplyTitle"),
        Description = GetLoc("AutoUseEarthsReplyDescription"),
        Category    = ModuleCategories.Action,
        Author      = ["ToxicStar"]
    };
    
    private const uint RiddleOfEarthAction = 29482; // 金刚极意
    private const uint EarthsReplyAction   = 29483; // 金刚转轮
    private const uint SprintStatus        = 1342;  // 冲刺
    private const uint GuardStatus         = 3054;  // 防御
    
    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        ModuleConfig =   LoadConfig<Config>() ?? new();
        TaskHelper   ??= new() { TimeLimitMS = 8_000 };
        
        UseActionManager.RegUseActionLocation(OnUseAction);
    }
    
    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("AutoUseEarthsReply-UseWhenGuard"), ref ModuleConfig.UseWhenSprint))
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox(GetLoc("AutoUseEarthsReply-UseWhenSprint"), ref ModuleConfig.UseWhenGuard))
            SaveConfig(ModuleConfig);
    }

    private void OnUseAction(bool result, ActionType actionType, uint actionID, ulong targetID, Vector3 location, uint extraParam, byte a7)
    {
        if (actionType != ActionType.Action || actionID != RiddleOfEarthAction || !result) return;

        TaskHelper.Abort();
        TaskHelper.DelayNext(8_000, $"Delay_UseAction{EarthsReplyAction}", false, 1);
        TaskHelper.Enqueue(() =>
                           {
                               if (DService.ObjectTable.LocalPlayer is not { } localPlayer) return;

                               if (!ModuleConfig.UseWhenSprint && localPlayer.StatusList.HasStatus(SprintStatus)) return;
                               if (!ModuleConfig.UseWhenGuard  && localPlayer.StatusList.HasStatus(GuardStatus)) return;

                               UseActionManager.UseActionLocation(ActionType.Action, EarthsReplyAction);
                           }, $"UseAction_{EarthsReplyAction}", 500, true, 1);
    }

    protected override void Uninit() => 
        UseActionManager.Unreg(OnUseAction);

    public class Config : ModuleConfiguration
    {
        public bool UseWhenSprint;
        public bool UseWhenGuard;
    }
}
