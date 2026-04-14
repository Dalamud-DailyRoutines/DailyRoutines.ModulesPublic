using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoNotifyReadyCheck : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoNotifyReadyCheckTitle"),
        Description = Lang.Get("AutoNotifyReadyCheckDescription"),
        Category    = ModuleCategory.Notice
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    protected override void Init() =>
        LogMessageManager.Instance().RegPost(OnLogMessage);

    protected override void Uninit() =>
        LogMessageManager.Instance().Unreg(OnLogMessage);

    private static void OnLogMessage(uint logMessageID, LogMessageQueueItem item)
    {
        if (!ValidLogMessages.Contains(logMessageID)) return;

        NotifyHelper.Instance().NotificationInfo(LuminaWrapper.GetLogMessageText(3790));
        NotifyHelper.Speak(LuminaWrapper.GetLogMessageText(3790));
    }
    
    #region 常量

    private static readonly FrozenSet<uint> ValidLogMessages = [3790, 3791];

    #endregion
}
