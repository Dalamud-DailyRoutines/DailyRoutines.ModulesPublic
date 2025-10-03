using System;
using System.Linq;
using System.Numerics;
using DailyRoutines.Helpers;
using DailyRoutines.Infos;
using DailyRoutines.IPC;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;

namespace DailyRoutines.ModulesPublic;

public partial class OccultCrescentHelper 
{
    public class AetheryteManager(OccultCrescentHelper mainModule) : BaseIslandModule(mainModule)
    {
        public static bool IsTaskHelperBusy => MoveTaskHelper?.IsBusy ?? false;

        private const string CommandTP = "ptp";

        private static TaskHelper? MoveTaskHelper;

        public override void Init()
        {
            MoveTaskHelper ??= new() { TimeLimitMS = 30_000 };

            DService.ClientState.TerritoryChanged += OnZoneChanged;
            DService.ClientState.Logout           += OnLogout;

            CommandManager.AddSubCommand(CommandTP, new(OnCommandTP) {HelpMessage = GetLoc("OccultCrescentHelper-Command-PTP-Help")});
        }

        public override void Uninit()
        {
            CommandManager.RemoveSubCommand(CommandTP);
            
            DService.ClientState.TerritoryChanged -= OnZoneChanged;
            DService.ClientState.Logout           -= OnLogout;

            MoveTaskHelper?.Abort();
            MoveTaskHelper?.Dispose();
            MoveTaskHelper = null;

            vnavmeshIPC.PathStop();
        }
        
        public override void DrawConfig()
        {
            using var id = ImRaii.PushId("AetheryteManager");

            using (FontManager.UIFont.Push())
            {
                ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("OccultCrescentHelper-FastTeleport"));

                ImGui.SameLine(0, 8f * GlobalFontScale);
                if (ImGui.SmallButton($"{GetLoc("Stop")}##StopAetheryte"))
                {
                    MoveTaskHelper.Abort();
                    vnavmeshIPC.PathStop();
                }

                var longestName = string.Empty;
                foreach (var aetheryte in CrescentAetheryte.SouthHornAetherytes)
                {
                    if (aetheryte.Name.Length <= longestName.Length) continue;
                    longestName = aetheryte.Name;
                }

                var buttonSize = new Vector2(ImGui.CalcTextSize(longestName).X * 2, ImGui.GetTextLineHeightWithSpacing());
                using (ImRaii.Disabled(GameState.TerritoryIntendedUse != 61))
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

            if (ImGui.Checkbox($"{GetLoc("OccultCrescentHelper-PrioritizeMoveTo")}", ref ModuleConfig.IsEnabledMoveToAetheryte))
                ModuleConfig.Save(MainModule);
            ImGuiOm.HelpMarker(GetLoc("OccultCrescentHelper-AetheryteManager-PrioritizeMoveTo-Help"), 20f * GlobalFontScale);

            if (ModuleConfig.IsEnabledMoveToAetheryte)
            {
                ImGui.SetNextItemWidth(150f * GlobalFontScale);
                ImGui.SliderFloat($"{GetLoc("OccultCrescentHelper-DistanceTo")}", ref ModuleConfig.DistanceToMoveToAetheryte, 1f, 100f, "%.1f");
                if (ImGui.IsItemDeactivatedAfterEdit())
                    ModuleConfig.Save(MainModule);
                ImGuiOm.HelpMarker($"{GetLoc("OccultCrescentHelper-AetheryteManager-PrioritizeMoveTo-DistanceTo-Help")}", 20f * GlobalFontScale);
            }
            
            ImGui.NewLine();
            
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("Command"));

            using (ImRaii.PushIndent())
                ImGui.Text($"/pdr {CommandTP} {GetLoc("OccultCrescentHelper-Command-PTP-Help")}");
        }

        private static void OnLogout(int type, int code)
        {
            MoveTaskHelper?.Abort();
            vnavmeshIPC.PathStop();
        }

        private static void OnZoneChanged(ushort obj)
        {
            MoveTaskHelper?.Abort();
            vnavmeshIPC.PathStop();
        }
        
