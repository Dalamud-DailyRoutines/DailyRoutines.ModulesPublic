using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoNotifyReadyCheck : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoNotifyReadyCheckTitle"),
        Description = Lang.Get("AutoNotifyReadyCheckDescription"),
        Category    = ModuleCategory.Combat
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

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
        if (logMessageID is not (3790 or 3791)) 
            return;

        NotifyHelper.Instance().TrayInfo(item.ToReadOnlySeString().ToString());
    }
}
