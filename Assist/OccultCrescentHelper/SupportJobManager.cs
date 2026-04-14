using System.Numerics;
using System.Text;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using OmenTools.Threading;
using OmenTools.Threading.TaskHelper;
using TerritoryIntendedUse = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace DailyRoutines.ModulesPublic;

public partial class OccultCrescentHelper
{
    private class SupportJobManager
    (
        OccultCrescentHelper mainModule
    ) : BaseIslandModule(mainModule)
    {
        private const string COMMAND_SWITCH_JOB = "pjob";
        private const string COMMAND_BUFF       = "pbuff";

        private static TaskHelper? SupportJobTaskHelper;

        public override void Init()
        {
            SupportJobTaskHelper ??= new();

            CommandManager.Instance().AddSubCommand
            (
                COMMAND_SWITCH_JOB,
                new(OnCommandSwitchJob) { HelpMessage = $"{Lang.Get("OccultCrescentHelper-Command-PJob-Help")}" }
            );

            CommandManager.Instance().AddSubCommand
            (
                COMMAND_BUFF,
                new(OnCommandBuff) { HelpMessage = $"{Lang.Get("OccultCrescentHelper-Command-PBuff-Help")}" }
            );

            UseActionManager.Instance().RegPreUseAction(OnPreUseAction);
            UseActionManager.Instance().RegPreCharacterCompleteCast(OnCompleteCast);
        }

        public override void Uninit()
        {
            CommandManager.Instance().RemoveSubCommand(COMMAND_SWITCH_JOB);
            CommandManager.Instance().RemoveSubCommand(COMMAND_BUFF);

            SupportJobTaskHelper?.Abort();
            SupportJobTaskHelper?.Dispose();
            SupportJobTaskHelper = null;

            UseActionManager.Instance().Unreg(OnPreUseAction);
            UseActionManager.Instance().Unreg(OnCompleteCast);
        }

        public override void DrawConfig()
        {
            using var id = ImRaii.PushId("SupportJobManager");

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
            {
                ImGui.TextUnformatted($"/pdr {COMMAND_SWITCH_JOB} {Lang.Get("OccultCrescentHelper-Command-PJob-Help")}");

                var builder = new StringBuilder();
                builder.Append("ID:\n");
                foreach (var data in LuminaGetter.Get<MKDSupportJob>())
                    builder.Append($"\t{data.RowId} - {data.Name}\t{data.NameFemale}\t{data.NameEnglish}\n");
                ImGuiOm.HelpMarker(builder.ToString().TrimEnd('\n'), 100f * GlobalUIScale);

                ImGui.TextUnformatted($"/pdr {COMMAND_BUFF} {Lang.Get("OccultCrescentHelper-Command-PBuff-Help")}");
            }
        }

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
            // 狂战士自动面向
            if (MainModule.config.IsEnabledBerserkerRageAutoFace)
            {
                if (actionType != ActionType.Action || actionID != 41592) return;

                if (TargetManager.Target == null)
                    ChatManager.Instance().SendMessage("/tenemy");
                ChatManager.Instance().SendMessage("/facetarget");
            }
        }

        private void OnCompleteCast
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
            if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return;
            if (battleChara.Address != localPlayer.Address) return;

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

        private unsafe void OnCommandSwitchJob(string command, string args)
        {
            if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent)
            {
                RaptureLogModule.Instance()->ShowLogMessage(10970);
                return;
            }

            args = args.Trim().ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(args))
            {
                MainModule.othersModule.SupportJobChangeAddon.Toggle();
                return;
            }

            if (byte.TryParse(args, out var parsedJobID))
            {
                AgentMKDSupportJobList.Instance()->ChangeSupportJob(parsedJobID);
                return;
            }

            var matchingJob = LuminaGetter.Get<MKDSupportJob>()
                                          .Select
                                          (data => new
                                              {
                                                  Data        = data,
                                                  NameMale    = data.Name.ToString(),
                                                  NameFemale  = data.NameFemale.ToString(),
                                                  NameEnglish = data.NameEnglish.ToString()
                                              }
                                          )
                                          .Where
                                          (x => x.NameMale.Contains(args, StringComparison.OrdinalIgnoreCase)   ||
                                                x.NameFemale.Contains(args, StringComparison.OrdinalIgnoreCase) ||
                                                x.NameEnglish.Contains(args, StringComparison.OrdinalIgnoreCase)
                                          )
                                          .OrderBy(x => Math.Min(Math.Min(x.NameMale.Length, x.NameFemale.Length), x.NameEnglish.Length))
                                          .FirstOrDefault();
            if (matchingJob != null)
                AgentMKDSupportJobList.Instance()->ChangeSupportJob((byte)matchingJob.Data.RowId);
        }

        private static void OnCommandBuff(string command, string args)
        {
            if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent) return;
            ExecuteBuffSequence();
        }

        // TODO: 使用自由人的探求心技能
        private static void ExecuteBuffSequence()
        {
            if (!CrescentSupportJob.TryFindKnowledgeCrystal(out var gameObject) ||
                LocalPlayerState.DistanceToObject2DSquared(gameObject) > 10)
            {
                NotifyHelper.Instance().NotificationError(Lang.Get("OccultCrescentHelper-OthersManager-Notification-CrystalNotFound"));
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
    }
}
