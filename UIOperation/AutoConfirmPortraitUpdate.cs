using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;

namespace DailyRoutines.ModulesPublic;

public class AutoConfirmPortraitUpdate : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoConfirmPortraitUpdateTitle"),
        Description = GetLoc("AutoConfirmPortraitUpdateDescription"),
        Category    = ModuleCategories.UIOperation
    };

    private static Config ModuleConfig = null!;

    protected override unsafe void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "BannerPreview", OnAddon);
        if (BannerPreview != null) 
            OnAddon(AddonEvent.PostSetup, null);
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("SendNotification"), ref ModuleConfig.SendNotification))
            SaveConfig(ModuleConfig);
        
        if (ImGui.Checkbox(GetLoc("SendChat"), ref ModuleConfig.SendChat))
            SaveConfig(ModuleConfig);
    }

    private static unsafe void OnAddon(AddonEvent type, AddonArgs? args)
    {
        Callback(BannerPreview, true, 0);
        
        if (ModuleConfig.SendNotification) 
            NotificationSuccess(GetLoc("AutoConfirmPortraitUpdate-Notification"));
        if (ModuleConfig.SendChat)
            Chat(GetLoc("AutoConfirmPortraitUpdate-Notification"));
    }

    protected override void Uninit() => DService.AddonLifecycle.UnregisterListener(OnAddon);

    private class Config : ModuleConfiguration
    {
        public bool SendNotification = true;
        public bool SendChat         = true;
    }
}
