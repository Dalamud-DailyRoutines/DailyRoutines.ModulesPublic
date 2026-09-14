using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using OmenTools.Info.Game.Enums;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Duty;

public class AutoVeryEasyQuestBattle : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoVeryEasyQuestBattleTitle"),
        Description = Lang.Get("AutoVeryEasyQuestBattleDescription"),
        Category    = ModuleCategory.Duty
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true, AllDefaultEnabled = true };

    protected override void Init() =>
        ExecuteCommandManager.Instance().RegPre(OnPreUseCommand);

    protected override void Uninit() =>
        ExecuteCommandManager.Instance().Unreg(OnPreUseCommand);

    private static void OnPreUseCommand
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

        // 客户端会发送两次该命令: 一次为难度数据设置, 一次是让服务端响应确认, 后者参数全为 0
        var isRequest = (param1 | param2) != 0;

        param1 = 2;

        if (!isRequest) return;

        var message = Lang.Get("AutoVeryEasyQuestBattle-Notification");
        
        NotifyHelper.Instance().Chat(message);
        NotifyHelper.Toast(message);
    }
}
