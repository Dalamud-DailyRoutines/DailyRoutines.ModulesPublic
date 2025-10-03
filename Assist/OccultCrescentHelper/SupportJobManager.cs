using System;
using System.Linq;
using System.Numerics;
using System.Text;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public partial class OccultCrescentHelper
{
    public class SupportJobManager(OccultCrescentHelper mainModule) : BaseIslandModule(mainModule)
    {
        private const string CommandSwitchJob = "pjob";
        private const string CommandBuff      = "pbuff";

        private static TaskHelper? SupportJobTaskHelper;
        
        public override void Init()
        {
            SupportJobTaskHelper ??= new();
            
            CommandManager.AddSubCommand(CommandSwitchJob,
                                         new(OnCommandSwitchJob) { HelpMessage = $"{GetLoc("OccultCrescentHelper-Command-PJob-Help")}" });

            CommandManager.AddSubCommand(CommandBuff,
                                         new(OnCommandBuff) { HelpMessage = $"{GetLoc("OccultCrescentHelper-Command-PBuff-Help")}" });
            
            UseActionManager.RegPreUseAction(OnPreUseAction);
            UseActionManager.RegPreCharacterCompleteCast(OnCompleteCast);
        }
        
        public override void Uninit()
        {
            CommandManager.RemoveSubCommand(CommandSwitchJob);
            CommandManager.RemoveSubCommand(CommandBuff);
            
            SupportJobTaskHelper?.Abort();
            SupportJobTaskHelper?.Dispose();
            SupportJobTaskHelper = null;
            
            UseActionManager.Unreg(OnPreUseAction);
            UseActionManager.Unreg(OnCompleteCast);
        }

        public override void DrawConfig()
        {
            using var id = ImRaii.PushId("SupportJobManager");

            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), LuminaWrapper.GetMKDSupportJobName(3));

            using (ImRaii.PushIndent())
            {
                if (ImGui.Checkbox($"{GetLoc("OccultCrescentHelper-SupportJobManager-Monk-PhantomKickNoMove")}##NoMoveMonk",
                                   ref ModuleConfig.IsEnabledMonkKickNoMove))
                    ModuleConfig.Save(MainModule);
                ImGuiOm.HelpMarker(GetLoc("OccultCrescentHelper-SupportJobManager-Monk-PhantomKickNoMove-Help"), 20f * GlobalFontScale);
            }

            ImGui.NewLine();

            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), LuminaWrapper.GetMKDSupportJobName(2));

            using (ImRaii.PushIndent())
            {
                if (ImGui.Checkbox($"{GetLoc("OccultCrescentHelper-SupportJobManager-Berserker-RageAutoFace")}##BerserkerRageAutoFace",
                                   ref ModuleConfig.IsEnabledBerserkerRageAutoFace))
                    ModuleConfig.Save(MainModule);
                ImGuiOm.HelpMarker(GetLoc("OccultCrescentHelper-SupportJobManager-Berserker-RageAutoFace-Help"), 20f * GlobalFontScale);

                if (ImGui.Checkbox($"{GetLoc("OccultCrescentHelper-SupportJobManager-Berserker-RageReplace")}##BerserkerRageReplace",
                                   ref ModuleConfig.IsEnabledBerserkerRageReplace))
                    ModuleConfig.Save(MainModule);
                ImGuiOm.HelpMarker(GetLoc("OccultCrescentHelper-SupportJobManager-Berserker-RageReplace-Help"), 20f * GlobalFontScale);
            }
            
            ImGui.NewLine();
            
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("Command"));

            using (ImRaii.PushIndent())
            {
                ImGui.Text($"/pdr {CommandSwitchJob} {GetLoc("OccultCrescentHelper-Command-PJob-Help")}");

                var builder = new StringBuilder();
                builder.Append("ID:\n");
                foreach (var data in LuminaGetter.Get<MKDSupportJob>())
                    builder.Append($"\t{data.RowId} - {data.Unknown0}\t{data.Unknown1}\t{data.Unknown4}\n");
                ImGuiOm.HelpMarker(builder.ToString().TrimEnd('\n'), 100f * GlobalFontScale);

                ImGui.Text($"/pdr {CommandBuff} {GetLoc("OccultCrescentHelper-Command-PBuff-Help")}");
            }
        }

        private static void OnPreUseAction(
            ref bool                        isPrevented,
            ref ActionType                  actionType,
            ref uint                        actionID,
            ref ulong                       targetID,
            ref uint                        extraParam,
            ref ActionManager.UseActionMode queueState,
            ref uint                        comboRouteID)
        {
            // 狂战士自动面向
            if (ModuleConfig.IsEnabledBerserkerRageAutoFace)
            {
                if (actionType != ActionType.Action || actionID != 41592) return;

                if (DService.Targets.Target == null)
                    ChatHelper.SendMessage("/tenemy");
                ChatHelper.SendMessage("/facetarget");
            }
        }
        
        private static void OnCompleteCast(
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
            ref int          ballistaEntityID)
        {
            if (DService.ObjectTable.LocalPlayer is not { } localPlayer) return;
            if (battleChara.Address != localPlayer.Address) return;

            // 武僧无位移
            if (ModuleConfig.IsEnabledMonkKickNoMove)
            {
                if (actionType == ActionType.Action && actionID == 41595)
                    actionID = spellID = 7;
            }

            // 狂怒攻击替换
            if (ModuleConfig.IsEnabledBerserkerRageReplace)
            {
                if (actionType == ActionType.Action && actionID == 41593)
                    actionID = spellID = 3549;
            }
        }
        
        private static unsafe void OnCommandSwitchJob(string command, string args)
        {
            if (GameState.TerritoryIntendedUse != 61)
            {
                RaptureLogModule.Instance()->ShowLogMessage(10970);
                return;
            }

            args = args.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(args))
            {
                OthersManager.SupportJobChangeAddon.Toggle();
                return;
            }

            if (byte.TryParse(args, out var parsedJobID))
            {
                AgentMKDSupportJobList.Instance()->ChangeSupportJob(parsedJobID);
                return;
            }

            var matchingJob = LuminaGetter.Get<MKDSupportJob>()
                                          .Select(data => new
                                          {
                                              Data        = data,
                                              NameMale    = data.Unknown0.ExtractText(),
                                              NameFemale  = data.Unknown1.ExtractText(),
                                              NameEnglish = data.Unknown4.ExtractText()
                                          })
                                          .Where(x => x.NameMale.Contains(args, StringComparison.OrdinalIgnoreCase)   ||
                                                      x.NameFemale.Contains(args, StringComparison.OrdinalIgnoreCase) ||
                                                      x.NameEnglish.Contains(args, StringComparison.OrdinalIgnoreCase))
                                          .OrderBy(x => Math.Min(Math.Min(x.NameMale.Length, x.NameFemale.Length), x.NameEnglish.Length))
                                          .FirstOrDefault();
            if (matchingJob != null)
                AgentMKDSupportJobList.Instance()->ChangeSupportJob((byte)matchingJob.Data.RowId);
        }

        private static void OnCommandBuff(string command, string args)
        {
            if (GameState.TerritoryIntendedUse != 61) return;
            ExecuteBuffSequence();
        }

        private static void ExecuteBuffSequence()
        {
            if (!CrescentSupportJob.TryFindKnowledgeCrystal(out _))
            {
                NotificationError(GetLoc("OccultCrescentHelper-OthersManager-Notification-CrystalNotFound"));
                return;
            }

            var currentJob = CrescentSupportJob.GetCurrentSupportJob();

            var allJobs = CrescentSupportJob.AllJobs
                                            .Where(x => x.IsLongTimeStatusUnlocked())
                                            .OrderBy(x => x.JobType switch
                                            {
                                                CrescentSupportJobType.Knight => 0,
                                                CrescentSupportJobType.Bard   => 1,
                                                CrescentSupportJobType.Monk   => 3,
                                                _                             => 999
                                            })
                                            .ToList();
            allJobs.ForEach(x => StatusManager.ExecuteStatusOff(x.LongTimeStatusID));

            SupportJobTaskHelper.Abort();
            SupportJobTaskHelper.Enqueue(() =>
            {
                if (!DService.Condition[ConditionFlag.Mounted]) return true;

                ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.Dismount);
                return true;
            });

            foreach (var sJob in allJobs)
            {
                SupportJobTaskHelper.Enqueue(() =>
                {
                    if (sJob.IsThisJob()) return true;
                    if (!Throttler.Throttle("OthersManager-OthersManager-ChangeSupportJob", 750)) return false;

                    sJob.ChangeTo();
                    return false;
                });
                SupportJobTaskHelper.Enqueue(() =>
                {
                    if (sJob.IsWithLongTimeStatus()) return true;

                    UseActionManager.UseAction(ActionType.Action, sJob.LongTimeStatusActionID);
                    return false;
                });
            }

            SupportJobTaskHelper.Enqueue(() =>
            {
                if (currentJob.IsThisJob()) return true;
                if (!Throttler.Throttle("OthersManager-OthersManager-ChangeSupportJob", 750)) return false;

                currentJob.ChangeTo();
                return false;
            });
        }
    }
}
