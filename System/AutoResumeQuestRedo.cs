using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using OmenTools.Info.Game.Enums;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

internal class AutoResumeQuestRedo : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoResumeQuestRedoTitle"),
        Description = Lang.Get("AutoResumeQuestRedoDescription"),
        Category    = ModuleCategory.System
    };

    public override string ModuleName => nameof(AutoResumeQuestRedo);

    protected override void Init()
    {
        GameState.Instance().Login               += OnLogin;
        IClientState.Instance().TerritoryChanged += OnZoneChanged;
    }

    protected override void Uninit()
    {
        IClientState.Instance().TerritoryChanged -= OnZoneChanged;
        GameState.Instance().Login               -= OnLogin;
    }

    private static void OnZoneChanged
    (
        uint zone
    )
    {
        if (GameState.ContentFinderCondition == 0) return;
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.ContinueQuestRedo);
    }

    private static void OnLogin() =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.ContinueQuestRedo);
}
