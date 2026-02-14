using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace DailyRoutines.ModulesPublic;

public class AutoNotifyReadyCheck : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoNotifyReadyCheckTitle"),
        Description = GetLoc("AutoNotifyReadyCheckDescription"),
        Category    = ModuleCategories.Notice,
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static readonly FrozenSet<uint> ValidLogMessages = [3790, 3791];
    
    protected override void Init() =>
        LogMessageManager.Instance().RegPost(OnLogMessage);
    
    protected override void Uninit() => 
        LogMessageManager.Instance().Unreg(OnLogMessage);

    private void OnLogMessage(uint logMessageID, LogMessageQueueItem item)
    {
        if (!ValidLogMessages.Contains(logMessageID)) return;
        
        NotificationInfo(LuminaWrapper.GetLogMessageText(3790));
        Speak(LuminaWrapper.GetLogMessageText(3790));
    }
}
