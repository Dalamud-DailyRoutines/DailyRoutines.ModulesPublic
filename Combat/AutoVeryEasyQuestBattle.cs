using DailyRoutines.Abstracts;

namespace DailyRoutines.ModulesPublic;

public class AutoVeryEasyQuestBattle : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoVeryEasyQuestBattleTitle"),
        Description = GetLoc("AutoVeryEasyQuestBattleDescription"),
        Category    = ModuleCategories.Combat
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true, AllDefaultEnabled = true };

    protected override void Init() => 
        ExecuteCommandManager.RegPre(OnPreUseCommand);

    private static unsafe void OnPreUseCommand(
        ref bool               isPrevented,
        ref ExecuteCommandFlag command,
        ref uint               param1,
        ref uint               param2,
        ref uint               param3,
        ref uint               param4)
    {
        if (command != ExecuteCommandFlag.StartSoloQuestBattle) return;

        param1 = 2;

        if (!IsAddonAndNodesReady(SelectString))
        {
            Chat(GetLoc("AutoVeryEasyQuestBattle-Notification"));
            NotificationInfo(GetLoc("AutoVeryEasyQuestBattle-Notification"));
        }
    }

    protected override void Uninit() => 
        ExecuteCommandManager.Unreg(OnPreUseCommand);
}
