using DailyRoutines.Abstracts;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DailyRoutines.ModulesPublic;

public class AutoHandleTeleportStuck : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoHandleTeleportStuckTitle"),
        Description = GetLoc("AutoHandleTeleportStuckDescription", LuminaWrapper.GetLogMessageText(1665)),
        Category    = ModuleCategories.General
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    protected override void Init() => 
        LogMessageManager.Register(OnReceiveLogMessage);

    private static void OnReceiveLogMessage(ref bool isPrevented, ref uint logMessageID)
    {
        if (logMessageID != 1665) return;
        isPrevented = true;
        
        new UseActionPacket(ActionType.GeneralAction, 7, LocalPlayerState.EntityID, 0).Send();
    }
    
    protected override void Uninit() => 
        LogMessageManager.Unregister(OnReceiveLogMessage); 
}
