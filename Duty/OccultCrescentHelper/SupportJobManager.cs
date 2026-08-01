using System.Numerics;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using OmenTools.Info.Game;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;
using OmenTools.Threading.TaskHelper;
using Action = Lumina.Excel.Sheets.Action;
using TerritoryIntendedUse = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace DailyRoutines.ModulesPublic.Duty;

public partial class OccultCrescentHelper
{
    private class SupportJobManager
    (
        OccultCrescentHelper mainModule
    ) : BaseIslandModule(mainModule)
    {
        private static TaskHelper? SupportJobTaskHelper;
        private static TaskHelper? ActionInsertionTaskHelper;

        private ActionInsertionRule[] actionInsertionRules = [];

        public override void Init()
        {
            SupportJobTaskHelper      = new();
            ActionInsertionTaskHelper = new() { TimeoutMS = 60_000 };

            actionInsertionRules =
            [
                new
                (
                    CrescentSupportJob.Bard,
                    ACTION_OFFENSIVE_ARIA,
                    () => MainModule.config.IsEnabledBardOffensiveAria,
                    ShouldUseOffensiveAria
                )
            ];

            CommandManager.Instance().AddSubCommand
            (
                COMMAND_BUFF,
                new(OnCommandBuff) { HelpMessage = $"{Lang.Get("OccultCrescentHelper-Command-PBuff-Help")}" }
            );

            UseActionManager.Instance().RegPreUseAction(OnPreUseAction);
            UseActionManager.Instance().RegPreCharacterCompleteCast(OnPreCompleteCast);
            UseActionManager.Instance().RegPostCharacterCompleteCast(OnPostCompleteCast);
            UseActionManager.Instance().RegPostUseActionLocation(OnPostUseAction);
        }

        public override void Uninit()
        {
            CommandManager.Instance().RemoveSubCommand(COMMAND_BUFF);

            SupportJobTaskHelper?.Abort();
            SupportJobTaskHelper?.Dispose();
            SupportJobTaskHelper = null;

            ActionInsertionTaskHelper?.Abort();
            ActionInsertionTaskHelper?.Dispose();
            ActionInsertionTaskHelper = null;
            actionInsertionRules      = [];

            UseActionManager.Instance().Unreg(OnPreUseAction);
            UseActionManager.Instance().Unreg(OnPreCompleteCast);
            UseActionManager.Instance().Unreg(OnPostCompleteCast);
            UseActionManager.Instance().Unreg(OnPostUseAction);
        }

        public override void DrawConfig()
        {
            using var id = ImRaii.PushId("SupportJobManager");

            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), LuminaWrapper.GetMKDSupportJobName(6));

            using (ImRaii.PushIndent())
            {
                if (ImGui.Checkbox
                    (
                        $"{Lang.Get("OccultCrescentHelper-SupportJobManager-Bard-OffensiveAria")}##BardOffensiveAria",
                        ref MainModule.config.IsEnabledBardOffensiveAria
                    ))
                    MainModule.config.Save(MainModule);
                ImGuiOm.HelpMarker(Lang.Get("OccultCrescentHelper-SupportJobManager-Bard-OffensiveAria-Help"), 20f * GlobalUIScale);
            }

