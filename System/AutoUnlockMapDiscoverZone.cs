using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OmenTools.Interop.Game.Models;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoUnlockMapDiscoverZone : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoUnlockMapDiscoverZoneTitle"),
        Description = Lang.Get("AutoUnlockMapDiscoverZoneDescription"),
        Category    = ModuleCategory.System
    };
    
    private static readonly CompSig                       AgentMapUpdateSig = new("48 89 5C 24 ?? 55 56 57 41 54 41 55 41 56 41 57 48 83 EC ?? 48 8B E9 E8");
    private delegate        void                          AgentMapUpdateDelegate(AgentMap* agent, uint updateCount);
    private                 Hook<AgentMapUpdateDelegate>? AgentMapUpdateHook;

    protected override void Init()
    {
        AgentMapUpdateHook ??= AgentMapUpdateSig.GetHook<AgentMapUpdateDelegate>(AgentMapUpdateDetour);
        AgentMapUpdateHook.Enable();
    }

    private void AgentMapUpdateDetour(AgentMap* agent, uint updateCount)
    {
        agent->CurrentMapDiscoveryFlag  = 0;
        agent->SelectedMapDiscoveryFlag = 0;
        AgentMapUpdateHook.Original(agent, updateCount);
        agent->CurrentMapDiscoveryFlag  = 0;
        agent->SelectedMapDiscoveryFlag = 0;
    }
}
