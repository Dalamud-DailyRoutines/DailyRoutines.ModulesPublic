using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoCancelStarContributor : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoCancelStarContributorTitle"),
        Description = Lang.Get("AutoCancelStarContributorDescription"),
        Category    = ModuleCategory.General,
        Author      = ["Shiyuvi"]
    };

    protected override void Init()
    {
        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
    }

    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        DService.Instance().ClientState.ClassJobChanged  -= OnClassJobChanged;

        FrameworkManager.Instance().Unreg(OnUpdate);
    }

    private static void OnZoneChanged(uint u)
    {
        FrameworkManager.Instance().Unreg(OnUpdate);
        DService.Instance().ClientState.ClassJobChanged -= OnClassJobChanged;

        if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.CosmicExploration) return;

        FrameworkManager.Instance().Reg(OnUpdate, 10_000);
        DService.Instance().ClientState.ClassJobChanged += OnClassJobChanged;
    }

    private static void OnClassJobChanged(uint classJobID) =>
        OnUpdate(DService.Instance().Framework);

    private static void OnUpdate(IFramework framework)
    {
        if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.CosmicExploration)
        {
            FrameworkManager.Instance().Unreg(OnUpdate);
            return;
        }

        if (DService.Instance().Condition.IsBetweenAreas || DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return;

        var statusManager = localPlayer.ToStruct()->StatusManager;
        if (!statusManager.HasStatus(STAR_CONTRIBUTOR_BUFF_ID)) return;

        StatusManager.ExecuteStatusOff(STAR_CONTRIBUTOR_BUFF_ID);
    }

    #region 常量

    private const uint STAR_CONTRIBUTOR_BUFF_ID = 4409;

    #endregion
}
