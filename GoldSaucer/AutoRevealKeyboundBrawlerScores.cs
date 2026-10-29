using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.Expansion.FFXIV.Client.UI;

namespace DailyRoutines.ModulesPublic;

public class AutoRevealKeyboundBrawlerScores : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoRevealKeyboundBrawlerScoresTitle"),
        Description = Lang.Get("AutoRevealKeyboundBrawlerScoresDescription"),
        Category    = ModuleCategory.GoldSaucer
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    protected override unsafe void Init()
    {
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup, "KTGResult", OnAddonSetup);

        RevealScores((AddonKTGResult*)KTGResult);
    }

    protected override void Uninit() =>
        IAddonLifecycle.Instance().UnregisterListener(OnAddonSetup);

    private static unsafe void OnAddonSetup
    (
        AddonEvent type,
        AddonArgs  args
    ) =>
        RevealScores((AddonKTGResult*)args.Addon.Address);

    private static unsafe void RevealScores
    (
        AddonKTGResult* addon
    )
    {
        if (addon == null ||
            !addon->IsActive)
            return;

        foreach (ref var entry in addon->ScoreEntries)
            entry.HideScore = false;

        addon->UpdateScoreRows();
    }
}
