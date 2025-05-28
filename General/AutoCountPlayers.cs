using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace DailyRoutines.ModulesPublic;

public class AutoCountPlayers : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoCountPlayersTitle"),
        Description = GetLoc("AutoCountPlayersDescription"),
        Category    = ModuleCategories.General,
    };
    
    private const ImGuiWindowFlags WindowFlags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar |
                                                 ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoFocusOnAppearing |
                                                 ImGuiWindowFlags.NoNavFocus | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize |
                                                 ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoInputs;

    private static readonly uint LineColorBlue = ImGui.ColorConvertFloat4ToU32(LightSkyBlue);
    private static readonly uint LineColorRed  = ImGui.ColorConvertFloat4ToU32(Red);
    private static readonly uint DotColor      = ImGui.ColorConvertFloat4ToU32(RoyalBlue);

    private static Config        ModuleConfig = null!;
    private static IDtrBarEntry? Entry;

    private static readonly Dictionary<uint, byte[]> JobIcons = [];
    
    private static List<IPlayerCharacter> TargetingMePlayers = [];
    
    private static HashSet<uint> LastTargetingEntityIDs = [];

    private static string SearchInput = string.Empty;

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        Overlay            ??= new(this);
        Overlay.Flags      &=  ~ImGuiWindowFlags.NoTitleBar;
        Overlay.Flags      &=  ~ImGuiWindowFlags.AlwaysAutoResize;
        Overlay.WindowName =   $"{GetLoc("AutoCountPlayers-PlayersAroundInfo")}###AutoCountPlayers-Overlay";

        Entry ??= DService.DtrBar.Get("DailyRoutines-AutoCountPlayers");
        Entry.Shown = true;
        Entry.OnClick += () => Overlay.IsOpen ^= true;

        DService.UiBuilder.Draw += OnDraw;

        PlayersManager.ReceivePlayersAround += OnUpdate;
    }

    public override void ConfigUI()
    {
        ImGui.SetNextItemWidth(120f * GlobalFontScale);
        if (ImGui.InputFloat(GetLoc("Scale"), ref ModuleConfig.ScaleFactor, 0, 0, "%.1f"))
            ModuleConfig.ScaleFactor = Math.Max(0.1f, ModuleConfig.ScaleFactor);
        if (ImGui.IsItemDeactivatedAfterEdit())
            ModuleConfig.Save(this);

        ImGui.NewLine();

        if (ImGui.Checkbox(GetLoc("AutoCountPlayers-DisplayLineWhenTargetingMe"), ref ModuleConfig.DisplayLineWhenTargetingMe))
            ModuleConfig.Save(this);

        if (ImGui.Checkbox(GetLoc("SendChat"), ref ModuleConfig.SendChat))
            ModuleConfig.Save(this);

        if (ImGui.Checkbox(GetLoc("SendNotification"), ref ModuleConfig.SendNotification))
            ModuleConfig.Save(this);

        if (ImGui.Checkbox(GetLoc("SendTTS"), ref ModuleConfig.SendTTS))
            ModuleConfig.Save(this);
    }

    public override unsafe void OverlayUI()
    {
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("###Search", ref SearchInput, 128);

        if (BetweenAreas || DService.ClientState.LocalPlayer is not { } localPlayer) return;

        var source = PlayersManager.PlayersAround.Where(x => string.IsNullOrWhiteSpace(SearchInput) ||
                                               x.ToString().Contains(SearchInput, StringComparison.OrdinalIgnoreCase))
                                   .OrderBy(x => x.Name.TextValue.Length);

        using var child = ImRaii.Child("列表", ImGui.GetContentRegionAvail() - ImGui.GetStyle().ItemSpacing, true);
        if (!child) return;
        
        foreach (var playerAround in source)
        {
            using var id = ImRaii.PushId($"{playerAround.GameObjectId}");
            if (ImGuiOm.ButtonIcon("定位", FontAwesomeIcon.Flag, GetLoc("Locate")))
            {
                var mapPos = WorldToMap(playerAround.Position.ToVector2(), GameState.MapData);
                var message = new SeStringBuilder()
                              .Add(new PlayerPayload(playerAround.Name.TextValue,
                                                     playerAround.ToStruct()->HomeWorld))
                              .Append(" (")
                              .AddIcon(playerAround.ClassJob.Value.ToBitmapFontIcon())
                              .Append($" {playerAround.ClassJob.Value.Name})")
                              .Add(new NewLinePayload())
                              .Append("     ")
                              .Append(SeString.CreateMapLink(DService.ClientState.TerritoryType,
                                                             DService.ClientState.MapId, mapPos.X, mapPos.Y))
                              .Build();
                Chat(message);
            }

            if (DService.Gui.WorldToScreen(playerAround.Position, out var screenPos) &&
                DService.Gui.WorldToScreen(localPlayer.Position,  out var localScreenPos))
            {
                if (!ImGui.IsAnyItemHovered() || ImGui.IsItemHovered())
                    DrawLine(localScreenPos, screenPos, playerAround);
            }
            

            ImGui.SameLine();
            ImGui.Text($"{playerAround.Name} ({playerAround.ClassJob.Value.Name})");
        }
    }
    
    private static unsafe void OnDraw()
    {
    
        var mainViewPort = ImGui.GetMainViewport();
        var currentWindowPos = mainViewPort.Pos;
        var currentWindowSize = mainViewPort.Size;
        
        if (!ModuleConfig.DisplayLineWhenTargetingMe || TargetingMePlayers.Count == 0) return;
        
        var framework = Framework.Instance();
        if (framework == null || framework->WindowInactive) return;
        
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return;
        
        if (IsAddonAndNodesReady(NamePlate))
        {
            var node = NamePlate->GetNodeById(1);
            if (node != null)
            {
                var nodeState = NodeState.Get(node);
                if (ImGui.Begin($"AutoCountPlayers-{localPlayer->EntityId}", WindowFlags))
                {
                    ImGui.SetWindowPos((nodeState.Position2 / 2) - (ImGui.GetWindowSize() * 0.75f));
                    using (FontManager.UIFont140.Push())
                    using (ImRaii.Group())
                    {
                        ImGuiHelpers.SeStringWrapped(new SeStringBuilder().AddIcon(BitmapFontIcon.Warning).Encode());
                        
                        ImGui.SameLine();
                        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - (1.2f * GlobalFontScale));
                        ImGuiOm.TextOutlined(Orange, $"{TargetingMePlayers.Count}", Brown4);

                        // 副本内有点遮视线
                        if (GameState.ContentFinderCondition == 0)
                        {
                            using (FontManager.UIFont80.Push())
                            {
                                var text = GetLoc("AutoCountPlayers-Notification-SomeoneTargetingMe");
                                ImGuiOm.TextOutlined(ImGui.GetCursorScreenPos() - new Vector2(ImGui.CalcTextSize(text).X * 0.3f, 0),
                                                     ImGui.ColorConvertFloat4ToU32(Orange),
                                                     $"({text})", ImGui.ColorConvertFloat4ToU32(Brown4));
                            }
                        }
                    }

                    ImGui.End();
                }
            }
        }
        
        foreach (var player in TargetingMePlayers)
        {
            if (DService.Gui.WorldToScreen(player.Position, out var screenPos))
            {
                Vector2 localScreenPos;
                
                // 检查 localPlayer 世界坐标转换结果，失败时使用屏幕底部中央位置
                if (!DService.Gui.WorldToScreen(localPlayer->Position, out localScreenPos))
                {
                    localScreenPos = new Vector2(currentWindowSize.X / 2, currentWindowSize.Y);
                }
                
                DrawLine(localScreenPos, screenPos, player, LineColorRed);
            }
        }
    }

    private static void OnUpdate(IReadOnlyList<IPlayerCharacter> characters)
    {
        if (Entry == null) return;

        TargetingMePlayers = characters.Where(x => x.TargetObjectId == GameState.EntityID)
                                       .OrderBy(x => x.EntityId)
                                       .ToList();
        
        var currentTargetingEntityIds = TargetingMePlayers.Select(x => x.EntityId).ToHashSet();
        if (TargetingMePlayers.Count > 0 &&
            (GameState.ContentFinderCondition == 0 || DService.PartyList.Length < 2))
        {
            if (TargetingMePlayers.Any(x => Throttler.Throttle($"AutoCountPlayers-Player-{x.EntityId}", 30_000)) &&
                (currentTargetingEntityIds.Count != LastTargetingEntityIDs.Count || !currentTargetingEntityIds.SetEquals(LastTargetingEntityIDs)))
            {

                if (ModuleConfig.SendTTS)
                    Speak(GetLoc("AutoCountPlayers-Notification-SomeoneTargetingMe"));

                if (ModuleConfig.SendNotification)
                    NotificationWarning(GetLoc("AutoCountPlayers-Notification-SomeoneTargetingMe"));

                if (ModuleConfig.SendChat)
                {
                    var builder = new SeStringBuilder();

                    builder.Append($"{GetLoc("AutoCountPlayers-Notification-SomeoneTargetingMe")}:");
                    builder.Add(new NewLinePayload());
                    foreach (var player in TargetingMePlayers)
                    {
                        builder.Add(new PlayerPayload(player.Name.ExtractText(), player.HomeWorld.RowId))
                               .Append(" (")
                               .AddIcon(player.ClassJob.Value.ToBitmapFontIcon())
                               .Append($" {player.ClassJob.Value.Name})");
                        builder.Add(new NewLinePayload());
                    }

                    var message = builder.Build();
                    if (message.Payloads.Last() is NewLinePayload)
                        message.Payloads.RemoveAt(message.Payloads.Count - 1);

                    Chat(builder.Build());
                }
            }
        }

        LastTargetingEntityIDs = currentTargetingEntityIds;
        
        Entry.Text = $"{GetLoc("AutoCountPlayers-PlayersAroundCount")}: {PlayersManager.PlayersAroundCount}" +
                     (TargetingMePlayers.Count == 0 ? string.Empty : $" ({TargetingMePlayers.Count})");

        if (characters.Count == 0)
        {
            Entry.Tooltip = string.Empty;
            return;
        }
        
        var tooltip = new StringBuilder();

        if (TargetingMePlayers.Count > 0)
        {
            tooltip.AppendLine($"{GetLoc("AutoCountPlayers-PlayersTargetingMe")}:");
            TargetingMePlayers.ForEach(x => tooltip.AppendLine($"{x.Name} ({x.ClassJob.Value.Name.ExtractText()})"));
            tooltip.AppendLine(string.Empty);
        }
        
        tooltip.AppendLine($"{GetLoc("AutoCountPlayers-PlayersAroundInfo")}:");
        characters.ForEach(x => tooltip.AppendLine($"{x.Name} ({x.ClassJob.Value.Name.ExtractText()})"));
        
        Entry.Tooltip = tooltip.ToString().Trim();
    }

    private static void DrawLine(Vector2 startPos, Vector2 endPos, ICharacter chara, uint lineColor = 0)
    {
        lineColor = lineColor == 0 ? LineColorBlue : lineColor;
        
        var drawList = ImGui.GetForegroundDrawList();

        drawList.AddLine(startPos, endPos, lineColor, 8f);
        drawList.AddCircleFilled(startPos, 12f, DotColor);
        drawList.AddCircleFilled(endPos,   12f, DotColor);
        
        ImGui.SetNextWindowPos(endPos);
        if (ImGui.Begin($"AutoCountPlayers-{chara.EntityId}", WindowFlags))
        {
            using (ImRaii.Group())
            {
                ScaledDummy(12f);

                var icon = JobIcons.GetOrAdd(chara.ClassJob.RowId, 
                                             _ => new SeStringBuilder().AddIcon(chara.ClassJob.Value.ToBitmapFontIcon()).Encode());
                ImGui.SameLine();
                ImGuiHelpers.SeStringWrapped(icon);
                
                ImGui.SameLine();
                ImGuiOm.TextOutlined(Orange, $"{chara.Name}");
            }

            ImGui.End();
        }
    }

    public override void Uninit()
    {
        DService.UiBuilder.Draw -= OnDraw;
        PlayersManager.ReceivePlayersAround -= OnUpdate;
        
        TargetingMePlayers.Clear();
        LastTargetingEntityIDs.Clear();
        
        Entry?.Remove();
        Entry = null;
        
        base.Uninit();
    }
    
    public class Config : ModuleConfiguration
    {
        public float ScaleFactor = 1;

        public bool DisplayLineWhenTargetingMe = true;

        public bool SendNotification = true;
        public bool SendChat         = true;
        public bool SendTTS          = true;
    }
}
