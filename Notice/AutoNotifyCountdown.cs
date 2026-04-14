using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoNotifyCountdown : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoNotifyCountdownTitle"),
        Description = Lang.Get("AutoNotifyCountdownDescription"),
        Category    = ModuleCategory.Notice,
        Author      = ["HSS"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private Config config = null!;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        LogMessageManager.Instance().RegPost(OnLogMessage);
    }

    protected override void Uninit() =>
        LogMessageManager.Instance().Unreg(OnLogMessage);

    private void OnLogMessage(uint logMessageID, LogMessageQueueItem item)
    {
        if (logMessageID != 5255) return;
        if (config.OnlyNotifyWhenBackground && GameState.IsForeground) return;

        NotifyHelper.Instance().NotificationInfo(Lang.Get("AutoNotifyCountdown-NotificationTitle"));
        NotifyHelper.Speak(Lang.Get("AutoNotifyCountdown-NotificationTitle"));
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("OnlyNotifyWhenBackground"), ref config.OnlyNotifyWhenBackground))
            config.Save(this);
    }

    private class Config : ModuleConfig
    {
        public bool OnlyNotifyWhenBackground = true;
    }
}
