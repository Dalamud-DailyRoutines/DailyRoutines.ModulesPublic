using DailyRoutines.Abstracts;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace DailyRoutines.ModulesPublic;

public class AutoNotifyCountdown : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoNotifyCountdownTitle"),
        Description = GetLoc("AutoNotifyCountdownDescription"),
        Category    = ModuleCategories.Notice,
        Author      = ["HSS"]
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        LogMessageManager.Instance().RegPost(OnLogMessage);
    }
    
    protected override void Uninit() => 
        LogMessageManager.Instance().Unreg(OnLogMessage);

    private static void OnLogMessage(uint logMessageID, LogMessageQueueItem item)
    {
        if (logMessageID != 5255) return;
        if (ModuleConfig.OnlyNotifyWhenBackground && GameState.IsForeground) return;

        NotificationInfo(GetLoc("AutoNotifyCountdown-NotificationTitle"));
        Speak(GetLoc("AutoNotifyCountdown-NotificationTitle"));
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("OnlyNotifyWhenBackground"), ref ModuleConfig.OnlyNotifyWhenBackground))
            SaveConfig(ModuleConfig);
    }

    private class Config : ModuleConfiguration
    {
        public bool OnlyNotifyWhenBackground = true;
    }
}
