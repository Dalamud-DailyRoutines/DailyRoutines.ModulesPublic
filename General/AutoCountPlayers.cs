using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Common.Info.Models;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using OmenTools.Info.Lumina;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;
using TerritoryIntendedUse = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoCountPlayers : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoCountPlayersTitle"),
        Description = Lang.Get("AutoCountPlayersDescription"),
        Category    = ModuleCategory.General
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static bool IsPlayerSearchLocation =>
        IsContentSearchZone || IsPlayerSearchZone;

    private static bool IsContentSearchZone =>
        ContentMemberListValidZones.Contains(GameState.TerritoryIntendedUse);

    private static bool IsPlayerSearchZone =>
        GameState.TerritoryType > 0 && 
        GameState.ContentFinderCondition == 0 &&
        Sheets.PlayerSearchPlaceNames.ContainsKey(GameState.TerritoryTypeData.PlaceNameZone.RowId);
    
    private Hook<InfoProxyContentMember.Delegates.EndRequest>? InfoProxyContentMemberEndRequestHook;
    private Hook<InfoProxySearch.Delegates.EndRequest>?        InfoProxySearchEndRequestHook;

    private Config        config = null!;
    private IDtrBarEntry? entry;

    private readonly Dictionary<uint, byte[]>              jobIcons          = [];
    private readonly Dictionary<uint, PlayerTargetingInfo> lastTargetingData = [];

    private string searchInput     = string.Empty;
    private string searchZoneInput = string.Empty;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        entry       ??= DService.Instance().DTRBar.Get("DailyRoutines-AutoCountPlayers");
        entry.Shown =   true;
        entry.Text  =   $"{Lang.Get("AutoCountPlayers-PlayersAroundCount")}: 0";
        entry.OnClick = _ =>
        {
            EnsureOverlay();
            Overlay.IsOpen ^= true;
        };

        WindowManager.Instance().PostDraw += OnDraw;

        PlayersManager.Instance().ReceivePlayersAround      += OnReceivePlayers;
        PlayersManager.Instance().ReceivePlayersTargetingMe += OnPlayersTargetingMeUpdate;

        InfoProxyContentMemberEndRequestHook = InfoProxyContentMember.Instance()->VirtualTable->HookVFuncFromName
        (
            "EndRequest",
            (InfoProxyContentMember.Delegates.EndRequest)InfoProxyContentMemberRequestDetour
        );
        InfoProxyContentMemberEndRequestHook.Enable();
        
        InfoProxySearchEndRequestHook = InfoProxySearch.Instance()->VirtualTable->HookVFuncFromName
        (
            "EndRequest",
            (InfoProxySearch.Delegates.EndRequest)InfoProxySearchRequestDetour
        );
        InfoProxySearchEndRequestHook.Enable();

        LogMessageManager.Instance().RegPre(OnLogMessage);
        FrameworkManager.Instance().Reg(OnUpdate, throttleMS: 10_000);
        OnUpdate(DService.Instance().Framework);
    }

    protected override void Uninit()
    {
        FrameworkManager.Instance().Unreg(OnUpdate);
        LogMessageManager.Instance().Unreg(OnLogMessage);

        WindowManager.Instance().PostDraw                   -= OnDraw;
        PlayersManager.Instance().ReceivePlayersAround      -= OnReceivePlayers;
        PlayersManager.Instance().ReceivePlayersTargetingMe -= OnPlayersTargetingMeUpdate;

        foreach (var info in lastTargetingData.Values)
        {
            var duration = DateTime.Now - info.TargetingStartTime;
            config.TargetingHistories.Add
            (
                new()
                {
                    Name        = info.Player.Name,
                    HomeWorldID = info.Player.HomeWorld.RowId,
                    JobID       = info.Player.ClassJob.RowId,
                    StartTime   = info.TargetingStartTime,
                    Duration    = duration,
                    ZoneID      = GameState.TerritoryType
                }
            );
        }

        if (lastTargetingData.Count > 0)
        {
            lastTargetingData.Clear();
            if (config.TargetingHistories.Count > 100)
                config.TargetingHistories.RemoveRange(0, config.TargetingHistories.Count - 100);
            config.Save(this);
        }

        entry?.Remove();
        entry = null;
    }

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(120f * GlobalUIScale);
        if (ImGui.InputFloat(Lang.Get("Scale"), ref config.ScaleFactor, 0, 0, "%.1f"))
            config.ScaleFactor = Math.Max(0.1f, config.ScaleFactor);
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);

        ImGui.NewLine();

        if (ImGui.Checkbox(Lang.Get("AutoCountPlayers-DisplayLineWhenTargetingMe"), ref config.DisplayLineWhenTargetingMe))
            config.Save(this);

        if (config.DisplayLineWhenTargetingMe)
        {
            using (ImRaii.PushIndent())
            {
                if (ImGui.Checkbox(Lang.Get("SendChat"), ref config.SendChat))
                    config.Save(this);

                if (ImGui.Checkbox(Lang.Get("SendNotification"), ref config.SendNotification))
                    config.Save(this);

                if (ImGui.Checkbox(Lang.Get("SendTTS"), ref config.SendTTS))
                    config.Save(this);

                if (config.SendNotification || config.SendTTS)
                {
                    using (ImRaii.PushIndent())
                    {
                        if (ImGui.Checkbox(Lang.Get("AutoCountPlayers-FilterFriend"), ref config.FilterFriend))
                            config.Save(this);
                    }
                }
            }
        }


    }

    protected override void OverlayUI()
    {
        using var tabBar = ImRaii.TabBar("##Tab");
        if (!tabBar) return;

        using (var item = ImRaii.TabItem(Lang.Get("AutoCountPlayers-PlayersAround")))
        {
            if (item)
            {
                ImGui.SetNextItemWidth(-1f);
                ImGui.InputText("###Search", ref searchInput, 128);

                if (DService.Instance().Condition.IsBetweenAreas) return;

                using var child = ImRaii.Child("列表", ImGui.GetContentRegionAvail() - ImGui.GetStyle().ItemSpacing, true);
                if (!child) return;

                foreach (var playerAround in PlayersManager.Instance().PlayersAround)
                {
                    using var id = ImRaii.PushId($"{playerAround.GameObjectID}");

                    if (!string.IsNullOrWhiteSpace(searchInput) && !playerAround.Name.Contains(searchInput)) continue;

                    if (ImGuiOm.ButtonIcon("定位", FontAwesomeIcon.Flag, Lang.Get("Locate")))
                    {
                        var mapPos = PositionHelper.WorldToMap(playerAround.Position.ToVector2(), GameState.MapData);
                        var message = new SeStringBuilder()
                                      .Add
                                      (
                                          new PlayerPayload
                                          (
                                              playerAround.Name,
                                              playerAround.ToStruct()->HomeWorld
                                          )
                                      )
                                      .Append(" (")
                                      .AddIcon(playerAround.ClassJob.Value.ToBitmapFontIcon())
                                      .Append($" {playerAround.ClassJob.Value.Name})")
                                      .Add(new NewLinePayload())
                                      .Append("     ")
                                      .Append(SeString.CreateMapLink(GameState.TerritoryType, GameState.Map, mapPos.X, mapPos.Y))
                                      .Build();

                        // TODO: 改成 ReadOnlyString
                        NotifyHelper.Instance().Chat(message.Encode());
                    }

                    var gameGUI  = DService.Instance().GameGUI;
                    var viewport = ImGui.GetMainViewport();

                    gameGUI.WorldToScreen(playerAround.Position, out var screenPos, out var isInView);

                    if (!gameGUI.WorldToScreen(LocalPlayerState.Object.Position, out var localScreenPos, out _))
                        localScreenPos = viewport.Pos + new Vector2(viewport.Size.X * 0.5f, viewport.Size.Y);

                    if (!ImGui.IsAnyItemHovered() || ImGui.IsItemHovered())
                    {
                        var linePositions = GetLinePositions
                        (
                            screenPos,
                            isInView,
                            viewport.Pos,
                            viewport.Size,
                            OFFSCREEN_MARKER_INSET * GlobalUIScale,
                            OFFSCREEN_LINE_OVERFLOW * GlobalUIScale
                        );

                        DrawLine(localScreenPos, linePositions.LineEnd, linePositions.Marker, playerAround, isInView);
                    }

                    if (DService.Instance().Texture.TryGetFromGameIcon(playerAround.ClassJob.Value.GetIcon(), out var texture))
                    {
                        ImGui.SameLine();
                        ImGui.Image(texture.GetWrapOrEmpty().Handle, new(ImGui.GetFrameHeight()));
                    }

                    ImGui.SameLine();
                    ImGuiOm.RenderPlayerInfo(playerAround.Name, playerAround.HomeWorld.Value.Name.ToString());
                }
            }
        }
        
        if (IsPlayerSearchLocation)
        {
            using var item = ImRaii.TabItem(Lang.Get("AutoCountPlayers-PlayersInZone"));
            if (item)
            {
                ImGui.SetNextItemWidth(-1f);
                ImGui.InputText("###Search", ref searchZoneInput, 128);

                if (ICondition.Instance().IsBetweenAreas) return;

                using var child = ImRaii.Child("列表", ImGui.GetContentRegionAvail() - ImGui.GetStyle().ItemSpacing, true);
                if (!child) return;
                
                var info = IsPlayerSearchZone ?
                               (InfoProxyCommonList*)InfoProxySearch.Instance() :
                               (InfoProxyCommonList*)InfoProxyContentMember.Instance();
                
                for (var index = 0; index < info->EntryCount; index++)
                {
                    var player = info->CharDataSpan[index];
                    if (IsPlayerSearchZone && player.Location != GameState.TerritoryType) continue;

                    using var id = ImRaii.PushId($"{player.ContentId}");

                    if (!string.IsNullOrWhiteSpace(searchZoneInput) && !player.NameString.Contains(searchZoneInput)) continue;
                    
                    ImGui.Image
                    (
                        ITextureProvider.Instance().GetFromGameIcon(LuminaWrapper.GetJobIcon(player.Job)).GetWrapOrEmpty().Handle,
                        new(ImGui.GetTextLineHeight())
                    );

                    ImGui.SameLine(0f, 4f * GlobalUIScale);
                    ImGuiOm.RenderPlayerInfo(player.NameString, LuminaWrapper.GetWorldName(player.HomeWorld));
                }
            }
        }

        using (var item = ImRaii.TabItem(Lang.Get("AutoCountPlayers-TargetedHistory")))
        {
            if (item)
            {
                foreach (var record in config.TargetingHistories.AsEnumerable().Reverse())
                {
                    ImGui.TextDisabled($"{record.StartTime:MM/dd HH:mm:ss}");

                    if (DService.Instance().Texture.TryGetFromGameIcon(LuminaGetter.GetRowOrDefault<ClassJob>(record.JobID).GetIcon(), out var texture))
                    {
                        ImGui.SameLine();
                        ImGui.Image(texture.GetWrapOrEmpty().Handle, new(ImGui.GetTextLineHeight()));
                    }

                    ImGui.SameLine();
                    ImGuiOm.RenderPlayerInfo(record.Name, LuminaWrapper.GetWorldName(record.HomeWorldID));

                    ImGui.SameLine();
                    ImGui.TextColored(KnownColor.Orange.ToVector4(), $"[{record.Duration:mm\\:ss}]");

                    ImGui.SameLine();
                    ImGui.TextDisabled($"({LuminaWrapper.GetZonePlaceName(record.ZoneID)})");
                }
            }
        }
    }

    #region 事件

    private void OnDraw()
    {
        if (!config.DisplayLineWhenTargetingMe || PlayersManager.Instance().PlayersTargetingMe.Count == 0) return;

        if (!GameState.IsForeground) return;

        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return;

        if (NamePlate->IsAddonAndNodesReady())
        {
            var node = NamePlate->GetNodeById(1);

            if (node != null)
            {
                var nodeState = node->GetNodeState();

                if (ImGui.Begin($"AutoCountPlayers-{localPlayer->EntityId}", WINDOW_FLAGS))
                {
                    ImGui.SetWindowPos(nodeState.Center - (ImGui.GetWindowSize() * 0.75f));

                    using (FontManager.Instance().UIFont140.Push())
                    using (ImRaii.Group())
                    {
                        ImGuiHelpers.SeStringWrapped(new SeStringBuilder().AddIcon(BitmapFontIcon.Warning).Encode());

                        ImGui.SameLine();
                        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - (1.2f * GlobalUIScale));
                        ImGuiOm.TextOutlined
                            (KnownColor.Orange.ToUInt(), $"{PlayersManager.Instance().PlayersTargetingMe.Count}", KnownColor.SaddleBrown.ToUInt());

                        if (GameState.ContentFinderCondition == 0)
                        {
                            using (FontManager.Instance().UIFont80.Push())
                            {
                                var text = Lang.Get("AutoCountPlayers-Notification-SomeoneTargetingMe");
                                ImGuiOm.TextOutlined
                                (
                                    ImGui.GetCursorScreenPos() - new Vector2(ImGui.CalcTextSize(text).X * 0.3f, 0),
                                    KnownColor.Orange.ToUInt(),
                                    $"({text})",
                                    KnownColor.SaddleBrown.ToUInt()
                                );
                            }
                        }
                    }

                    ImGui.End();
                }
            }
        }

        var gameGUI  = DService.Instance().GameGUI;
        var viewport = ImGui.GetMainViewport();

        if (!gameGUI.WorldToScreen(localPlayer->Position, out var localScreenPos, out _))
            localScreenPos = viewport.Pos + new Vector2(viewport.Size.X * 0.5f, viewport.Size.Y);

        foreach (var playerInfo in PlayersManager.Instance().PlayersTargetingMe)
        {
            gameGUI.WorldToScreen(playerInfo.Player.Position, out var screenPos, out var isInView);

            var linePositions = GetLinePositions
            (
                screenPos,
                isInView,
                viewport.Pos,
                viewport.Size,
                OFFSCREEN_MARKER_INSET * GlobalUIScale,
                OFFSCREEN_LINE_OVERFLOW * GlobalUIScale
            );

            DrawLine
            (
                localScreenPos,
                linePositions.LineEnd,
                linePositions.Marker,
                playerInfo.Player,
                isInView,
                true,
                $" [{TimeSpan.FromSeconds(playerInfo.TargetingDurationSeconds)}]"
            );
        }
    }

    private static void OnLogMessage
    (
        ref bool                isPrevented,
        ref uint                logMessageID,
        ref LogMessageQueueItem item
    )
    {
        if (logMessageID != 81) return;
        isPrevented = true;
    }

    private static void OnUpdate
    (
        IFramework framework
    )
    {
        if (!IsPlayerSearchLocation) return;
        
        if (IsContentSearchZone)
        {
            if (InfoProxyContentMember.Instance() == null ||
                AgentModule.Instance()->GetAgentByInternalId(AgentId.ContentMemberList)->IsAgentActive())
                return;
            
            AgentId.ContentMemberList.SendEvent(0, 1);
        }
        else if (IsPlayerSearchZone)
        {
            var searchInstance = InfoProxySearch.Instance();
            if (searchInstance== null ||
                AgentModule.Instance()->GetAgentByInternalId(AgentId.Search)->IsAgentActive())
                return;

            searchInstance->JobMask          = 0xFFFFFFFFFFFFFFFF; // all
            searchInstance->LevelMin         = 1;
            searchInstance->LevelMax         = 255;
            searchInstance->GrandCompanyMask = 0xFF;
            searchInstance->LanguageMask     = 0xFF;
            searchInstance->OnlineStatusMask = 0x800000000000;
            searchInstance->LocationIDs[0]   = (ushort)GameState.TerritoryTypeData.PlaceNameZone.RowId;
            searchInstance->LocationCount    = 1;
            for (var i = 0; i < searchInstance->Name.Length; i++)
                searchInstance->Name[i] = 0;

            searchInstance->RequestData();
        }
        else
            FrameworkManager.Instance().Unreg(OnUpdate);
    }

    private void OnReceivePlayers
    (
        IReadOnlyList<IPlayerCharacter> characters
    )
    {
        if (entry == null) return;
        
        if (IsPlayerSearchLocation)
            entry.Shown = true;
        else
            entry.Shown = !ICondition.Instance()[ConditionFlag.InCombat] || GameState.IsInPVPArea;

        if (!entry.Shown)
        {
            EnsureOverlay();
            Overlay.IsOpen = false;
            return;
        }

        entry.Text = $"{Lang.Get("AutoCountPlayers-PlayersAroundCount")}: {PlayersManager.Instance().PlayersAroundCount}" +
                     (PlayersManager.Instance().PlayersTargetingMe.Count == 0 ?
                          string.Empty :
                          $" ({PlayersManager.Instance().PlayersTargetingMe.Count})");

        // 特殊场景探索
        if (IsContentSearchZone)
        {
            entry.Text.Append
            (
                $" / {Lang.Get("AutoCountPlayers-PlayersZoneCount")}: " +
                $"{InfoProxyContentMember.Instance()->EntryCount}"
            );
        }
        else if (IsPlayerSearchZone)
        {
            var count = InfoProxySearch.Instance()->CharDataSpan
                        .ToArray()
                        .Count(x => x.Job > 0 && x.Location == GameState.TerritoryType);
            entry.Text.Append
            (
                $" / {Lang.Get("AutoCountPlayers-PlayersZoneCount")}: " +
                $"{count}"
            );
        }

        if (characters.Count == 0)
        {
            entry.Tooltip = string.Empty;
            return;
        }

        var tooltip = new SeStringBuilder();

        if (PlayersManager.Instance().PlayersTargetingMe.Count > 0)
        {
            tooltip.AddUiForeground(32)
                   .AddText($"{Lang.Get("AutoCountPlayers-PlayersTargetingMe")}")
                   .AddUiForegroundOff()
                   .Add(NewLinePayload.Payload);

            PlayersManager.Instance().PlayersTargetingMe.ForEach
            (info =>
                 tooltip
                     .AddIcon(info.Player.ClassJob.Value.ToBitmapFontIcon())
                     .AddText($"{info.Player.Name}")
                     .AddIcon(BitmapFontIcon.CrossWorld)
                     .AddText($"{info.Player.HomeWorld.Value.Name}")
                     .Add(NewLinePayload.Payload)
            );
        }

        tooltip.AddUiForeground(32)
               .AddText($"{Lang.Get("AutoCountPlayers-PlayersAroundInfo")}")
               .AddUiForegroundOff()
               .Add(NewLinePayload.Payload);

        characters.ForEach
        (info => tooltip
                 .AddIcon(info.ClassJob.Value.ToBitmapFontIcon())
                 .AddText($"{info.Name}")
                 .AddIcon(BitmapFontIcon.CrossWorld)
                 .AddText($"{info.HomeWorld.Value.Name}")
                 .Add(NewLinePayload.Payload)
        );

        var message = tooltip.Build();
        if (message.Payloads.Last() is NewLinePayload)
            message.Payloads.RemoveAt(message.Payloads.Count - 1);

        entry.Tooltip = message;
    }

    private void OnPlayersTargetingMeUpdate
    (
        IReadOnlyList<PlayerTargetingInfo> targetingPlayersInfo
    )
    {
        var currentIDs     = targetingPlayersInfo.Select(x => x.Player.EntityID).ToHashSet();
        var endedTargeting = lastTargetingData.Where(x => !currentIDs.Contains(x.Key)).ToList();

        if (endedTargeting.Count > 0)
        {
            foreach (var (key, info) in endedTargeting)
            {
                var duration = DateTime.Now - info.TargetingStartTime;

                config.TargetingHistories.Add
                (
                    new()
                    {
                        Name        = info.Player.Name,
                        HomeWorldID = info.Player.HomeWorld.RowId,
                        JobID       = info.Player.ClassJob.RowId,
                        StartTime   = info.TargetingStartTime,
                        Duration    = duration,
                        ZoneID      = GameState.TerritoryType
                    }
                );

                lastTargetingData.Remove(key);
            }

            if (config.TargetingHistories.Count > 100)
                config.TargetingHistories.RemoveRange(0, config.TargetingHistories.Count - 100);

            config.Save(this);
        }

        foreach (var info in targetingPlayersInfo)
        {
            if (info.Player.ClassJob.RowId == 0) continue;
            lastTargetingData[info.Player.EntityID] = info;
        }

        if (targetingPlayersInfo.Count > 0 &&
            (GameState.ContentFinderCondition == 0 || DService.Instance().PartyList.Length < 2))
        {
            var newTargetingPlayers = targetingPlayersInfo.Where(info => info.IsNew).ToList();

            if (newTargetingPlayers.Any(info => Throttler.Shared.Throttle($"AutoCountPlayers-Player-{info.Player.EntityID}", 30_000)))
            {
                if (config.SendTTS)
                {
                    if (!config.FilterFriend || targetingPlayersInfo.All(x => !x.Player.ToStruct()->IsFriend))
                        NotifyHelper.Speak(Lang.Get("AutoCountPlayers-Notification-SomeoneTargetingMe"));
                }

                if (config.SendNotification)
                {
                    if (!config.FilterFriend || targetingPlayersInfo.All(x => !x.Player.ToStruct()->IsFriend))
                        NotifyHelper.Instance().NotificationWarning(Lang.Get("AutoCountPlayers-Notification-SomeoneTargetingMe"));
                }

                if (config.SendChat)
                {
                    var builder = new SeStringBuilder();

                    builder.Append($"{Lang.Get("AutoCountPlayers-Notification-SomeoneTargetingMe")}:");
                    builder.Add(new NewLinePayload());

                    foreach (var info in targetingPlayersInfo)
                    {
                        builder.Add(new PlayerPayload(info.Player.Name, info.Player.HomeWorld.RowId))
                               .Append(" (")
                               .AddIcon(info.Player.ClassJob.Value.ToBitmapFontIcon())
                               .Append($" {info.Player.ClassJob.Value.Name})");
                        builder.Add(new NewLinePayload());
                    }

                    var message = builder.Build();
                    if (message.Payloads.Last() is NewLinePayload)
                        message.Payloads.RemoveAt(message.Payloads.Count - 1);

                    // TODO: 改成 ReadOnlyString
                    NotifyHelper.Instance().Chat(builder.Build().Encode());
                }
            }
        }
    }

    private void InfoProxyContentMemberRequestDetour
    (
        InfoProxyContentMember* proxy
    )
    {
        InfoProxyContentMemberEndRequestHook.Original(proxy);
        OnReceivePlayers(PlayersManager.Instance().PlayersAround);
    }
    
    private void InfoProxySearchRequestDetour
    (
        InfoProxySearch* proxy
    )
    {
        InfoProxySearchEndRequestHook.Original(proxy);
        OnReceivePlayers(PlayersManager.Instance().PlayersAround);
    }

    #endregion

    private void DrawLine
    (
        Vector2    startPos,
        Vector2    lineEndPos,
        Vector2    markerPos,
        ICharacter chara,
        bool       isMarkerVisible,
        bool       isAlert = false,
        string?    extraInfo = null
    )
    {
        var drawList     = ImGui.GetForegroundDrawList();
        var lineColor    = isAlert ? AlertLineColor : InfoLineColor;
        var labelColor   = isAlert ? AlertLabelColor : InfoLabelColor;
        var startRadius  = START_MARKER_RADIUS * GlobalUIScale;
        var markerRadius = TARGET_MARKER_RADIUS * GlobalUIScale;

        drawList.AddLine(startPos, lineEndPos, LineOutlineColor, LINE_OUTLINE_THICKNESS * GlobalUIScale);
        drawList.AddLine(startPos, lineEndPos, lineColor, LINE_THICKNESS * GlobalUIScale);

        drawList.AddCircleFilled(startPos, startRadius + (START_MARKER_OUTLINE_SIZE * GlobalUIScale), LineOutlineColor);
        drawList.AddCircleFilled(startPos, startRadius, lineColor);

        if (isMarkerVisible)
        {
            drawList.AddCircleFilled(markerPos, markerRadius + (TARGET_MARKER_OUTLINE_SIZE * GlobalUIScale), LineOutlineColor);
            drawList.AddCircle(markerPos, markerRadius, lineColor, TARGET_MARKER_SEGMENTS, TARGET_MARKER_THICKNESS * GlobalUIScale);
            drawList.AddCircleFilled(markerPos, TARGET_MARKER_CORE_RADIUS * GlobalUIScale, MarkerCoreColor);
        }

        var viewportCenter = ImGui.GetMainViewport().GetCenter();
        var labelPivot = new Vector2
        (
            markerPos.X >= viewportCenter.X ? 1f : 0f,
            markerPos.Y >= viewportCenter.Y ? 1f : 0f
        );

        ImGui.SetNextWindowPos(markerPos, ImGuiCond.Always, labelPivot);

        if (ImGui.Begin($"AutoCountPlayers-{chara.EntityID}", WINDOW_FLAGS))
        {
            using (ImRaii.Group())
            {
                ImGuiOm.ScaledDummy(12f);

                var icon = jobIcons.GetOrAdd
                (
                    chara.ClassJob.RowId,
                    _ => new SeStringBuilder().AddIcon(chara.ClassJob.Value.ToBitmapFontIcon()).Encode()
                );
                ImGui.SameLine();
                ImGuiHelpers.SeStringWrapped(icon);

                ImGui.SameLine();
                ImGuiOm.TextOutlined(labelColor, $"{chara.Name}" + (extraInfo ?? string.Empty));
            }

            ImGui.End();
        }
    }

    private static (Vector2 LineEnd, Vector2 Marker) GetLinePositions
    (
        Vector2 projectedPosition,
        bool    isInView,
        Vector2 viewportPosition,
        Vector2 viewportSize,
        float   markerInset,
        float   lineOverflow
    )
    {
        if (isInView) return (projectedPosition, projectedPosition);

        var viewportCenter = viewportPosition + (viewportSize * 0.5f);
        var direction      = projectedPosition - viewportCenter;

        if (!float.IsFinite(direction.X) ||
            !float.IsFinite(direction.Y) ||
            direction.LengthSquared() < MIN_DIRECTION_LENGTH_SQUARED)
            direction = Vector2.UnitY;

        var halfWidth           = Math.Max(viewportSize.X * 0.5f, 1f);
        var halfHeight          = Math.Max(viewportSize.Y * 0.5f, 1f);
        var horizontalT         = MathF.Abs(direction.X) > float.Epsilon ? halfWidth / MathF.Abs(direction.X) : float.MaxValue;
        var verticalT           = MathF.Abs(direction.Y) > float.Epsilon ? halfHeight / MathF.Abs(direction.Y) : float.MaxValue;
        var edgePosition        = viewportCenter + (direction * MathF.Min(horizontalT, verticalT));
        var normalizedDirection = Vector2.Normalize(direction);

        return
        (
            edgePosition + (normalizedDirection * lineOverflow),
            edgePosition - (normalizedDirection * markerInset)
        );
    }

    private void EnsureOverlay()
    {
        if (Overlay != null) return;

        Overlay            =  new(this);
        Overlay.Flags      &= ~ImGuiWindowFlags.NoTitleBar;
        Overlay.Flags      &= ~ImGuiWindowFlags.AlwaysAutoResize;
        Overlay.WindowName =  $"{Lang.Get("AutoCountPlayers-PlayersAroundInfo")}###AutoCountPlayers-Overlay";
    }

    private class Config : ModuleConfig
    {
        public bool DisplayLineWhenTargetingMe = true;

        public bool  FilterFriend;
        public float ScaleFactor = 1;

        public bool SendChat         = true;
        public bool SendNotification = true;
        public bool SendTTS          = true;

        public List<TargetingRecord> TargetingHistories = [];
    }

    private class TargetingRecord
    {
        public string   Name        { get; set; } = string.Empty;
        public uint     HomeWorldID { get; set; }
        public uint     JobID       { get; set; }
        public uint     ZoneID      { get; set; }
        public DateTime StartTime   { get; set; }
        public TimeSpan Duration    { get; set; }
    }

    #region 常量

    private const ImGuiWindowFlags WINDOW_FLAGS =
        ImGuiWindowFlags.NoScrollbar           |
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
        ImGuiWindowFlags.NoInputs              |
        ImGuiWindowFlags.NoSavedSettings;

    private const float LINE_THICKNESS               = 3f;
    private const float LINE_OUTLINE_THICKNESS       = 5f;
    private const float START_MARKER_RADIUS          = 2.25f;
    private const float START_MARKER_OUTLINE_SIZE    = 1.25f;
    private const float TARGET_MARKER_RADIUS         = 4f;
    private const float TARGET_MARKER_OUTLINE_SIZE   = 1.5f;
    private const float TARGET_MARKER_THICKNESS      = 1.5f;
    private const float TARGET_MARKER_CORE_RADIUS    = 1.25f;
    private const int   TARGET_MARKER_SEGMENTS       = 16;
    private const float OFFSCREEN_MARKER_INSET       = 14f;
    private const float OFFSCREEN_LINE_OVERFLOW      = 32f;
    private const float MIN_DIRECTION_LENGTH_SQUARED = 0.001f;

    private const float INFO_LINE_OPACITY    = 0.78f;
    private const float ALERT_LINE_OPACITY   = 0.88f;
    private const float INFO_LABEL_OPACITY   = 0.96f;
    private const float ALERT_LABEL_OPACITY  = 0.98f;
    private const float LINE_OUTLINE_OPACITY = 0.4f;
    private const float MARKER_CORE_OPACITY  = 0.88f;

    private static readonly uint InfoLineColor =
        (KnownColor.DeepSkyBlue.ToVector4() with { W = INFO_LINE_OPACITY }).ToUInt();

    private static readonly uint AlertLineColor =
        (KnownColor.IndianRed.ToVector4() with { W = ALERT_LINE_OPACITY }).ToUInt();

    private static readonly uint InfoLabelColor =
        (KnownColor.LightSkyBlue.ToVector4() with { W = INFO_LABEL_OPACITY }).ToUInt();

    private static readonly uint AlertLabelColor =
        (KnownColor.LightCoral.ToVector4() with { W = ALERT_LABEL_OPACITY }).ToUInt();

    private static readonly uint LineOutlineColor =
        (KnownColor.Black.ToVector4() with { W = LINE_OUTLINE_OPACITY }).ToUInt();

    private static readonly uint MarkerCoreColor =
        (KnownColor.WhiteSmoke.ToVector4() with { W = MARKER_CORE_OPACITY }).ToUInt();

    private static readonly FrozenSet<TerritoryIntendedUse> ContentMemberListValidZones =
    [
        TerritoryIntendedUse.OccultCrescent,
        TerritoryIntendedUse.Bozja,
        TerritoryIntendedUse.Eureka
    ];

    #endregion
}
