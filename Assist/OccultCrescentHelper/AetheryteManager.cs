using System.Numerics;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.Dalamud;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.Interop.Game.Models.Packets.Upstream;
using OmenTools.OmenService;
using OmenTools.Threading;
using OmenTools.Threading.TaskHelper;

namespace DailyRoutines.ModulesPublic;

public partial class OccultCrescentHelper
{
    private class AetheryteManager
    (
        OccultCrescentHelper mainModule
    ) : BaseIslandModule(mainModule)
    {
        private const string COMMAND_TP = "ptp";

        private TaskHelper? moveTaskHelper;
        public  bool        IsTaskHelperBusy => moveTaskHelper?.IsBusy ?? false;

        public override void Init()
        {
            moveTaskHelper ??= new() { TimeoutMS = 30_000 };

            DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
            DService.Instance().ClientState.Logout           += OnLogout;

            CommandManager.Instance().AddSubCommand(COMMAND_TP, new(OnCommandTP) { HelpMessage = Lang.Get("OccultCrescentHelper-Command-PTP-Help") });
        }

        public override void Uninit()
        {
            CommandManager.Instance().RemoveSubCommand(COMMAND_TP);

            DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
            DService.Instance().ClientState.Logout           -= OnLogout;

            moveTaskHelper?.Abort();
            moveTaskHelper?.Dispose();
            moveTaskHelper = null;

            vnavmeshIPC.StopPathfind();
        }

        public override void DrawConfig()
        {
            using var id = ImRaii.PushId("AetheryteManager");

            using (FontManager.Instance().UIFont.Push())
            {
                ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("OccultCrescentHelper-FastTeleport"));

                ImGui.SameLine(0, 8f * GlobalUIScale);

                if (ImGui.SmallButton($"{Lang.Get("Stop")}##StopAetheryte"))
                {
                    moveTaskHelper.Abort();
                    vnavmeshIPC.StopPathfind();
                }

                var longestName = string.Empty;

                foreach (var aetheryte in CrescentAetheryte.SouthHornAetherytes)
                {
                    if (aetheryte.Name.Length <= longestName.Length) continue;
                    longestName = aetheryte.Name;
                }

                var buttonSize = new Vector2(ImGui.CalcTextSize(longestName).X * 2, ImGui.GetTextLineHeightWithSpacing());

                using (ImRaii.Disabled(GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent))
                using (ImRaii.PushIndent())
                {
                    foreach (var aetheryte in CrescentAetheryte.SouthHornAetherytes)
                    {
                        if (ImGui.Button(aetheryte.Name, buttonSize))
                            UseAetheryte(aetheryte);
                    }
                }
            }

            ImGui.NewLine();

            if (ImGui.Checkbox($"{Lang.Get("OccultCrescentHelper-PrioritizeMoveTo")}", ref MainModule.config.IsEnabledMoveToAetheryte))
                MainModule.config.Save(MainModule);
            ImGuiOm.HelpMarker(Lang.Get("OccultCrescentHelper-AetheryteManager-PrioritizeMoveTo-Help"), 20f * GlobalUIScale);

            if (MainModule.config.IsEnabledMoveToAetheryte)
            {
                ImGui.SetNextItemWidth(150f * GlobalUIScale);
                ImGui.SliderFloat($"{Lang.Get("OccultCrescentHelper-DistanceTo")}", ref MainModule.config.DistanceToMoveToAetheryte, 1f, 100f, "%.1f");
                if (ImGui.IsItemDeactivatedAfterEdit())
                    MainModule.config.Save(MainModule);
                ImGuiOm.HelpMarker($"{Lang.Get("OccultCrescentHelper-AetheryteManager-PrioritizeMoveTo-DistanceTo-Help")}", 20f * GlobalUIScale);
            }

            ImGui.NewLine();

            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Command"));

            using (ImRaii.PushIndent())
                ImGui.TextUnformatted($"/pdr {COMMAND_TP} {Lang.Get("OccultCrescentHelper-Command-PTP-Help")}");
        }

        private void OnLogout(int type, int code)
        {
            moveTaskHelper?.Abort();
            vnavmeshIPC.StopPathfind();
        }

        private void OnZoneChanged(uint u)
        {
            moveTaskHelper?.Abort();
            vnavmeshIPC.StopPathfind();
        }

        private void OnCommandTP(string command, string args)
        {
            if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent) return;