        private static void OnCommandTP(string command, string args)
        {
            if (GameState.TerritoryIntendedUse != 61) return;

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

        public static unsafe void UseAetheryte(CrescentAetheryte aetheryte)
        {
            if (aetheryte == null) return;

            ChatHelper.SendMessage("/automove off");
            if (DService.Condition[ConditionFlag.Mounted])
                ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.Dismount);

            // 以太之光传送走了
            if (aetheryte.TeleportTo()) return;

            // 附近可以找到魔路
            if (EventFrameworkHelper.TryGetNearestEvent(x => x.EventId.ContentId == EventHandlerContent.CustomTalk,
                                                        x => x.NameString.Equals(LuminaWrapper.GetEObjName(2006473), StringComparison.OrdinalIgnoreCase) ||
                                                             x.NameString.Equals(LuminaWrapper.GetEObjName(2014664), StringComparison.OrdinalIgnoreCase),
                                                        default,
                                                        out var eventID,
                                                        out var eventObjectID) &&
                DService.ObjectTable.SearchById(eventObjectID) is { } targetObj)
            {
                var distance3D = LocalPlayerState.DistanceTo3D(targetObj.Position);

                // 可以直接交互, 不管怎么样直接交互
                if (distance3D <= 4f)
                {
                    MoveTaskHelper.Abort();

                    MoveTaskHelper.Enqueue(() =>
                    {
                        if (DService.Condition[ConditionFlag.Mounted]) return false;

                        new EventStartPackt(eventObjectID, eventID).Send();
                        new EventCompletePackt(721820, 16777216, aetheryte.DataID).Send();
                        return true;
                    });

                    return;
                }

                // 启用了绿玩移动
                if (ModuleConfig.IsEnabledMoveToAetheryte    &&
                    IPCManager.IsIPCAvailable<vnavmeshIPC>() &&
                    distance3D <= ModuleConfig.DistanceToMoveToAetheryte)
                {
                    MoveTaskHelper.Abort();

                    MoveTaskHelper.Enqueue(() =>
                    {
                        // 已经在坐骑上
                        if (DService.Condition[ConditionFlag.Mounted]) return true;
                        if (distance3D <= 30)
                        {
                            // 用一下冲刺
                            MoveTaskHelper.Enqueue(() =>
                            {
                                if (!ActionManager.Instance()->IsActionOffCooldown(ActionType.Action, 3) ||
                                    LocalPlayerState.HasStatus(50, out _)) return true;

                                return UseActionManager.UseActionLocationCallDetour(ActionType.Action, 3);
                            }, weight: 1);

                            return true;
                        }

                        return UseActionManager.UseAction(ActionType.GeneralAction, 9);
                    });

                    MoveTaskHelper.Enqueue(() =>
                    {
                        if (!Throttler.Throttle("OccultCrescentHelper-AetheryteManager-MoveTo")) return false;
                        if (vnavmeshIPC.PathIsRunning()) return true;

                        vnavmeshIPC.PathfindAndMoveTo(targetObj.Position, false);
                        return false;
                    });

                    MoveTaskHelper.Enqueue(() =>
                    {
                        // 可以稍微放宽一点
                        if (LocalPlayerState.DistanceTo3D(targetObj.Position) <= 4f || !vnavmeshIPC.PathIsRunning())
                        {
                            ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.Dismount);
                            vnavmeshIPC.PathStop();
                            return true;
                        }

                        return false;
                    });

                    MoveTaskHelper.Enqueue(() =>
                    {
                        if (DService.Condition[ConditionFlag.Mounted]) return false;

                        new EventStartPackt(eventObjectID, eventID).Send();
                        new EventCompletePackt(721820, 16777216, aetheryte.DataID).Send();
                        return true;
                    });

                    MoveTaskHelper.Enqueue(() => LocalPlayerState.DistanceTo3D(aetheryte.Position) <= 30);
                    return;
                }
            }

            // 先回去 然后重复一次这个流程
            if (ModuleConfig.IsEnabledMoveToAetheryte &&
                IPCManager.IsIPCAvailable<vnavmeshIPC>())
            {
                MoveTaskHelper.Enqueue(() => UseActionManager.UseActionLocation(ActionType.Action, 41343));
                MoveTaskHelper.Enqueue(() => IsScreenReady() && LocalPlayerState.DistanceTo3D(CrescentAetheryte.ExpeditionBaseCamp.Position) <= 100);
                MoveTaskHelper.Enqueue(() => UseAetheryte(aetheryte));

                return;
            }

            TP(aetheryte.Position, MoveTaskHelper);
        }
    }
}
