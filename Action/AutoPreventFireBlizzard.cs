using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.ClientState.JobGauge.Types;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic;

public class AutoPreventFireBlizzard : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = Lang.Get("AutoPreventFireBlizzardTitle"),
        Description = Lang.Get
        (
            "AutoPreventFireBlizzardDescription",
            LuminaWrapper.GetJobName(25),                 // 黑魔法师
            LuminaWrapper.GetStatusName(3212),            // 星极火
            LuminaWrapper.GetActionName(ACITON_BLIZZARD), // 冰结
            LuminaWrapper.GetStatusName(3214),            // 灵极冰
            LuminaWrapper.GetActionName(ACITON_FIRE)      // 火炎
        ),
        Category = ModuleCategory.Action
    };

    protected override void Init() =>
        UseActionManager.Instance().RegPreUseActionLocation(OnUseAction);

    protected override void Uninit() =>
        UseActionManager.Instance().Unreg(OnUseAction);

    private static void OnUseAction
    (
        ref bool       isPrevented,
        ref ActionType type,
        ref uint       actionID,
        ref ulong      targetID,
        ref Vector3    location,
        ref uint       extraParam,
        ref byte       a7
    )
    {
        if (type != ActionType.Action) return;
        if (actionID is not (ACITON_BLIZZARD or ACITON_FIRE)) return;

        var gauage = IJobGauges.Instance().Get<BLMGauge>();

        if ((gauage.InAstralFire && actionID == ACITON_BLIZZARD) ||
            (gauage.InUmbralIce  && actionID == ACITON_FIRE))
        {
            isPrevented = true;
            
            if (Throttler.Shared.Throttle($"AutoPreventFireBlizzard.Notification.{actionID}", 2_500))
            {
                using var rented = new RentedSeStringBuilder();
                rented.Builder
                      .PushEdgeColorType(32)
                      .Append(LuminaWrapper.GetActionName(actionID))
                      .PopEdgeColorType();

                NotifyHelper.ToastQuest
                (
                    Lang.GetSe("AutoPreventFireBlizzard-Notification", rented),
                    new()
                    {
                        IconId = LuminaWrapper.GetActionIconID(actionID),
                    }
                );
            }
        }
    }

    #region 常量

    /// <summary>冰结</summary>
    private const uint ACITON_BLIZZARD = 142;

    /// <summary>火炎</summary>
    private const uint ACITON_FIRE = 141;

    #endregion
}
