using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.Helpers;

namespace DailyRoutines.ModulesPublic;

public partial class OccultCrescentHelper
{
    public unsafe class TreasureManager(OccultCrescentHelper mainModule) : BaseIslandModule(mainModule)
    {
        private const ImGuiWindowFlags WindowFlags = ImGuiWindowFlags.NoScrollbar           |
                                                     ImGuiWindowFlags.AlwaysAutoResize      |
                                                     ImGuiWindowFlags.NoTitleBar            |
                                                     ImGuiWindowFlags.NoBackground          |
                                                     ImGuiWindowFlags.NoBringToFrontOnFocus |
                                                     ImGuiWindowFlags.NoFocusOnAppearing    |
                                                     ImGuiWindowFlags.NoNavFocus            |
                                                     ImGuiWindowFlags.NoDocking             |
                                                     ImGuiWindowFlags.NoMove                |
                                                     ImGuiWindowFlags.NoResize              |
                                                     ImGuiWindowFlags.NoScrollWithMouse     |
                                                     ImGuiWindowFlags.NoInputs;
        
        private static readonly uint LineColorBlue = KnownColor.CadetBlue.ToVector4().ToUInt();
        private static readonly uint DotColor      = KnownColor.IndianRed.ToVector4().ToUInt();
        private static readonly uint PlayerColor   = KnownColor.Orange.ToVector4().ToUInt();

        private const string CommandTreasure = "ptreasure";
        
        private static TaskHelper? TreasureTaskHelper;
        
        private static Queue<TreasureHuntPoint> QueuedGatheringList = [];
        private static List<TreasureData> Treasures = [];

        private static Vector3 OriginalPosition;
        
        private static List<TreasureHuntPoint> CurrentRoute = [];

        public override void Init()
        {
            TreasureTaskHelper ??= new() { TimeLimitMS = 180_000 };
            
            DService.UiBuilder.Draw               += OnPosDraw;
            DService.ClientState.TerritoryChanged += OnZoneChanged;
            
            GamePacketManager.RegPreSendPacket(OnPreSendPacket);
            
            CommandManager.AddSubCommand(CommandTreasure,
                                         new(OnCommandTreasure) { HelpMessage = $"{GetLoc("OccultCrescentHelper-Command-PTreasure-Help")}" });
        }
        
        public override void DrawConfig()
        {
            using var id = ImRaii.PushId("TreasureManager");
            
            if (ImGui.Checkbox(GetLoc("OccultCrescentHelper-TreasureManager-AutoOpenTreasure"), ref ModuleConfig.IsEnabledAutoOpenTreasure))
                ModuleConfig.Save(MainModule);
            ImGuiOm.HelpMarker(GetLoc("OccultCrescentHelper-TreasureManager-AutoOpenTreasure-Help"), 20f * GlobalFontScale);

            if (ModuleConfig.IsEnabledAutoOpenTreasure)
            {
                ImGui.SetNextItemWidth(150f * GlobalFontScale);
                ImGui.SliderFloat($"{GetLoc("OccultCrescentHelper-DistanceTo")}", 
                                  ref ModuleConfig.DistanceToAutoOpenTreasure, 1.0f, 50f, "%.1f");
                if (ImGui.IsItemDeactivatedAfterEdit())
                    ModuleConfig.Save(MainModule);
                ImGuiOm.HelpMarker($"{GetLoc("OccultCrescentHelper-TreasureManager-AutoOpenTreasure-DistanceTo-Help")}", 20f * GlobalFontScale);
            }
            
            ImGui.NewLine();

            using (FontManager.UIFont.Push())
            {
                ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("OccultCrescentHelper-TreasureManager-AutoHuntTresures"));
                ImGuiOm.HelpMarker(GetLoc("OccultCrescentHelper-TreasureManager-AutoHuntTresures-Help"), 20f * GlobalFontScale);

                using (ImRaii.Disabled(GameState.TerritoryIntendedUse != 61))
                using (ImRaii.PushIndent())
                {
                    ImGui.Text($"{GetLoc("OccultCrescentHelper-TreasureManager-AutoHuntTresures-LeftPoints")}: {QueuedGatheringList.Count}");

                    var isFirst = true;
                    foreach (var (routeName, routeData) in Routes)
                    {
                        if (!isFirst)
                            ImGui.SameLine();
                        isFirst = false;
                        
                        if (ImGui.Button(routeName))
                            EnqueueAutoTreasureHunt(routeData);
                    }

                    if (ImGui.Button(GetLoc("Stop")))
                        StopAutoTreasureHunt();
                }
            }
            
