using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace DailyRoutines.ModulesPublic;

public class AutoBlockTitleMovie : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoBlockTitleMovieTitle"),
        Description = GetLoc("AutoBlockTitleMovieDescription"),
        Category    = ModuleCategories.System
    };

    protected override void Init() => 
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "Title", OnAddon);

    // 非即时的懒得 Hook 了
    private static unsafe void OnAddon(AddonEvent type, AddonArgs args)
    {
        var agent = AgentLobby.Instance();
        if (agent == null) return;

        agent->IdleTime = 0;
    }

    protected override void Uninit() => 
        DService.AddonLifecycle.UnregisterListener(OnAddon);
}
