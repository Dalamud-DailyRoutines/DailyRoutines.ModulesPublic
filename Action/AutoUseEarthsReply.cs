using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoUseEarthsReply : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoUseEarthsReplyTitle"),
        Description = Lang.Get("AutoUseEarthsReplyDescription"),
        Category    = ModuleCategory.Action,
        Author      = ["ToxicStar"]
    };

    private Config config = null!;

    protected override void Init()
    {
        config =   Config.Load(this) ?? new();
        TaskHelper   ??= new() { TimeoutMS = 8_000 };

        UseActionManager.Instance().RegPostUseActionLocation(OnUseAction);
    }
    
    protected override void Uninit() =>
        UseActionManager.Instance().Unreg(OnUseAction);

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("AutoUseEarthsReply-UseWhenGuard"), ref config.UseWhenSprint))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("AutoUseEarthsReply-UseWhenSprint"), ref config.UseWhenGuard))
            config.Save(this);
    }

    private void OnUseAction(bool result, ActionType actionType, uint actionID, ulong targetID, Vector3 location, uint extraParam, byte a7)
    {
        if (actionType != ActionType.Action || actionID != RIDDLE_OF_EARTH_ACTION || !result) return;

        TaskHelper.Abort();
        TaskHelper.DelayNext(8_000, $"Delay_UseAction{EARTHS_REPLY_ACTION}", 1);
        TaskHelper.Enqueue
        (
            () =>
            {
                if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return;

                if (!config.UseWhenSprint && localPlayer.StatusList.HasStatus(SPRINT_STATUS)) return;
                if (!config.UseWhenGuard  && localPlayer.StatusList.HasStatus(GUARD_STATUS)) return;

                UseActionManager.Instance().UseActionLocation(ActionType.Action, EARTHS_REPLY_ACTION);
            },
            $"UseAction_{EARTHS_REPLY_ACTION}",
            500,
            weight: 1
        );
    }
    
    private class Config : ModuleConfig
    {
        public bool UseWhenGuard;
        public bool UseWhenSprint;
    }
    
    #region 常量
    
    private const uint RIDDLE_OF_EARTH_ACTION = 29482; // 金刚极意
    private const uint EARTHS_REPLY_ACTION    = 29483; // 金刚转轮
    private const uint SPRINT_STATUS          = 1342;  // 冲刺
    private const uint GUARD_STATUS           = 3054;  // 防御
    
    #endregion
}
