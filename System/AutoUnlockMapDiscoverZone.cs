using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoUnlockMapDiscoverZone : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoUnlockMapDiscoverZoneTitle"),
        Description = GetLoc("AutoUnlockMapDiscoverZoneDescription"),
        Category    = ModuleCategories.System,
    };
    
    private static readonly CompSig                       AgentMapUpdateSig = new("48 89 5C 24 ?? 55 56 57 41 54 41 55 41 56 41 57 48 83 EC ?? 48 8B E9 E8");
    private delegate        void                          AgentMapUpdateDelegate(AgentMap* agent, uint updateCount);
    private static          Hook<AgentMapUpdateDelegate>? AgentMapUpdateHook;

    protected override void Init()
    {
        AgentMapUpdateHook ??= AgentMapUpdateSig.GetHook<AgentMapUpdateDelegate>(AgentMapUpdateDetour);
        AgentMapUpdateHook.Enable();
    }

    private static void AgentMapUpdateDetour(AgentMap* agent, uint updateCount)
    {
        agent->CurrentMapDiscoveryFlag  = 0;
        agent->SelectedMapDiscoveryFlag = 0;
        AgentMapUpdateHook.Original(agent, updateCount);
        agent->CurrentMapDiscoveryFlag  = 0;
        agent->SelectedMapDiscoveryFlag = 0;
    }
}
