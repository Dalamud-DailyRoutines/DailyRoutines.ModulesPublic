using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

internal unsafe class AutoCancelStarContributor : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoCancelStarContributorTitle"),
        Description = Lang.Get("AutoCancelStarContributorDescription"),
        Category    = ModuleCategory.General,
        Author      = ["Shiyuvi"]
    };

    public override string ModuleName => nameof(AutoCancelStarContributor);

    protected override void Init()
    {
        IClientState.Instance().TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
    }

    protected override void Uninit()
    {
        IClientState.Instance().TerritoryChanged -= OnZoneChanged;
        IClientState.Instance().ClassJobChanged  -= OnClassJobChanged;

        FrameworkManager.Instance().Unreg(OnUpdate);
    }

    private static void OnZoneChanged
    (
        uint u
    )
    {
        FrameworkManager.Instance().Unreg(OnUpdate);
        IClientState.Instance().ClassJobChanged -= OnClassJobChanged;

        if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.CosmicExploration) return;

        FrameworkManager.Instance().Reg(OnUpdate, 10_000);
        IClientState.Instance().ClassJobChanged += OnClassJobChanged;
    }

    private static void OnClassJobChanged
    (
        uint classJobID
    ) =>
        OnUpdate(IFramework.Instance());

    private static void OnUpdate
    (
        IFramework framework
    )
    {
        if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.CosmicExploration)
        {
            FrameworkManager.Instance().Unreg(OnUpdate);
            return;
        }

        if (ICondition.Instance().IsBetweenAreas || IObjectTable.Instance().LocalPlayer is not { } localPlayer) return;

        var statusManager = localPlayer.ToStruct()->StatusManager;
        if (!statusManager.HasStatus(STAR_CONTRIBUTOR_BUFF_ID)) return;

        StatusManager.ExecuteStatusOff(STAR_CONTRIBUTOR_BUFF_ID);
    }

    #region 常量

    private const uint STAR_CONTRIBUTOR_BUFF_ID = 4409;

    #endregion
}
