using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

internal class AutoNotifyCountdown : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoNotifyCountdownTitle"),
        Description = Lang.Get("AutoNotifyCountdownDescription"),
        Category    = ModuleCategory.Combat,
        Author      = ["HSS"]
    };

    public override ModuleBehaviour Behaviour { get; } = new()
    {
        AllDefaultEnabled = true
    };

    public override string ModuleName => nameof(AutoNotifyCountdown);

    protected override void Init() =>
        LogMessageManager.Instance().RegPost(OnLogMessage);

    protected override void Uninit() =>
        LogMessageManager.Instance().Unreg(OnLogMessage);

    private static void OnLogMessage
    (
        uint                logMessageID,
        LogMessageQueueItem item
    )
    {
        if (logMessageID != 5255) return;

        var message = Lang.Get
        (
            "AutoNotifyCountdown-Notification",
            new Dictionary<string, object>
            {
                ["seconds"] = item.Parameters[0].IntValue
            }
        );

        NotifyHelper.Instance().TrayInfo(message);
    }
}