            ImGui.NewLine();
            
            if (ImGui.Checkbox($"{GetLoc("OccultCrescentHelper-TreasureManager-ShowLinkLine")} ({LuminaWrapper.GetAddonText(395)})", 
                               ref ModuleConfig.IsEnabledDrawLineToTreasure))
                ModuleConfig.Save(MainModule);
            
            if (ImGui.Checkbox($"{GetLoc("OccultCrescentHelper-TreasureManager-ShowLinkLine")} ({LuminaWrapper.GetEObjName(2014695)})", 
                               ref ModuleConfig.IsEnabledDrawLineToLog))
                ModuleConfig.Save(MainModule);
            
            if (ImGui.Checkbox($"{GetLoc("OccultCrescentHelper-TreasureManager-ShowLinkLine")} ({LuminaWrapper.GetItemName(48096)})", 
                               ref ModuleConfig.IsEnabledDrawLineToCarrot))
                ModuleConfig.Save(MainModule);
            
            ImGui.NewLine();

            var textSize = ImGui.CalcTextSize($"{LuminaWrapper.GetAddonText(395)} [999.99, 999.99, 999.99]");
            using (ImRaii.Disabled(TreasureTaskHelper.IsBusy))
            {
                if (OriginalPosition != default)
                {
                    if (ImGui.Button($"[{OriginalPosition.X:F1}, {OriginalPosition.Y:F1}, {OriginalPosition.Z:F1}]", 
                                     new(textSize.X * 2, ImGui.GetTextLineHeightWithSpacing())))
                    {
                        TreasureTaskHelper.Enqueue(() =>
                        {
                            PlayerController.Instance()->MoveControllerWalk.IsMovementLocked = true;
                            MovementManager.TPSmooth(OriginalPosition, DService.Condition[ConditionFlag.Mounted] ? 24 : 12, true, -20);
                
                            if (!Throttler.Throttle("OccultCrescentHelper-TreasureManager-Pathfind-Check")) return false;

                            if (LocalPlayerState.DistanceTo2D(OriginalPosition.ToVector2()) >= 3) return false;
                        
                            OnUpdate();

                            PlayerController.Instance()->MoveControllerWalk.IsMovementLocked = false;
                            return true;
                        });
                    }
                    
                    ImGui.Spacing();
                }

                foreach (var treasureData in Treasures)
                {
                    var pos = treasureData.Position;
                    if (ImGui.Button($"{treasureData.Name} [{pos.X:F1}, {pos.Y:F1}, {pos.Z:F1}]", new(textSize.X * 2, ImGui.GetTextLineHeightWithSpacing())))
                    {
                        OriginalPosition = LocalPlayerState.Object.Position;
                        
                        TreasureTaskHelper.Enqueue(() =>
                        {
                            PlayerController.Instance()->MoveControllerWalk.IsMovementLocked = true;
                            MovementManager.TPSmooth(pos, DService.Condition[ConditionFlag.Mounted] ? 24 : 12, true, -20);
                
                            if (!Throttler.Throttle("OccultCrescentHelper-TreasureManager-Pathfind-Check")) return false;

                            if (LocalPlayerState.DistanceTo2D(pos.ToVector2()) >= 3) return false;
                        
                            OnUpdate();

                            PlayerController.Instance()->MoveControllerWalk.IsMovementLocked = false;
                            return true;
                        });
                    }
                }
            }
            
            
            ImGui.NewLine();
            
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("Command"));

