using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
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

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };
    
    protected override void Init() =>
        GameState.Instance().Login += OnLogin;

    protected override void Uninit() =>
        GameState.Instance().Login -= OnLogin;

    private static void OnLogin()
    {
        var message = Lang.Get("AutoNotifyLogin-Notification");
        
        NotifyHelper.Instance().Chat(message, false);
        NotifyHelper.Instance().TrayInfo(message);
    }
}
