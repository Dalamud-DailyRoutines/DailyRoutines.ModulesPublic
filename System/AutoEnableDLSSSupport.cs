using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoEnableDLSSSupport : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoEnableDLSSSupportTitle"),
        Description = Lang.Get("AutoEnableDLSSSupportDescription"),
        Category    = ModuleCategory.System
    };

    public override ModulePermission Permission { get; } = new() { CNOnly = true, KROnly = true, TCOnly = true };

    protected override void Init()
    {
        
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            NotifyErrorGameDirectory();
            return;
        }
        
        var gameDirectory = Directory.GetParent(processPath).FullName;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            NotifyErrorGameDirectory();
            return;
        }
        
        var installDirectory = Directory.GetParent(gameDirectory).FullName;
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            NotifyErrorGameDirectory();
            return;
        }
        
        var bootDirectory = Path.Combine(installDirectory, "boot");
        Directory.CreateDirectory(bootDirectory);
        
        var ffxivbootPath           = Path.Combine(bootDirectory, "ffxivboot.exe");
        var ffxivupdater64Path = Path.Combine(bootDirectory, "ffxivupdater64.exe");

        if (Path.Exists(ffxivbootPath) && File.Exists(ffxivupdater64Path))
        {
            NotifyFinished();
            return;
        }
        
        if (!Path.Exists(ffxivbootPath))
            File.WriteAllBytes(ffxivbootPath, []);
        
        if (!Path.Exists(ffxivupdater64Path))
            File.WriteAllBytes(ffxivupdater64Path, []);
        
        NotifyFinished();
    }

    private static void NotifyErrorGameDirectory()
    {
        var error = Lang.Get("AutoEnableDLSSSupport-Error-GameDirectory");
        NotifyHelper.Instance().ChatError(error);
        NotifyHelper.Instance().NotificationError(error);
    }
    
    private static void NotifyFinished()
    {
        var error = Lang.Get("AutoEnableDLSSSupport-Notification-Finished");
        NotifyHelper.Instance().Chat(error);
        NotifyHelper.Instance().NotificationInfo(error);
    }
}
