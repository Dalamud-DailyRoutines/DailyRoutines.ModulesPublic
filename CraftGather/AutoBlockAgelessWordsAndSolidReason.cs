using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.CraftGather;

public class AutoBlockAgelessWordsAndSolidReason : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoBlockAgelessWordsAndSolidReasonTitle"),
        Description = Lang.Get("AutoBlockAgelessWordsAndSolidReasonDescription"),
        Category    = ModuleCategory.CraftGather
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

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
        if (type != ActionType.Action) 
            return;
        
        if (actionID != SOLID_REASON_ACTION_ID && actionID != AGELESS_WORDS_ACTION_ID)
            return;
        
        if (!LocalPlayerState.HasStatus(EUREKA_MOMENT_STATUS_ID, out _))
            return;

        isPrevented = true;

        var message = Lang.Get
        (
            "AutoBlockAgelessWordsAndSolidReason-Notification-Blocked",
            LuminaWrapper.GetStatusName(EUREKA_MOMENT_STATUS_ID),
            LuminaWrapper.GetActionName(actionID)
        );
        NotifyHelper.ToastError(message);
    }

    #region 常量

    // 石工之理
    private const uint SOLID_REASON_ACTION_ID  = 232;
    
    // 农夫之智
    private const uint AGELESS_WORDS_ACTION_ID = 215;

    // 理智同兴预备
    private const uint EUREKA_MOMENT_STATUS_ID = 2765;

    #endregion
}
