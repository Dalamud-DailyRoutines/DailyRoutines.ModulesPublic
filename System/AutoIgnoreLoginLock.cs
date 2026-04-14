using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoIgnoreLoginLock : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoIgnoreLoginLockTitle"),
        Description = Lang.Get("AutoIgnoreLoginLockDescription", LuminaWrapper.GetLogMessageText(430)),
        Category    = ModuleCategory.System
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private static readonly CompSig AgentLobbyUpdateSig =
        new("40 55 56 41 55 48 8D 6C 24 ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 45 ?? 48 83 B9");
    private delegate void                            AgentLobbyUpdateDelegate(AgentLobby* agent, uint deltaTime);
    private          Hook<AgentLobbyUpdateDelegate>? AgentLobbyUpdateHook;

    protected override void Init()
    {
        AgentLobbyUpdateHook = AgentLobbyUpdateSig.GetHook<AgentLobbyUpdateDelegate>(AgentLobbyUpdateDetour);
        AgentLobbyUpdateHook.Enable();
    }

    private void AgentLobbyUpdateDetour(AgentLobby* agent, uint deltaTime)
    {
        agent->TemporaryLocked = false;
        AgentLobbyUpdateHook.Original(agent, deltaTime);
        agent->TemporaryLocked = false;
    }
}