            ImGui.NewLine();

            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), LuminaWrapper.GetMKDSupportJobName(3));

            using (ImRaii.PushIndent())
            {
                if (ImGui.Checkbox
                    (
                        $"{Lang.Get("OccultCrescentHelper-SupportJobManager-Monk-PhantomKickNoMove")}##NoMoveMonk",
                        ref MainModule.config.IsEnabledMonkKickNoMove
                    ))
                    MainModule.config.Save(MainModule);
                ImGuiOm.HelpMarker(Lang.Get("OccultCrescentHelper-SupportJobManager-Monk-PhantomKickNoMove-Help"), 20f * GlobalUIScale);
            }

            ImGui.NewLine();

            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), LuminaWrapper.GetMKDSupportJobName(2));

            using (ImRaii.PushIndent())
            {
                if (ImGui.Checkbox
                    (
                        $"{Lang.Get("OccultCrescentHelper-SupportJobManager-Berserker-RageAutoFace")}##BerserkerRageAutoFace",
                        ref MainModule.config.IsEnabledBerserkerRageAutoFace
                    ))
                    MainModule.config.Save(MainModule);
                ImGuiOm.HelpMarker(Lang.Get("OccultCrescentHelper-SupportJobManager-Berserker-RageAutoFace-Help"), 20f * GlobalUIScale);

                if (ImGui.Checkbox
                    (
                        $"{Lang.Get("OccultCrescentHelper-SupportJobManager-Berserker-RageReplace")}##BerserkerRageReplace",
                        ref MainModule.config.IsEnabledBerserkerRageReplace
                    ))
                    MainModule.config.Save(MainModule);
                ImGuiOm.HelpMarker(Lang.Get("OccultCrescentHelper-SupportJobManager-Berserker-RageReplace-Help"), 20f * GlobalUIScale);
            }

            ImGui.NewLine();

            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Command"));

            using (ImRaii.PushIndent())
                ImGui.TextWrapped($"/pdr {COMMAND_BUFF} {Lang.Get("OccultCrescentHelper-Command-PBuff-Help")}");
        }

        #region 入队

        private static void EnqueueBuffSequence()
        {
            if (!CrescentSupportJob.TryFindKnowledgeCrystal(out var gameObject) ||
                LocalPlayerState.DistanceToObject2DSquared(gameObject) > 10)
            {
                NotifyHelper.ToastError(Lang.Get("OccultCrescentHelper-OthersManager-Notification-CrystalNotFound"));
                return;
            }

            var currentJob = CrescentSupportJob.GetCurrentSupportJob();

            var allJobs = CrescentSupportJob.AllJobs
                                            .Where(x => x.IsLongTimeStatusUnlocked())
                                            .OrderBy
                                            (x => x.JobType switch
                                                {
                                                    CrescentSupportJobType.Knight => 0,
                                                    CrescentSupportJobType.Bard   => 1,
                                                    CrescentSupportJobType.Monk   => 3,
                                                    CrescentSupportJobType.Dancer => 4,
                                                    _                             => 999
                                                }
                                            )
                                            .ToList();
            allJobs.ForEach(x => StatusManager.ExecuteStatusOff(x.LongTimeStatusID));

            SupportJobTaskHelper.Abort();
            SupportJobTaskHelper.Enqueue
            (() =>
                {
                    if (!DService.Instance().Condition[ConditionFlag.Mounted]) return true;

                    ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.Dismount);
                    return true;
                }
            );

            if (CrescentSupportJob.Freelancer.IsActionUnlocked(ACTION_FREELANCER_BUFF))
            {
                SupportJobTaskHelper.Enqueue
                (() =>
                    {
                        if (CrescentSupportJob.Freelancer.IsThisJob()) return true;
                        if (!Throttler.Shared.Throttle("OthersManager-OthersManager-ChangeSupportJob", 750)) return false;

                        CrescentSupportJob.Freelancer.ChangeTo();
                        return false;
                    }
                );
                SupportJobTaskHelper.Enqueue
                    (() => UseActionManager.Instance().UseAction(ActionType.Action, ACTION_FREELANCER_BUFF));
            }
            else
            {
                foreach (var sJob in allJobs)
                {
                    SupportJobTaskHelper.Enqueue
                    (() =>
                        {
                            if (sJob.IsThisJob()) return true;
                            if (!Throttler.Shared.Throttle("OthersManager-OthersManager-ChangeSupportJob", 750)) return false;

                            sJob.ChangeTo();
                            return false;
                        }
                    );
                    SupportJobTaskHelper.Enqueue
                    (() =>
                        {
                            if (sJob.IsWithLongTimeStatus()) return true;

                            UseActionManager.Instance().UseAction(ActionType.Action, sJob.LongTimeStatusActionID);
                            return false;
                        }
                    );
                }
            }

            SupportJobTaskHelper.Enqueue
            (() =>
                {
                    if (currentJob.IsThisJob()) return true;
                    if (!Throttler.Shared.Throttle("OthersManager-OthersManager-ChangeSupportJob", 750)) return false;

                    currentJob.ChangeTo();
                    return false;
                }
            );
        }

        private static unsafe void EnqueueActionInsertion
        (
            ActionInsertionRule insertionRule
        )
        {
            ActionInsertionTaskHelper.Abort();

            var manager = ActionManager.Instance();
            if (manager == null) return;

            ActionInsertionTaskHelper.DelayNext((int)MathF.Max(ANIMATION_LOCK, manager->AnimationLock * 1000), "等待动画锁结束");
            ActionInsertionTaskHelper.Enqueue
            (
                () =>
                {
                    if (manager->QueuedActionId == 0) return;

                    if (manager->QueuedActionType != ActionType.Action)
                    {
                        ActionInsertionTaskHelper.Abort();
                        return;
                    }

                    if (!LuminaGetter.TryGetRow(manager->QueuedActionId, out Action nextActionRow) ||
                        nextActionRow.Recast100ms == 0)
                        ActionInsertionTaskHelper.Abort();
                },
                "检查当前状态是否合法"
            );
            ActionInsertionTaskHelper.Enqueue
            (
                () =>
                {
                    if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer ||
                        !IsActionInsertionReady(insertionRule, localPlayer, manager))
                    {
                        ActionInsertionTaskHelper.Abort();
                        return true;
                    }

                    return UseActionManager.Instance().UseAction(ActionType.Action, insertionRule.ActionID);
                },
                $"UseAction_{insertionRule.ActionID}"
            );
        }

        #endregion

        #region 事件

        private void OnPreUseAction
        (
            ref bool                        isPrevented,
            ref ActionType                  actionType,
            ref uint                        actionID,
            ref ulong                       targetID,
            ref uint                        extraParam,
            ref ActionManager.UseActionMode queueState,
            ref uint                        comboRouteID
        )
        {
            if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent)
                return;

            // 狂战士自动面向
            if (MainModule.config.IsEnabledBerserkerRageAutoFace)
            {
                if (actionType != ActionType.Action || actionID != 41592) return;

                if (TargetManager.Target == null)
                    ChatManager.Instance().SendMessage("/tenemy");
                ChatManager.Instance().SendMessage("/facetarget");
            }
        }

        private static void OnPostUseAction
        (
            bool       result,
            ActionType actionType,
            uint       actionID,
            ulong      targetID,
            Vector3    location,
            uint       extraParam,
            byte       a7
        )
        {
            if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent)
                return;

            if (!result) return;

            if (actionType == ActionType.Action                        &&
                LuminaGetter.TryGetRow(actionID, out Action actionRow) &&
                actionRow.Recast100ms != 0)
                return;

            ActionInsertionTaskHelper.Abort();
        }

        private void OnPreCompleteCast
        (
            ref bool         isPrevented,
            ref IBattleChara battleChara,
            ref ActionType   actionType,
            ref uint         actionID,
            ref uint         spellID,
            ref GameObjectId animationTargetID,
            ref Vector3      position,
            ref float        f,
            ref short        s,
            ref int          i,
            ref int          ballistaEntityID
        )
        {
            if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent)
                return;

            if (battleChara.EntityID != LocalPlayerState.EntityID)
                return;

            // 武僧无位移
            if (MainModule.config.IsEnabledMonkKickNoMove)
            {
                if (actionType == ActionType.Action && actionID == 41595)
                    actionID = spellID = 7;
            }

            // 狂怒攻击替换
            if (MainModule.config.IsEnabledBerserkerRageReplace)
            {
                if (actionType == ActionType.Action && actionID == 41593)
                    actionID = spellID = 3549;
            }
        }

        private unsafe void OnPostCompleteCast
        (
            bool         result,
            IBattleChara player,
            ActionType   actionType,
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
            if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent)
                return;

            if (!result                                        ||
                !ICondition.Instance()[ConditionFlag.InCombat] ||
                player.EntityID != LocalPlayerState.EntityID)
                return;

            if (!LuminaGetter.TryGetRow(actionID, out Action actionRow) ||
                actionRow.Recast100ms == 0)
                return;

            var manager = ActionManager.Instance();
            if (manager == null) return;

            var insertionRule = actionInsertionRules.FirstOrDefault(x => IsActionInsertionReady(x, player, manager));
            if (insertionRule == null) return;

            var recastGroupTypeOne = manager->GetRecastGroup((int)ActionType.Action, actionID);
            var recastDetailTypeOne = recastGroupTypeOne == -1 ?
                                          null :
                                          manager->GetRecastGroupDetail(recastGroupTypeOne);

            var recastGroupTypeTwo = manager->GetAdditionalRecastGroup(ActionType.Action, actionID);
            var recastDetailTypeTwo = recastGroupTypeTwo == -1 ?
                                          null :
                                          manager->GetRecastGroupDetail(recastGroupTypeTwo);

            if (recastDetailTypeOne != null)
            {
                if (!recastDetailTypeOne->IsActive ||
                    (recastDetailTypeOne->Total - recastDetailTypeOne->Elapsed) * 1000 < RECAST_TIME_WINDOW)
                    return;
            }
            else if (recastDetailTypeTwo != null)
            {
                if (!recastDetailTypeTwo->IsActive ||
                    (recastDetailTypeTwo->Total - recastDetailTypeTwo->Elapsed) * 1000 < RECAST_TIME_WINDOW)
                    return;
            }

            if (manager->QueuedActionId != 0)
            {
                if (manager->QueuedActionType != ActionType.Action)
                    return;

                if (!LuminaGetter.TryGetRow(manager->QueuedActionId, out Action nextActionRow) ||
                    nextActionRow.Recast100ms == 0)
                    return;
            }

            EnqueueActionInsertion(insertionRule);
        }

        private static void OnCommandBuff
        (
            string command,
            string args
        )
        {
            if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent) return;
            EnqueueBuffSequence();
        }

        #endregion

        #region 工具

        private static unsafe bool IsActionInsertionReady
        (
            ActionInsertionRule insertionRule,
            IBattleChara        player,
            ActionManager*      manager
        ) =>
            insertionRule.EnabledCondition()                                  &&
            insertionRule.SupportJob.IsThisJob()                              &&
            insertionRule.SupportJob.IsActionUnlocked(insertionRule.ActionID) &&
            insertionRule.UseCondition(player)                                &&
            manager->IsActionOffCooldown(ActionType.Action, insertionRule.ActionID);

        private static unsafe bool ShouldUseOffensiveAria
        (
            IBattleChara player
        )
        {
            var statusManager = player.ToStruct()->StatusManager;
            var statusIndex   = statusManager.GetStatusIndex(STATUS_OFFENSIVE_ARIA);
            return statusIndex == -1 || 
                   statusManager.GetRemainingTime(statusIndex) <= OFFENSIVE_ARIA_REFRESH_THRESHOLD;
        }

        #endregion
        
        #region 常量

        private const string COMMAND_BUFF                     = "pbuff";
        private const uint   ACTION_FREELANCER_BUFF           = 46606;
        private const uint   ACTION_OFFENSIVE_ARIA            = 41608;
        private const uint   STATUS_OFFENSIVE_ARIA            = 4247;
        private const float  OFFENSIVE_ARIA_REFRESH_THRESHOLD = 20f;
        private const int    RECAST_TIME_WINDOW               = 500;
        private const int    ANIMATION_LOCK                   = 100;

        #endregion

        private sealed record ActionInsertionRule
        (
            CrescentSupportJob       SupportJob,
            uint                     ActionID,
            Func<bool>               EnabledCondition,
            Func<IBattleChara, bool> UseCondition
        );
    }
}
