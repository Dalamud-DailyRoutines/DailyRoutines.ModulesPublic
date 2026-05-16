using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.Dalamud;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;
using Action = Lumina.Excel.Sheets.Action;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoLucidDreaming : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoLucidDreamingTitle"),
        Description = Lang.Get("AutoLucidDreamingDescription"),
        Category    = ModuleCategory.Action,
        Author      = ["qingsiweisan"]
    };
    
    private Config config = null!;

    protected override void Init()
    {
        TaskHelper   ??= new() { TimeoutMS = 30_000, ShowDebug = true };
        config =   Config.Load(this) ?? new();

        UseActionManager.Instance().RegPostCharacterCompleteCast(OnCompleteCast);
        UseActionManager.Instance().RegPostUseActionLocation(OnUseAction);
    }

    protected override void Uninit()
    {
        UseActionManager.Instance().Unreg(OnCompleteCast);
        UseActionManager.Instance().Unreg(OnUseAction);
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("OnlyInDuty"), ref config.OnlyInDuty))
            config.Save(this);

        ImGui.SetNextItemWidth(250f * GlobalUIScale);
        if (ImGui.DragInt("##MpThresholdSlider", ref config.MpThreshold, 100f, 3000, 9000, $"{LuminaWrapper.GetAddonText(233)}: %d"))
            config.Save(this);

        ImGui.NewLine();
        
        if (ImGui.Checkbox(Lang.Get("SendChat"), ref config.SendChat))
            config.Save(this);
        
        if (ImGui.Checkbox(Lang.Get("SendNotification"), ref config.SendNotification))
            config.Save(this);
    }
    
    private void OnCompleteCast
    (
        bool         result,
        IBattleChara player,
        ActionType   type,
        uint         actionID,
        uint         spellID,
        GameObjectId animationTargetID,
        Vector3      location,
        float        rotation,
        short        lastUsedActionSequence,
        int          animationVariation,
        int          ballistaEntityID
    )
    {
        // 施法不成功
        // 在 PVP 区域内
        // 应该在副本内但现在不在
        // 非本地玩家
        // 不在有效职业范围内
        // 当前魔力值大于设定阈值
        if (!result                                                      ||
            GameState.IsInPVPArea                                        ||
            (config.OnlyInDuty && GameState.ContentFinderCondition == 0) ||
            player.EntityID != LocalPlayerState.EntityID                 ||
            !ValidClassJobs.Contains(player.ClassJob.RowId)              ||
            player.CurrentMp > config.MpThreshold)
            return;
        
        // 无法获取技能信息
        // 能力技 (假设用户网络非常烂只能单插, 已经使用能力技的情况下不要再使用)
        if (!LuminaGetter.TryGetRow(actionID, out Action actionRow) ||
            actionRow.Recast100ms == 0)
            return;
        
        // ActionManager 为空 (怎会如此)
        // 醒梦正在冷却
        var manager = ActionManager.Instance();
        if (manager == null ||
            !manager->IsActionOffCooldown(ActionType.Action, LUCID_DREAMING_ID))
            return;

        var recastGroupTypeOne  = manager->GetRecastGroup((int)ActionType.Action, actionID);
        var recastDetailTypeOne = recastGroupTypeOne == -1 ? null : manager->GetRecastGroupDetail(recastGroupTypeOne);
        
        var recastGroupTypeTwo  = manager->GetAdditionalRecastGroup(ActionType.Action, actionID);
        var recastDetailTypeTwo = recastGroupTypeTwo == -1 ? null : manager->GetRecastGroupDetail(recastGroupTypeTwo);
        
        // 复唱判断（类型1）
        if (recastDetailTypeOne != null)
        {
            // 已经可以发动下一个技能了
            // 剩余的窗口再插一个技能已经不够了
            if (!recastDetailTypeOne->IsActive ||
                (recastDetailTypeOne->Total - recastDetailTypeOne->Elapsed) * 1000 < RECAST_TIME_WINDOW)
                return;
        }
        else if (recastDetailTypeTwo != null)
        {
            // 已经可以发动下一个技能了
            // 剩余的窗口再插一个技能已经不够了
            if (!recastDetailTypeTwo->IsActive ||
                (recastDetailTypeTwo->Total - recastDetailTypeTwo->Elapsed) * 1000 < RECAST_TIME_WINDOW)
                return;
        }
        
        // 已经在连点下一个技能了
        if (manager->QueuedActionId != 0)
        {
            // 假设插的是其他东西, 比如食物之类的, 为了保险起见还是不要了
            if (manager->QueuedActionType != ActionType.Action)
                return;
            
            // 无法获取下一个技能信息
            // 下一个技能是能力技
            if (!LuminaGetter.TryGetRow(manager->QueuedActionId, out Action nextActionRow) ||
                nextActionRow.Recast100ms == 0)
                return;
            
            // 下个技能是公 CD 技能
        }
        
        // 下一个技能为空
        EnqueueUseLucidDreaming();
    }
    
    private void OnUseAction(bool result, ActionType actionType, uint actionID, ulong targetID, Vector3 location, uint extraParam, byte a7)
    {
        // 施法不成功
        // 在 PVP 区域内
        // 应该在副本内但现在不在
        // 不在有效职业范围内
        if (!result                                                      ||
            GameState.IsInPVPArea                                        ||
            (config.OnlyInDuty && GameState.ContentFinderCondition == 0) ||
            !ValidClassJobs.Contains(LocalPlayerState.ClassJob))
            return;
        
        TaskHelper.Abort();
    }

    private void EnqueueUseLucidDreaming()
    {
        // 不允许同时有多个插入
        TaskHelper.Abort();
        
        var manager = ActionManager.Instance();
        if (manager == null) return;
        
        // 为了合法插入
        TaskHelper.DelayNext((int)MathF.Max(ANIMATION_LOCK, manager->AnimationLock * 1000), "等待动画锁结束");
        
        // 我们还是得检查一次
        TaskHelper.Enqueue(() =>
        {
            // 已经在连点下一个技能了
            if (manager->QueuedActionId != 0)
            {
                // 假设插的是其他东西, 比如食物之类的, 为了保险起见还是不要了
                if (manager->QueuedActionType != ActionType.Action)
                {
                    TaskHelper.Abort();
                    return;
                }
            
                // 无法获取下一个技能信息
                // 下一个技能是能力技
                if (!LuminaGetter.TryGetRow(manager->QueuedActionId, out Action nextActionRow) ||
                    nextActionRow.Recast100ms == 0)
                {
                    TaskHelper.Abort();
                    return;
                }
            
                // 下个技能是公 CD 技能 (终于等到这一刻)
            }
        }, "检查当前状态是否合法");

        // 使用醒梦
        TaskHelper.Enqueue
        (
            () =>
            {
                var status = UseActionManager.Instance().UseAction(ActionType.Action, LUCID_DREAMING_ID);
                if (!status)
                    return false;

                if (Throttler.Shared.Throttle("AutoLucidDreaming-SendChat", 10_000))
                {
                    using var rented  = new RentedSeStringBuilder();
                    rented.Builder
                          .PushColorType(32)
                          .Append(LuminaWrapper.GetActionName(LUCID_DREAMING_ID))
                          .PopColorType();

                    var message = Lang.GetSe("AutoLucidDreaming-Notification", rented.Builder, LocalPlayerState.Object?.CurrentMp ?? 0);
                    
                    if (config.SendChat)
                        NotifyHelper.Instance().Chat(message);
                    if (config.SendNotification)
                        NotifyHelper.Instance().NotificationInfo
                        (
                            message.ToString(),
                            options: new()
                            {
                                Icon = DService.Instance().Texture.GetFromGameIcon(LuminaWrapper.GetActionIconID(LUCID_DREAMING_ID))
                            }
                        );
                }
                
                if (config.SendChat &&
                    Throttler.Shared.Throttle("AutoLucidDreaming-SendChat", 10_000))
                    NotifyHelper.Instance().Chat(Lang.Get("AutoLucidDreaming-Notification", LocalPlayerState.Object?.CurrentMp ?? 0));

                return true;
            },
            "使用醒梦"
        );
    }

    private class Config : ModuleConfig
    {
        public int  MpThreshold = 7000;
        public bool OnlyInDuty;
        
        public bool SendChat;
        public bool SendNotification = true;
    }
    
    #region 常量
    
    private const int  RECAST_TIME_WINDOW = 500;
    private const int  ANIMATION_LOCK     = 100;
    private const uint LUCID_DREAMING_ID  = 7562;

    private static readonly FrozenSet<uint> ValidClassJobs =
    [
        6,  // 幻术师
        24, // 白魔法师
        26, // 秘术师
        27, // 召唤师
        28, // 学者
        33, // 占星术士
        35, // 赤魔法师
        36, // 青魔法师
        40, // 贤者
        42  // 绘灵法师
    ];

    #endregion
}
