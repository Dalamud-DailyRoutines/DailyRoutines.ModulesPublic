using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models.Packets.Upstream;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoHandleTeleportStuck : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoHandleTeleportStuckTitle"),
        Description = Lang.Get("AutoHandleTeleportStuckDescription", LuminaWrapper.GetLogMessageText(1665)),
        Category    = ModuleCategory.General
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    protected override void Init() =>
        LogMessageManager.Instance().RegPre(OnReceiveLogMessage);
    
    protected override void Uninit() =>
        LogMessageManager.Instance().Unreg(OnReceiveLogMessage);

    private static void OnReceiveLogMessage(ref bool isPrevented, ref uint logMessageID, ref LogMessageQueueItem values)
    {
        if (logMessageID != 1665) return;
        isPrevented = true;

        new UseActionPacket(ActionType.GeneralAction, 7, LocalPlayerState.EntityID, 0).Send();
    }
}
