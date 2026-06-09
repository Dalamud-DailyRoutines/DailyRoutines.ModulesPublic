using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoNotifyLogin : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoNotifyLoginTitle"),
        Description = Lang.Get("AutoNotifyLoginDescription"),
        Category    = ModuleCategory.Notification
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private Config config;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        GameState.Instance().Login += OnLogin;
    }

    protected override void Uninit() =>
        GameState.Instance().Login -= OnLogin;

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("OnlyNotifyWhenBackground"), ref config.IsOnlyBackground))
            config.Save(this);
        
        ImGui.NewLine();
        
        if (ImGui.Checkbox(Lang.Get("SendNotification"), ref config.SendNotification))
            config.Save(this);
        
        if (ImGui.Checkbox(Lang.Get("SendSystemSound"), ref config.SendSystemSound))
            config.Save(this);
        
        if (ImGui.Checkbox(Lang.Get("SendTTS"), ref config.SendTTS))
            config.Save(this);
    }
    
    private void OnLogin()
    {
        // 后台
        if (config.IsOnlyBackground && GameState.IsForeground)
            return;

        var message = Lang.Get("AutoNotifyLogin-Notification");
        if (config.SendNotification)
            NotifyHelper.Instance().NotificationInfo(message);
        if (config.SendSystemSound)
            NotifyHelper.SystemInformation();
        if (config.SendTTS)
            NotifyHelper.Speak(message);
    }
    
    private class Config : ModuleConfig
    {
        public bool IsOnlyBackground = true;
        
        public bool SendNotification = true;
        public bool SendSystemSound = true;
        public bool SendTTS;
    }
}
