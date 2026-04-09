using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoNotifyDutyName : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoNotifyDutyNameTitle"),
        Description = Lang.Get("AutoNotifyDutyNameDescription"),
        Category    = ModuleCategory.Notice
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private Config config = null!;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        DService.Instance().ClientState.TerritoryChanged += OnZoneChange;
    }
    
    protected override void Uninit() =>
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChange;
    
    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("SendTTS"), ref config.SendTTS))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("SendChat"), ref config.SendChat))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("SendNotification"), ref config.SendNotification))
            config.Save(this);
    }

    private unsafe void OnZoneChange(ushort territory)
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
                              : Sheets.Gears.Values
                                      .Where(x => x.LevelEquip != 1 && x.LevelEquip <= content.ClassJobLevelSync)
                                      .OrderByDescending(x => x.LevelItem.RowId)
                                      .FirstOrDefault().LevelItem.RowId;

        var message = Lang.Get
        (
            "AutoNotifyDutyName-NoticeMessage",
            levelText,
            content.Name.ToString(),
            Lang.Get("ILMinimum"),
            content.ItemLevelRequired,
            Lang.Get("ILMaximum"),
            content.ItemLevelSync != 0 ? content.ItemLevelSync : maxILGearIL
        );

        if (config.SendTTS)
            NotifyHelper.Speak(message);
        if (config.SendChat)
            NotifyHelper.Instance().Chat(message);
        if (config.SendNotification)
            NotifyHelper.Instance().NotificationInfo(message);
    }
    
    private class Config : ModuleConfig
    {
        public bool SendChat         = true;
        public bool SendNotification = true;
        public bool SendTTS          = true;
    }
}
