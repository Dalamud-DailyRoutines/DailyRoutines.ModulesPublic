using DailyRoutines.Abstracts;
using DailyRoutines.Infos;

namespace DailyRoutines.Modules;

public class AutoNotifyDutyName : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoNotifyDutyNameTitle"),
        Description = GetLoc("AutoNotifyDutyNameDescription"),
        Category = ModuleCategories.Notice,
    };

    private static Config ModuleConfig = null!;
    
    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        DService.ClientState.TerritoryChanged += OnZoneChange;
    }

    public override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("SendTTS"), ref ModuleConfig.SendTTS))
            SaveConfig(ModuleConfig);
        
        if (ImGui.Checkbox(GetLoc("SendChat"), ref ModuleConfig.SendChat))
            SaveConfig(ModuleConfig);
        
        if (ImGui.Checkbox(GetLoc("SendNotification"), ref ModuleConfig.SendNotification))
            SaveConfig(ModuleConfig);
    }

    private static void OnZoneChange(ushort territory)
    {
        if (!PresetData.Contents.TryGetValue(territory, out var content)) return;

        var levelText = content.ClassJobLevelRequired == content.ClassJobLevelSync ||
                        content.ClassJobLevelRequired > content.ClassJobLevelSync
                            ? content.ClassJobLevelSync.ToString()
                            : $"{content.ClassJobLevelRequired}-{content.ClassJobLevelSync}";
        var message = GetLoc("AutoNotifyDutyName-NoticeMessage", levelText, content.Name.ExtractText(),
                             GetLoc("ILMinimum"), content.ItemLevelRequired,          
                             GetLoc("ILMaximum"), content.ItemLevelSync);
        
        if (ModuleConfig.SendTTS) Speak(message);
        if (ModuleConfig.SendChat) Chat(message);
        if (ModuleConfig.SendNotification) NotificationInfo(message);
    }

    public override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnZoneChange;
    }

    private class Config : ModuleConfiguration
    {
        public bool SendTTS = true;
        public bool SendNotification = true;
        public bool SendChat = true;
    }
}
