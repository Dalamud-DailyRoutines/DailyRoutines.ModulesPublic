using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoConfirmPortraitUpdate : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoConfirmPortraitUpdateTitle"),
        Description = Lang.Get("AutoConfirmPortraitUpdateDescription"),
        Category    = ModuleCategory.UIOperation
    };
    
    private Config config = null!;

    protected override unsafe void Init()
    {
        config = Config.Load(this) ?? new();

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "BannerPreview", OnAddon);
        if (BannerPreview != null)
            OnAddon(AddonEvent.PostSetup, null);
    }
    
    protected override void Uninit() => 
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("SendNotification"), ref config.SendNotification))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("SendChat"), ref config.SendChat))
            config.Save(this);
    }

    private unsafe void OnAddon(AddonEvent type, AddonArgs? args)
    {
        BannerPreview->Callback(0);

        if (config.SendNotification)
            NotifyHelper.Instance().NotificationSuccess(Lang.Get("AutoConfirmPortraitUpdate-Notification"));
        if (config.SendChat)
            NotifyHelper.Instance().Chat(Lang.Get("AutoConfirmPortraitUpdate-Notification"));
    }

    private class Config : ModuleConfig
    {
        public bool SendChat         = true;
        public bool SendNotification = true;
    }
}
