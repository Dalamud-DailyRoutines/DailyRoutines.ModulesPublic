using System.Linq;
using DailyRoutines.Abstracts;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public class AutoNotifyDutyName : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoNotifyDutyNameTitle"),
        Description = GetLoc("AutoNotifyDutyNameDescription"),
        Category    = ModuleCategories.Notice,
    };

    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        DService.ClientState.TerritoryChanged += OnZoneChange;
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("SendTTS"), ref ModuleConfig.SendTTS))
            SaveConfig(ModuleConfig);
        
        if (ImGui.Checkbox(GetLoc("SendChat"), ref ModuleConfig.SendChat))
            SaveConfig(ModuleConfig);
        
        if (ImGui.Checkbox(GetLoc("SendNotification"), ref ModuleConfig.SendNotification))
            SaveConfig(ModuleConfig);
    }

    private static unsafe void OnZoneChange(ushort territory)
    {
        if (GameMain.Instance()->CurrentContentFinderConditionId == 0 ||
            !LuminaGetter.TryGetRow<ContentFinderCondition>(GameMain.Instance()->CurrentContentFinderConditionId, out var content))
            return;

        var levelText = content.ClassJobLevelRequired == content.ClassJobLevelSync ||
                        content.ClassJobLevelRequired > content.ClassJobLevelSync
                            ? content.ClassJobLevelSync.ToString()
                            : $"{content.ClassJobLevelRequired}-{content.ClassJobLevelSync}";

        var maxILGearIL = content.ClassJobLevelSync == 0
                            ? 0
                            : PresetSheet.Gears.Values
                                         .Where(x => x.LevelEquip != 1 && x.LevelEquip <= content.ClassJobLevelSync)
                                         .OrderByDescending(x => x.LevelItem.RowId)
                                         .FirstOrDefault().LevelItem.RowId;
        
        var message = GetLoc("AutoNotifyDutyName-NoticeMessage", levelText, content.Name.ExtractText(),
                             GetLoc("ILMinimum"), content.ItemLevelRequired,          
                             GetLoc("ILMaximum"), content.ItemLevelSync != 0 ? content.ItemLevelSync : maxILGearIL);
        
        if (ModuleConfig.SendTTS) 
            Speak(message);
        if (ModuleConfig.SendChat) 
            Chat(message);
        if (ModuleConfig.SendNotification) 
            NotificationInfo(message);
    }

    protected override void Uninit() => 
        DService.ClientState.TerritoryChanged -= OnZoneChange;

    private class Config : ModuleConfiguration
    {
        public bool SendTTS          = true;
        public bool SendNotification = true;
        public bool SendChat         = true;
    }
}
