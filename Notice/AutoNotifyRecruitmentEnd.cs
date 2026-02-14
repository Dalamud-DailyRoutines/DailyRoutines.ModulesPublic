using System.Collections.Frozen;
using DailyRoutines.Abstracts;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace DailyRoutines.ModulesPublic;

public class AutoNotifyRecruitmentEnd : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoNotifyRecruitmentEndTitle"),
        Description = GetLoc("AutoNotifyRecruitmentEndDescription"),
        Category    = ModuleCategories.Notice,
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static readonly FrozenSet<uint> ValidLogMessages = [983, 984, 985, 986, 7451, 7452];

    protected override void Init() =>
        LogMessageManager.Instance().RegPost(OnLogMessage);
    
    protected override void Uninit() => 
        LogMessageManager.Instance().Unreg(OnLogMessage);

    private static void OnLogMessage(uint logMessageID, LogMessageQueueItem item)
    {
        if (!ValidLogMessages.Contains(logMessageID)) return;
        
        var content = LuminaWrapper.GetLogMessageText(logMessageID);
        NotificationInfo(content);
        Speak(content);
    }
}
