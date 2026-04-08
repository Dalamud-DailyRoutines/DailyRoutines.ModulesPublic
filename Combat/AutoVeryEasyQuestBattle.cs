using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using OmenTools.Info.Game.Enums;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoVeryEasyQuestBattle : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoVeryEasyQuestBattleTitle"),
        Description = Lang.Get("AutoVeryEasyQuestBattleDescription"),
        Category    = ModuleCategory.Combat
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true, AllDefaultEnabled = true };

    protected override void Init() =>
        ExecuteCommandManager.Instance().RegPre(OnPreUseCommand);
    
    protected override void Uninit() =>
        ExecuteCommandManager.Instance().Unreg(OnPreUseCommand);

    private static unsafe void OnPreUseCommand
    (
        ref bool               isPrevented,
        ref ExecuteCommandFlag command,
        ref uint               param1,
        ref uint               param2,
        ref uint               param3,
        ref uint               param4
    )
    {
        if (command != ExecuteCommandFlag.StartSoloQuestBattle) return;

        param1 = 2;

        if (!SelectString->IsAddonAndNodesReady())
        {
            NotifyHelper.Instance().Chat(Lang.Get("AutoVeryEasyQuestBattle-Notification"));
            NotifyHelper.Instance().NotificationInfo(Lang.Get("AutoVeryEasyQuestBattle-Notification"));
        }
    }
}
