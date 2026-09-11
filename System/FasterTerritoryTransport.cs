using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using OmenTools.Info.Game.Enums;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class FasterTerritoryTransport : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title            = Lang.Get("FasterTerritoryTransportTitle"),
        Description      = Lang.Get("FasterTerritoryTransportDescription"),
        Category         = ModuleCategory.System,
        ModulesRecommend = ["NoUIFade"]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    protected override void Init() =>
        ICondition.Instance().ConditionChange += OnConditionChanged;

    protected override void Uninit() =>
        ICondition.Instance().ConditionChange -= OnConditionChanged;

    private static unsafe void OnConditionChanged
    (
        ConditionFlag flag,
        bool          value
    )
    {
        if (flag != ConditionFlag.BetweenAreas) return;
        if (InvalidWarpTypes.Contains(WarpInfo.Instance()->WarpType)) return;

        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.StartTerritoryTransport);
        WarpInfo.Instance()->CompleteWarp(0, 0);
    }

    #region 常量

    private static readonly FrozenSet<WarpType> InvalidWarpTypes =
    [
        WarpType.Login,
        WarpType.Teleport,
        WarpType.Return,
        WarpType.Resurrection,
        WarpType.HousingTeleport,
        WarpType.TownTranslate,
        WarpType.WorldTransfer
    ];

    #endregion
}