            using (ImRaii.PushIndent())
                ImGui.Text($"/pdr {CommandTreasure} {GetLoc("OccultCrescentHelper-Command-PTreasure-Help")}");
        }
        
        public override void Uninit()
        {
            CommandManager.RemoveSubCommand(CommandTreasure);
            
            GamePacketManager.Unreg(OnPreSendPacket);
            
            DService.ClientState.TerritoryChanged -= OnZoneChanged;
            DService.UiBuilder.Draw               -= OnPosDraw;
            
            TreasureTaskHelper?.Abort();
            TreasureTaskHelper?.Dispose();
            TreasureTaskHelper = null;
            
            Treasures.Clear();
        }
        
        private void OnCommandTreasure(string command, string args)
        {
            if (GameState.TerritoryIntendedUse != 61) return;

            args = args.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(args)) return;
            
            if (args == "abort")
            {
                StopAutoTreasureHunt();
                return;
            }
            
            var (_, routeData) = Routes.Where(x => x.Key.Contains(args, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.Key.Length).FirstOrDefault();
            if (routeData.Count <= 0) return;

            EnqueueAutoTreasureHunt(routeData);
        }

        private void EnqueueAutoTreasureHunt(List<TreasureHuntPoint> routeData)
        {
            TreasureTaskHelper.Abort();
            QueuedGatheringList.Clear();

            if (LocalPlayerState.DistanceTo2D(CrescentAetheryte.ExpeditionBaseCamp.Position.ToVector2()) <= 50)
            {
                NotificationError(GetLoc("OccultCrescentHelper-TreasureManager-AutoOpenTreasure-Notification-Danger"));
                return;
            }

            QueuedGatheringList = PathPlanner.PlanShortestPath(LocalPlayerState.Object.Position, routeData);
            CurrentRoute        = new(QueuedGatheringList);
            MoveToNextTreasurePoint();
        }

        private static void StopAutoTreasureHunt()
        {
            TreasureTaskHelper.Abort();
            QueuedGatheringList.Clear();
            CurrentRoute.Clear();

            PlayerController.Instance()->MoveControllerWalk.IsMovementLocked = false;
        }
        
        private void MoveToNextTreasurePoint()
        {
            TreasureTaskHelper.Abort();

            if (QueuedGatheringList.Count == 0)
            {
                StopAutoTreasureHunt();
                
                var message = GetLoc("OccultCrescentHelper-TreasureManager-AutoOpenTreasure-Notification-End");
                NotificationInfo(message);
                Speak(message);

                // 亚返回
                UseActionManager.UseActionLocation(ActionType.Action, 41343);
                return;
            }

            var data          = QueuedGatheringList.Dequeue();
            var position      = data.Position;
            var foundTreasure = false;

            TreasureTaskHelper.Enqueue(() =>
            {
                if (DService.Condition[ConditionFlag.Mounted]) return true;
                if (!Throttler.Throttle("OccultCrescentHelper-TreasureManager-AutoOpenTreasure-UseMount")) return false;
        
                if (IsCasting) return false;
        
                UseActionManager.UseAction(ActionType.GeneralAction, 9);
                return false;
            });
            
            TreasureTaskHelper.Enqueue(() =>
            {
                PlayerController.Instance()->MoveControllerWalk.IsMovementLocked = true;
                MovementManager.TPSmooth(position, 24, foundTreasure, -20);
                
                if (!Throttler.Throttle("OccultCrescentHelper-TreasureManager-Pathfind-Check")) return false;

                if (!data.IsExact)
                {
                    // 还没加载出来呢
                    if (LocalPlayerState.DistanceTo3D(position) >= 50) return false;
                }
                else
                {
                    if (LocalPlayerState.DistanceTo2D(position.ToVector2()) >= 3) return false;
                }

                OnUpdate();

                // 找到了, 移动过去
                if (Treasures.FirstOrDefault(x => x.ObjectType == SpecialObjectType.Treasure && 
                                                  Vector2.DistanceSquared(x.Position.ToVector2(), position.ToVector2()) <= 225) is { } obj)
                {
                    position      = obj.Position;
                    foundTreasure = true;
                    return false;
                }
                
                // 点位没有, 直接去下一个
                return true;
            });
            
            TreasureTaskHelper.Enqueue(MoveToNextTreasurePoint, "下一轮开始");
        }
        
        public static void OnPreSendPacket(ref bool isPrevented, int opcode, ref byte* packet, ref ushort priority)
        {
            if (GameState.TerritoryIntendedUse != 61 ||
                !TreasureTaskHelper.IsBusy           ||
                opcode != GamePacketOpcodes.PositionUpdateInstanceOpcode)
                return;
            
            isPrevented = true;
        }

        // 区域切换时清除掉宝藏信息和地图纹理
        private static void OnZoneChanged(ushort obj)
        {
            Treasures.Clear();
            CurrentRoute.Clear();
            QueuedGatheringList.Clear();
        }
        
        // 绘制连接线和地图
        private static void OnPosDraw()
        {
            if (GameState.TerritoryIntendedUse != 61) return;
            
            // 绘制地图
            if (TreasureTaskHelper.IsBusy)
                DrawTreasureRouteMap();
            
            // 绘制连接线
            if (Treasures is { Count: > 0 } treasures &&
                DService.ObjectTable.LocalPlayer is { } localPlayer)
            {
                foreach (var treasure in treasures)
                {
                    switch (treasure.ObjectType)
                    {
                        case SpecialObjectType.Treasure when !ModuleConfig.IsEnabledDrawLineToTreasure:
                        case SpecialObjectType.SurveyPoint when !ModuleConfig.IsEnabledDrawLineToLog:
                        case SpecialObjectType.SurveyPoint when !ModuleConfig.IsEnabledDrawLineToCarrot:
                            continue;
                    }

                    if (!DService.Gui.WorldToScreen(treasure.Position,    out var screenPos) ||
                        !DService.Gui.WorldToScreen(localPlayer.Position, out var localScreenPos))
                        continue;

                    DrawLine(localScreenPos, screenPos, treasure);
                }
            }
        }
        
        // 绘制寻宝路线地图
        private static void DrawTreasureRouteMap()
        {
            if (DService.ObjectTable.LocalPlayer is not { } localPlayer) return;
            
            var map = GameState.MapData;

            var displaySize = ScaledVector2(400);
            
            ImGui.SetNextWindowSize(displaySize + ScaledVector2(20, 40));
            ImGui.SetNextWindowBgAlpha(0.8f);
            
            if (ImGui.Begin("###AutoTreasureHuntMap", WindowFlags))
            {
                var drawList   = ImGui.GetWindowDrawList();
                var contentPos = ImGui.GetCursorScreenPos();
                
                ImGui.Image(DService.Texture.GetFromGame(map.GetTexturePath()).GetWrapOrEmpty().Handle, displaySize);
                
                if (CurrentRoute.Count > 0)
                {
                    for (var i = 0; i < CurrentRoute.Count - 1; i++)
                    {
                        var currentPoint = WorldToTexture(CurrentRoute[i].Position, map);
                        var nextPoint = WorldToTexture(CurrentRoute[i + 1].Position, map);
                        
                        var currentScreenPos = contentPos + (currentPoint * displaySize / 2048f);
                        var nextScreenPos    = contentPos + (nextPoint    * displaySize / 2048f);
                        
                        drawList.AddLine(currentScreenPos, nextScreenPos, LineColorBlue, 2.0f);
                    }
                }
                
                foreach (var point in CurrentRoute)
                {
                    var texturePos = WorldToTexture(point.Position, map);
                    var screenPos  = contentPos + (texturePos * displaySize / 2048f);
                    drawList.AddCircleFilled(screenPos, 4.0f, DotColor);
                }
                
                var playerTexturePos = WorldToTexture(localPlayer.Position, map);
                var playerScreenPos  = contentPos + (playerTexturePos * displaySize / 2048f);
                drawList.AddCircleFilled(playerScreenPos, 6.0f, PlayerColor);
            }
            
            ImGui.End();
        }
        
        // 更新箱子数据并处理开箱
        public override void OnUpdate()
        {
            RefreshTreasuresAround();
            HandleAutoOpenTreasures();
        }

        // 自动开箱
        private static void HandleAutoOpenTreasures()
        {
            if (GameState.TerritoryIntendedUse != 61       ||
                !ModuleConfig.IsEnabledAutoOpenTreasure    ||
                DService.Condition[ConditionFlag.InCombat] ||
                Treasures is not { Count: > 0 } treasures)
                return;

            if (DService.ObjectTable.LocalPlayer is not { IsDead: false, Position.Y: > -40 }) return;
            
            foreach (var treasure in treasures)
            {
                if (treasure.ObjectType != SpecialObjectType.Treasure) continue;
                if (treasure.GetGameObject() is not { } gameObject) continue;
                
                var treasureObj = (TreasureObjectTemp*)gameObject.Address;
                if (treasureObj->Flags.HasFlag(FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure.TreasureFlags.Opened) ||
                    treasureObj->Flags.HasFlag(FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure.TreasureFlags.FadedOut))
                    continue;
                
                if (LocalPlayerState.DistanceTo2D(treasure.Position.ToVector2()) > ModuleConfig.DistanceToAutoOpenTreasure) continue;
                
                // 一次只开 1 个, 避免移速上限
                InteractWithTreasure(gameObject);
                return;
            }
        }

        // 更新箱子数据
        private static void RefreshTreasuresAround()
        {
            if (GameState.TerritoryIntendedUse != 61) return;
            
            var newTreasures = new List<TreasureData>();
            
            // 仅在未战斗或正在坐骑上时更新数据 (考虑到坐骑上误进战状态)
            if (!DService.Condition[ConditionFlag.InCombat] || DService.Condition[ConditionFlag.Mounted])
            {
                foreach (var obj in DService.ObjectTable.Where(o => !((RenderFlag)o.RenderFlags).HasFlag(RenderFlag.Invisible)))
                {
                    if (TreasureData.FromGameObject(obj) is not { } treasure) continue;

                    if (treasure.ObjectType == SpecialObjectType.Treasure)
                    {
                        var treasureObj = (TreasureObjectTemp*)obj.Address;
                        if (treasureObj->Flags.HasFlag(FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure.TreasureFlags.Opened) ||
                            treasureObj->Flags.HasFlag(FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure.TreasureFlags.FadedOut))
                            continue;
                    }

                    newTreasures.Add(treasure);
                }
            }

            Treasures = newTreasures.OrderBy(x => x.EntityID).ToList();
        }
        
        private static void DrawLine(Vector2 startPos, Vector2 endPos, TreasureData treasure, uint lineColor = 0)
        {
            lineColor = lineColor == 0 ? LineColorBlue : lineColor;
        
            var drawList = ImGui.GetForegroundDrawList();

            drawList.AddLine(startPos, endPos, lineColor, 8f);
            drawList.AddCircleFilled(startPos, 12f, DotColor);
            drawList.AddCircleFilled(endPos,   12f, DotColor);
        
            ImGui.SetNextWindowPos(endPos);
            if (ImGui.Begin($"OccultCrescentHelper-{treasure.EntityID}", WindowFlags))
            {
                using (ImRaii.Group())
                {
                    ScaledDummy(12f);

                    if (DService.Texture.TryGetFromGameIcon(new(60354), out var texture))
                    {
                        var origPosY = ImGui.GetCursorPosY();
                        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - (8f * GlobalFontScale));
                        ImGui.Image(texture.GetWrapOrEmpty().Handle, new(ImGui.GetTextLineHeightWithSpacing()));
                        ImGui.SetCursorPosY(origPosY);
                        ImGui.SameLine();
                    }

                    var name = treasure.ObjectType switch
                    {
                        SpecialObjectType.Treasure    => LuminaWrapper.GetAddonText(395),
                        SpecialObjectType.SurveyPoint => LuminaWrapper.GetEObjName(2014695),
                        SpecialObjectType.Carrot      => LuminaWrapper.GetItemName(48096),
                        _                             => string.Empty
                    };
                
                    ImGuiOm.TextOutlined(KnownColor.Orange.ToVector4(), $"{name}");
                }

                ImGui.End();
            }
        }
        
        private static void InteractWithTreasure(IGameObject obj)
        {
            if (DService.ObjectTable.LocalPlayer is not { } localPlayer) return;
            
            PositionUpdateInstancePacket.Send(localPlayer.Rotation, obj.Position);
            new TreasureOpenPacket(obj.EntityId).Send();
            PositionUpdateInstancePacket.Send(localPlayer.Rotation, localPlayer.Position);
        }

        public class TreasureData(SpecialObjectType objectType, uint entityID, string name, Vector3 position)
        {
            public SpecialObjectType ObjectType { get; } = objectType;
            public uint              EntityID   { get; } = entityID;
            public string            Name       { get; } = name ?? string.Empty;
            public Vector3           Position   { get; } = position;
            
            public static TreasureData? FromGameObject(IGameObject? gameObject)
            {
                try
                {
                    if (gameObject == null || !gameObject.IsValid() || gameObject.Address == nint.Zero)
                        return null;

                    var type = SpecialObjectType.Treasure;
                    switch (gameObject.ObjectKind)
                    {
                        case ObjectKind.Treasure:
                            break;
                        case ObjectKind.EventObj:
                            switch (gameObject.EntityId)
                            {
                                case (uint)SpecialObjectType.SurveyPoint:
                                    type = SpecialObjectType.SurveyPoint;
                                    break;
                                case (uint)SpecialObjectType.Carrot:
                                    type = SpecialObjectType.Carrot;
                                    break;
                                default:
                                    return null;
                            }
                            break;
                        default:
                            return null;
                    }
                    
                    return new(type, gameObject.EntityId, gameObject.Name.ExtractText(), gameObject.Position);
                }
                catch
                {
                    return null;
                }
            }
            
            public IGameObject? GetGameObject() => 
                DService.ObjectTable.SearchByEntityId(EntityID);
        }

        public class TreasureHuntPoint(float x, float y, float z, bool isExact = false)
        {
            public Vector3 Position { get; } = new(x, y, z);
            public bool    IsExact  { get; } = isExact;
        }
        
        public enum SpecialObjectType : uint
        {
            /// <summary>
            /// 宝藏
            /// </summary>
            Treasure,
                
            /// <summary>
            /// 调查地点
            /// </summary>
            SurveyPoint = 2014695,
                
            /// <summary>
            /// 胡萝卜
            /// </summary>
            Carrot = 2010139,
        }
        
        private enum RenderFlag
        {
            Invisible = 256
        }

        private static readonly Dictionary<string, List<TreasureHuntPoint>> Routes = new()
        {
            [GetLoc("OccultCrescentHelper-TreasureManager-AutoHuntTresures-Route-SouthHornNorth")] =
            [
                new(617.09f, 66.30f, -703.88f),
                new(490.41f, 62.46f, -590.57f),
                new(386.92f, 96.79f, -451.38f),
                new(381.73f, 22.17f, -743.65f),
                new(142.11f, 16.40f, -574.06f),
                new(-118.97f, 4.99f, -708.46f),
                new(-451.68f, 2.98f, -775.57f),
                new(-585.29f, 4.99f, -864.84f),
                new(-729.43f, 4.99f, -724.82f),
                new(-825.1f, 3.0f, -833.6f),
                new(-884.12f, 3.80f, -682.03f),
                new(-661.71f, 2.98f, -579.49f),
                new(-491.02f, 2.98f, -529.59f),
                new(-140.46f, 22.35f, -414.27f),
                new(-343.16f, 52.32f, -382.13f),
                new(-487.11f, 98.53f, -205.46f),
                new(-444.11f, 90.68f, 26.23f),
                new(-394.89f, 106.74f, 175.43f),
                new(-713.80f, 62.06f, 192.61f),
                new(-756.83f, 76.55f, 97.37f),
                new(-682.80f, 135.61f, -195.27f),
                new(-729.92f, 116.53f, -79.06f),
                new(-856.96f, 68.83f, -93.16f),
                new(-798.25f, 105.58f, -310.57f),
                new(-767.45f, 115.62f, -235.00f),
                new(-680.54f, 104.84f, -354.79f)
            ],
            [GetLoc("OccultCrescentHelper-TreasureManager-AutoHuntTresures-Route-SouthHornSouth")] =
            [
                new(666.53f, 79.12f, -480.37f),
                new(870.66f, 95.69f, -388.36f),
                new(779.02f, 96.09f, -256.24f),
                new(770.75f, 107.99f, -143.57f),
                new(726.28f, 108.14f, -67.92f),
                new(475.73f, 95.99f, -87.08f),
                new(609.61f, 107.99f, 117.27f),
                new(788.88f, 120.38f, 109.39f),
                new(826.69f, 122.00f, 434.99f),
                new(869.29f, 109.97f, 581.20f),
                new(835.08f, 69.99f, 699.09f),
                new(697.32f, 69.99f, 597.92f),
                new(596.46f, 70.30f, 622.77f),
                new(433.71f, 70.30f, 683.53f),
                new(294.88f, 56.08f, 640.22f),
                new(140.98f, 55.99f, 770.99f),
                new(35.72f, 65.11f, 648.95f),
                new(256.15f, 73.17f, 492.36f),
                new(471.18f, 70.30f, 530.02f),
                new(642.97f, 69.99f, 407.80f),
                new(517.75f, 67.89f, 236.13f),
                new(277.79f, 103.78f, 241.90f),
                new(245.59f, 109.12f, -18.17f),
                new(354.12f, 95.66f, -288.93f),
                new(354.12f, 95.66f, -288.93f),
                new(55.28f, 111.31f, -289.08f),
                new(-158.65f, 98.62f, -132.74f),
                new(-25.68f, 102.22f, 150.16f),
                new(-256.89f, 120.99f, 125.08f),
                new(-401.66f, 85.04f, 332.54f),
                new(-283.99f, 115.98f, 377.04f),
                new(8.99f, 103.20f, 426.96f),
                new(-197.19f, 74.91f, 618.34f),
                new(-225.02f, 75.00f, 804.99f),
                new(-372.67f, 75.00f, 527.43f),
                new(-550.13f, 106.98f, 627.74f),
                new(-600.27f, 138.99f, 802.64f),
                new(-645.69f, 202.99f, 710.17f),
                new(-716.15f, 170.98f, 794.43f),
                new(-676.42f, 170.98f, 640.38f),
                new(-784.76f, 138.99f, 699.76f),
                new(-729.55f, 106.98f, 561.15f),
                new(-648.00f, 75.00f, 403.95f)
            ],
        };

        public static class PathPlanner
        {
            public static Queue<TreasureHuntPoint> PlanShortestPath(Vector3 currentPosition, List<TreasureHuntPoint> locations)
            {
                if (locations == null || locations.Count == 0)
                    return [];
                
                var startPoint = new TreasureHuntPoint(currentPosition.X, currentPosition.Y, currentPosition.Z);

                var allPoints = new List<TreasureHuntPoint> { startPoint };
                allPoints.AddRange(locations);

                var orderedPath = CreateInitialPathNearestNeighbor(allPoints);

                OptimizePath2Opt(orderedPath);

                orderedPath.RemoveAt(0);
                return new Queue<TreasureHuntPoint>(orderedPath);
            }

            private static List<TreasureHuntPoint> CreateInitialPathNearestNeighbor(List<TreasureHuntPoint> points)
            {
                var remainingPoints = new List<TreasureHuntPoint>(points);
                var orderedPath     = new List<TreasureHuntPoint>();

                var currentPoint = remainingPoints[0];
                orderedPath.Add(currentPoint);
                remainingPoints.RemoveAt(0);

                while (remainingPoints.Count > 0)
                {
                    TreasureHuntPoint nearestPoint = null;
                    var               minDistance  = float.MaxValue;

                    foreach (var point in remainingPoints)
                    {
                        var distance = Vector3.Distance(currentPoint.Position, point.Position);
                        if (distance < minDistance)
                        {
                            minDistance  = distance;
                            nearestPoint = point;
                        }
                    }

                    if (nearestPoint != null)
                    {
                        orderedPath.Add(nearestPoint);
                        remainingPoints.Remove(nearestPoint);
                        currentPoint = nearestPoint;
                    }
                }

                return orderedPath;
            }

            private static void OptimizePath2Opt(List<TreasureHuntPoint> path)
            {
                var improvementFound = true;
                var n                = path.Count;

                while (improvementFound)
                {
                    improvementFound = false;
                    for (var i = 0; i < n - 2; i++)
                    {
                        for (var j = i + 2; j < n - 1; j++)
                        {
                            var p1 = path[i].Position;
                            var p2 = path[i + 1].Position;
                            var p3 = path[j].Position;
                            var p4 = path[j + 1].Position;

                            var currentDist = Vector3.Distance(p1, p2) + Vector3.Distance(p3, p4);
                            var newDist     = Vector3.Distance(p1, p3) + Vector3.Distance(p2, p4);

                            if (newDist < currentDist)
                            {
                                path.Reverse(i + 1, j - i);
                                improvementFound = true;
                            }
                        }
                    }
                }
            }
        }
    }

    // TODO: 等待国际服 FFCS 合并
    [StructLayout(LayoutKind.Explicit)]
    private struct TreasureObjectTemp
    {
        [FieldOffset(0x1FC)]
        public FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure.TreasureFlags Flags;
    }
}