            args = args.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(args)) return;

            CrescentAetheryte? aetheryte;

            if (byte.TryParse(args, out var parsedIndex))
                aetheryte = CrescentAetheryte.SouthHornAetherytes[parsedIndex];
            else
            {
                aetheryte = CrescentAetheryte.SouthHornAetherytes
                                             .Where(x => x.Name.Contains(args, StringComparison.OrdinalIgnoreCase))
                                             .OrderBy(x => x.Name)
                                             .FirstOrDefault();
            }

            if (aetheryte == null) return;

            UseAetheryte(aetheryte);
        }

        public unsafe void UseAetheryte(CrescentAetheryte aetheryte)
        {
            if (aetheryte == null) return;

            ChatManager.Instance().SendMessage("/automove off");
            if (DService.Instance().Condition[ConditionFlag.Mounted])
                ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.Dismount);

            // 以太之光传送走了
            if (aetheryte.TeleportTo()) return;

            // 附近可以找到魔路
            if (EventFramework.Instance()->TryGetNearestEvent
                (
                    x => x.EventId.ContentId == EventHandlerContent.CustomTalk,
                    x => x.NameString.Equals(LuminaWrapper.GetEObjName(2006473), StringComparison.OrdinalIgnoreCase) ||
                         x.NameString.Equals(LuminaWrapper.GetEObjName(2014664), StringComparison.OrdinalIgnoreCase),
                    default,
                    out var eventID,
                    out var eventObjectID
                ) &&
                DService.Instance().ObjectTable.SearchByID(eventObjectID) is { } targetObj)
            {
                var distance3D = LocalPlayerState.DistanceTo3D(targetObj.Position);

                // 可以直接交互, 不管怎么样直接交互
                if (distance3D <= 4f)
                {
                    moveTaskHelper.Abort();

                    moveTaskHelper.Enqueue
                    (() =>
                        {
                            if (DService.Instance().Condition[ConditionFlag.Mounted]) return false;

                            new EventStartPackt(eventObjectID, eventID).Send();
                            new EventCompletePackt(721820, 16777216, aetheryte.DataID).Send();
                            return true;
                        }
                    );

                    return;
                }

                // 启用了绿玩移动
                if (MainModule.config.IsEnabledMoveToAetheryte                            &&
                    DService.Instance().PI.IsPluginEnabled(vnavmeshIPC.INTERNAL_NAME) &&
                    distance3D <= MainModule.config.DistanceToMoveToAetheryte)
                {
                    moveTaskHelper.Abort();

                    moveTaskHelper.Enqueue
                    (() =>
                        {
                            // 已经在坐骑上
                            if (DService.Instance().Condition[ConditionFlag.Mounted]) return true;

                            if (distance3D <= 30)
                            {
                                // 用一下冲刺
                                moveTaskHelper.Enqueue
                                (
                                    () =>
                                    {
                                        if (!ActionManager.Instance()->IsActionOffCooldown(ActionType.Action, 3) ||
                                            LocalPlayerState.HasStatus(50, out _)) return true;

                                        return ActionManager.Instance()->UseAction(ActionType.Action, 3);
                                    },
                                    weight: 1
                                );

                                return true;
                            }

                            return UseActionManager.Instance().UseAction(ActionType.GeneralAction, 9);
                        }
                    );

                    moveTaskHelper.Enqueue
                    (() =>
                        {
                            if (!Throttler.Shared.Throttle("OccultCrescentHelper-AetheryteManager-MoveTo")) return false;
                            if (vnavmeshIPC.GetIsPathfindRunning()) return true;

                            vnavmeshIPC.PathfindAndMoveTo(targetObj.Position, false);
                            return false;
                        }
                    );

                    moveTaskHelper.Enqueue
                    (() =>
                        {
                            // 可以稍微放宽一点
                            if (LocalPlayerState.DistanceTo3D(targetObj.Position) <= 4f || !vnavmeshIPC.GetIsPathfindRunning())
                            {
                                ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.Dismount);
                                vnavmeshIPC.StopPathfind();
                                return true;
                            }

                            return false;
                        }
                    );

                    moveTaskHelper.Enqueue
                    (() =>
                        {
                            if (DService.Instance().Condition[ConditionFlag.Mounted]) return false;

                            new EventStartPackt(eventObjectID, eventID).Send();
                            new EventCompletePackt(721820, 16777216, aetheryte.DataID).Send();
                            return true;
                        }
                    );

                    moveTaskHelper.Enqueue(() => LocalPlayerState.DistanceTo3D(aetheryte.Position) <= 30);
                    return;
                }
            }

            // 先回去 然后重复一次这个流程
            if (MainModule.config.IsEnabledMoveToAetheryte &&
                DService.Instance().PI.IsPluginEnabled(vnavmeshIPC.INTERNAL_NAME))
            {
                moveTaskHelper.Enqueue(() => UseActionManager.Instance().UseActionLocation(ActionType.Action, 41343));
                moveTaskHelper.Enqueue(() => UIModule.IsScreenReady() && LocalPlayerState.DistanceTo3D(CrescentAetheryte.ExpeditionBaseCamp.Position) <= 100);
                moveTaskHelper.Enqueue(() => UseAetheryte(aetheryte));

                return;
            }

            TP(aetheryte.Position, moveTaskHelper);
        }
    }
}
