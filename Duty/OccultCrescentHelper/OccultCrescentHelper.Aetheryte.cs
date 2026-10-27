using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using OmenTools.Dalamud;
using OmenTools.Info.Game;
using OmenTools.Info.Game.Enums;
using OmenTools.Info.Game.Packets.Upstream;
using OmenTools.Interop.Game.Lumina;
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
        private const string COMMAND_TP                = "ptp";
        private const float  USE_AETHERYTE_DISTANCE_SQ = 100f * 100f;

        private TaskHelper? moveTaskHelper;

        public override void Init()
        {
            moveTaskHelper ??= new() { TimeoutMS = 30_000 };

            IClientState.Instance().TerritoryChanged += OnZoneChanged;
            IClientState.Instance().Logout           += OnLogout;

            CommandManager.Instance().AddSubCommand
            (
                COMMAND_TP,
                new(OnCommandTP)
                {
                    HelpMessage = Lang.Get("OccultCrescentHelper-Command-PTP-Help")
                }
            );
        }

        public override void Uninit()
        {
            CommandManager.Instance().RemoveSubCommand(COMMAND_TP);

            IClientState.Instance().TerritoryChanged -= OnZoneChanged;
            IClientState.Instance().Logout           -= OnLogout;

            StopPathfinding();
            moveTaskHelper?.Dispose();
            moveTaskHelper = null;
        }

        public override void DrawConfig()
        {
            if (GameState.TerritoryIntendedUse == TerritoryIntendedUse.OccultCrescent)
            {
                ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Teleport"));

                var longestName = string.Empty;

                var aetherytes = GameState.TerritoryType == 1252 ?
                                     CrescentAetheryte.SouthHornAetherytes :
                                     CrescentAetheryte.NorthHornAetherytes;

                foreach (var aetheryte in aetherytes)
                {
                    if (aetheryte.Name.Length <= longestName.Length) continue;
                    longestName = aetheryte.Name;
                }

                var buttonSize = new Vector2(ImGui.CalcTextSize(longestName).X * 2, ImGui.GetTextLineHeightWithSpacing());

                using (ImRaii.Disabled(GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent))
                using (ImRaii.PushIndent())
                {
                    foreach (var aetheryte in aetherytes)
                    {
                        if (ImGui.Button(aetheryte.Name, buttonSize))
                            TryUseAetheryte(aetheryte);
                    }
                }

                ImGui.NewLine();
            }

            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Command"));

            using (ImRaii.PushIndent())
                ImGui.TextWrapped($"/pdr {COMMAND_TP} {Lang.Get("OccultCrescentHelper-Command-PTP-Help")}");
        }

        private void OnLogout
        (
            int type,
            int code
        ) =>
            StopPathfinding();

        private void OnZoneChanged
        (
            uint u
        ) =>
            StopPathfinding();

        public void StopPathfinding()
        {
            moveTaskHelper?.Abort();
            vnavmeshIPC.StopPathfind();
        }

        private void OnCommandTP
        (
            string command,
            string args
        )
        {
            if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent) return;

            args = args.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(args)) return;

            var source = GameState.TerritoryType == SOUTH_HORN_TERRITORY_ID ?
                             CrescentAetheryte.SouthHornAetherytes :
                             CrescentAetheryte.NorthHornAetherytes;

            CrescentAetheryte? aetheryte = null;

            if (byte.TryParse(args, out var parsedIndex))
            {
                try
                {
                    aetheryte = source[parsedIndex];
                }
                catch
                {
                    // ignored
                }
            }
            else
            {
                aetheryte = source
                            .Where(x => x.Name.Contains(args, StringComparison.OrdinalIgnoreCase))
                            .OrderBy(x => x.Name)
                            .FirstOrDefault();
            }

            if (aetheryte == null) return;

            TryUseAetheryte(aetheryte);
        }

        public unsafe bool TryUseAetheryte
        (
            CrescentAetheryte aetheryte
        )
        {
            if (aetheryte == null                                            ||
                moveTaskHelper is not { } taskHelper                         ||
                IObjectTable.Instance().LocalPlayer is not { } localPlayer)
                return false;

            StopPathfinding();
            ChatManager.Instance().SendMessage("/automove off");
            if (ICondition.Instance()[ConditionFlag.Mounted])
                ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.Dismount);

            // 以太之光传送走了
            if (aetheryte.TeleportTo()) return true;

            // 附近可以找到魔路
            if (EventFramework.Instance()->TryGetNearestEvent
                (
                    x => x.EventId.ContentId == EventHandlerContent.CustomTalk,
                    x => x.NameString.Equals(LuminaWrapper.GetEObjName(2006473), StringComparison.OrdinalIgnoreCase) ||
                         x.NameString.Equals(LuminaWrapper.GetEObjName(2014664), StringComparison.OrdinalIgnoreCase),
                    localPlayer.Position,
                    out var eventID,
                    out var eventObjectID
                ) &&
                IObjectTable.Instance().SearchByID(eventObjectID) is { } targetObj)
            {
                var distanceSQ = LocalPlayerState.DistanceTo3DSquared(targetObj.Position);

                // 可以直接交互, 不管怎么样直接交互
                if (distanceSQ <= 16f)
                {
                    taskHelper.Enqueue
                    (() =>
                        {
                            if (ICondition.Instance()[ConditionFlag.Mounted]) return false;

                            new EventStartPackt(eventObjectID, eventID).Send();
                            new EventCompletePackt(721820, 16777216, aetheryte.DataID).Send();
                            return true;
                        }
                    );

                    return true;
                }

                // 启用了绿玩移动
                if (IDalamudPluginInterface.Instance().IsPluginEnabled(vnavmeshIPC.INTERNAL_NAME) &&
                    distanceSQ <= USE_AETHERYTE_DISTANCE_SQ)
                {
                    taskHelper.Enqueue
                    (() =>
                        {
                            // 已经在坐骑上
                            if (ICondition.Instance()[ConditionFlag.Mounted]) return true;

                            if (distanceSQ <= 30f * 30f)
                            {
                                // 用一下冲刺
                                taskHelper.Enqueue
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

                    taskHelper.Enqueue
                    (() =>
                        {
                            if (!Throttler.Shared.Throttle("OccultCrescentHelper-AetheryteManager-MoveTo")) return false;
                            if (vnavmeshIPC.GetIsPathfindRunning() || vnavmeshIPC.GetIsPathfindInProgress()) return true;

                            vnavmeshIPC.PathfindAndMoveToClosely(targetObj.Position, false, 0.1f);
                            return false;
                        }
                    );

                    taskHelper.Enqueue
                    (() =>
                        {
                            if (vnavmeshIPC.GetIsPathfindRunning() || vnavmeshIPC.GetIsPathfindInProgress())
                                return false;

                            if (LocalPlayerState.DistanceTo3DSquared(targetObj.Position) > 4f * 4f)
                            {
                                if (Throttler.Shared.Throttle("OccultCrescentHelper-AetheryteManager-MoveTo-Retry"))
                                    vnavmeshIPC.PathfindAndMoveToClosely(targetObj.Position, false, 0.1f);

                                return false;
                            }

                            vnavmeshIPC.StopPathfind();

                            if (!ICondition.Instance()[ConditionFlag.Mounted]) return true;
                            if (!Throttler.Shared.Throttle("OccultCrescentHelper-AetheryteManager-Dismount"))
                                return false;

                            ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.Dismount);
                            return false;
                        }
                    );

                    taskHelper.Enqueue
                    (() =>
                        {
                            if (ICondition.Instance()[ConditionFlag.Mounted]) return false;

                            new EventStartPackt(eventObjectID, eventID).Send();
                            new EventCompletePackt(721820, 16777216, aetheryte.DataID).Send();
                            return true;
                        }
                    );

                    taskHelper.Enqueue(() => LocalPlayerState.DistanceTo3DSquared(aetheryte.Position) <= 30f * 30f);
                    return true;
                }
            }

            return false;
        }
    }
}
