using DailyRoutines.Abstracts;
using Dalamud.Game.ClientState.Objects.Enums;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using AgentId = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId;

namespace DailyRoutines.ModulesPublic;

public class AutoOpenNeedGreed : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title           = GetLoc("AutoOpenNeedGreedTitle"),
        Description     = GetLoc("AutoOpenNeedGreedDescription"),
        Category        = ModuleCategories.UIOperation,
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    protected override void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 3_000 };
        LogMessageManager.Instance().RegPost(OnPost);
        TargetManager.Instance().RegPostInteractWithObject(OnPostInteractWithObject);
    }

    protected override void Uninit()
    {
        LogMessageManager.Instance().Unreg(OnPost);
        TargetManager.Instance().Unreg(OnPostInteractWithObject);
    }
    
    private static void OnPostInteractWithObject(ulong result, IGameObject? target, bool checkLoS)
    {
        if (result == 0 || target is not { ObjectKind: ObjectKind.Treasure }) return;
        Throttler.Throttle("AutoOpenNeedGreed-SelfOpen", 1_000, true);
    }

    private static unsafe void OnPost(uint logMessageID, LogMessageQueueItem item)
    {
        if (logMessageID != 5194 || !Throttler.Check("AutoOpenNeedGreed-SelfOpen")) return;
        AgentId.Hud.SendEvent(0, 0, 2, " ");
    }
}
