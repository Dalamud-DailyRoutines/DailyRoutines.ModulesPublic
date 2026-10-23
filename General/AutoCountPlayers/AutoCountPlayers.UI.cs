using System.Numerics;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Utility;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class AutoCountPlayers
{
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

        if (ImGui.Checkbox(Lang.Get("AutoCountPlayers-FilterFriend"), ref config.FilterFriend))
            config.Save(this);
    }

    protected override void OverlayUI()
    {
        using var tabBar = ImRaii.TabBar("##Tab");
        if (!tabBar) return;

        DrawPlayersAroundTab();

        if (IsPlayerSearchLocation)
            DrawPlayersInZoneTab();

        DrawTargetingHistoryTab();
    }

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
                                var text = Lang.Get("AutoCountPlayers-Notification-Title");
                                ImGuiOm.TextOutlined
                                (
                                    ImGui.GetCursorScreenPos() - new Vector2(ImGui.CalcTextSize(text).X * 0.3f, 0),
                                    KnownColor.Orange.ToUInt(),
                                    $"（{text}）",
                                    KnownColor.SaddleBrown.ToUInt()
                                );
                            }
                        }
                    }
                }

                ImGui.End();
            }
        }

        foreach (var playerInfo in PlayersManager.Instance().PlayersTargetingMe)
        {
            DrawPlayerLine
            (
                playerInfo.Player,
                true,
                $" [{TimeSpan.FromSeconds(playerInfo.TargetingDurationSeconds)}]"
            );
        }
    }

    private void DrawPlayersAroundTab()
    {
        using var item = ImRaii.TabItem(Lang.Get("AutoCountPlayers-PlayersAround"));
        if (!item) return;

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("###Search", ref searchInput, 128);

        if (ICondition.Instance().IsBetweenAreas) return;

        using var child = ImRaii.Child("列表", ImGui.GetContentRegionAvail() - ImGui.GetStyle().ItemSpacing, true);
        if (!child) return;

        var players = PlayersManager.Instance().PlayersAround
                                    .Where(player => MatchesSearch(player.Name, searchInput))
                                    .ToList();

        foreach (var player in players)
        {
            using var id = ImRaii.PushId($"{player.GameObjectID}");

            if (!DrawPlayerRow(player.ClassJob.Value.GetIcon(), player.Name, player.HomeWorld.Value.Name.ToString(), clickable: true))
                continue;

            ImGui.SetTooltip(Lang.Get("Locate"));

            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                NotifyPlayerPosition(player);

            DrawPlayerLine(player);
        }
    }

    private void DrawPlayerLine
    (
        IPlayerCharacter player,
        bool             isAlert   = false,
        string?          extraInfo = null
    )
    {
        if (!GameViewHelper.TryGetProjection(out var projection))
            return;

        var viewport = ImGui.GetMainViewport();

        if (!projection.WorldToScreen(LocalPlayerState.Object.Position, out var localScreenPos, out _))
            localScreenPos = viewport.Pos + viewport.Size with { X = viewport.Size.X * 0.5f };

        projection.WorldToScreen(player.Position, out var screenPos, out var isInView);

        var linePositions = GetLinePositions
        (
            screenPos,
            isInView,
            viewport.Pos,
            viewport.Size,
            OFFSCREEN_MARKER_INSET  * GlobalUIScale,
            OFFSCREEN_LINE_OVERFLOW * GlobalUIScale
        );

        DrawLine(localScreenPos, linePositions.LineEnd, linePositions.Marker, player, isInView, isAlert, extraInfo);
    }

    private void DrawPlayersInZoneTab()
    {
        using var item = ImRaii.TabItem(Lang.Get("AutoCountPlayers-PlayersInZone"));
        if (!item) return;

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("###Search", ref searchZoneInput, 128);

        if (ICondition.Instance().IsBetweenAreas) return;

        using var child = ImRaii.Child("列表", ImGui.GetContentRegionAvail() - ImGui.GetStyle().ItemSpacing, true);
        if (!child) return;

        var info = IsPlayerSearchZone ?
                       (InfoProxyCommonList*)InfoProxySearch.Instance() :
                       (InfoProxyCommonList*)InfoProxyContentMember.Instance();

        foreach (var (dataCenterID, players) in GroupZonePlayersByDataCenter(info, searchZoneInput))
        {
            DrawSectionHeader(LuminaWrapper.GetDataCenterName(dataCenterID), players.Count);

            using (ImRaii.PushIndent(SECTION_PLAYER_INDENT))
            {
                foreach (var player in players)
                {
                    using var id = ImRaii.PushId($"{player.ContentId}");

                    DrawPlayerRow(LuminaWrapper.GetJobIcon(player.Job), player.NameString, LuminaWrapper.GetWorldName(player.HomeWorld));
                }
            }

            ImGui.Spacing();
        }
    }

    private void DrawTargetingHistoryTab()
    {
        using var item = ImRaii.TabItem(Lang.Get("AutoCountPlayers-TargetedHistory"));
        if (!item) return;

        using var child = ImRaii.Child("列表", ImGui.GetContentRegionAvail() - ImGui.GetStyle().ItemSpacing, true);
        if (!child) return;

        foreach (var record in config.TargetingHistories.AsEnumerable().Reverse())
        {
            using var id = ImRaii.PushId($"{record.StartTime.Ticks}-{record.Name}");

            DrawPlayerRow
            (
                LuminaGetter.GetRowOrDefault<ClassJob>(record.JobID).GetIcon(),
                record.Name,
                LuminaWrapper.GetWorldName(record.HomeWorldID),
                $"[{record.Duration:mm\\:ss}]",
                KnownColor.Orange,
                $"{LuminaWrapper.GetZonePlaceName(record.ZoneID)}  {record.StartTime:MM/dd HH:mm}"
            );
        }
    }

    private static bool DrawPlayerRow
    (
        uint       jobIconID,
        string     playerName,
        string     worldName,
        string?    trailingText  = null,
        KnownColor trailingColor = KnownColor.Gray,
        string?    subText       = null,
        bool       clickable     = false
    )
    {
        using var rented = new RentedSeStringBuilder();

        var playerInfo = rented.Append(playerName)
                               .AppendIcon((uint)BitmapFontIcon.CrossWorld)
                               .Append(worldName)
                               .ToReadOnlySeString();

        var style      = ImGui.GetStyle();
        var lineHeight = ImGui.GetTextLineHeight();
        var rowStart   = ImGui.GetCursorScreenPos();
        var rowHeight  = lineHeight + (style.FramePadding.Y * 2);
        var textPos    = rowStart with { Y = rowStart.Y + style.FramePadding.Y };
        var subSpacing = PLAYER_ROW_SUB_SPACING * GlobalUIScale;

        if (!string.IsNullOrEmpty(subText))
            rowHeight += lineHeight + subSpacing;

        var rowSize = new Vector2(ImGui.GetContentRegionAvail().X, rowHeight);

        if (clickable)
            ImGui.Selectable("##Row", false, ImGuiSelectableFlags.None, rowSize);
        else
            ImGui.Dummy(rowSize);

        var hovered = clickable && ImGui.IsItemHovered();
        var rowEnd  = ImGui.GetCursorScreenPos() with { X = rowStart.X };

        ImGui.SetCursorScreenPos(textPos);

        if (ITextureProvider.Instance().TryGetFromGameIcon(jobIconID, out var texture))
        {
            ImGui.Image(texture.GetWrapOrEmpty().Handle, new Vector2(lineHeight));

            ImGui.SameLine(0f, PLAYER_ROW_ICON_SPACING * GlobalUIScale);
        }

        var textStartX = ImGui.GetCursorScreenPos().X;

        ImGuiHelpers.SeStringWrapped(playerInfo);

        if (!string.IsNullOrEmpty(subText))
        {
            ImGui.SetCursorScreenPos(new Vector2(textStartX, textPos.Y + lineHeight + subSpacing));

            ImGui.TextColored(PlayerRowSubTextColor.ToVector4(), subText);
        }

        if (!string.IsNullOrEmpty(trailingText))
        {
            var trailingX = rowStart.X + rowSize.X - ImGui.CalcTextSize(trailingText).X - style.FramePadding.X;

            ImGui.SetCursorScreenPos(textPos with { X = trailingX });

            ImGui.TextColored(trailingColor.ToVector4(), trailingText);
        }

        ImGui.SetCursorScreenPos(rowEnd);

        return hovered;
    }

    private static void DrawSectionHeader
    (
        string title,
        int    count
    )
    {
        var drawList = ImGui.GetWindowDrawList();
        var paddingX = SECTION_HEADER_PADDING_HORIZONTAL * GlobalUIScale;
        var paddingY = SECTION_HEADER_PADDING_VERTICAL   * GlobalUIScale;
        var startPos = ImGui.GetCursorScreenPos();
        var width    = ImGui.GetContentRegionAvail().X;
        var height   = ImGui.GetTextLineHeight() + (paddingY * 2);
        var textPos  = startPos                  + new Vector2(paddingX, paddingY);

        drawList.AddText(textPos, SectionHeaderTextColor, title);

        var countText = count.ToString();

        drawList.AddText
        (
            textPos with { X = startPos.X + width - paddingX - ImGui.CalcTextSize(countText).X },
            SectionHeaderCountColor,
            countText
        );

        var lineY     = startPos.Y + height;
        var lineStart = startPos with { Y = lineY };
        var lineEnd   = lineStart with { X = startPos.X + width };

        drawList.AddLine(lineStart, lineEnd, SectionHeaderLineColor, SECTION_HEADER_LINE_THICKNESS * GlobalUIScale);

        ImGui.Dummy(new Vector2(width, height));
    }

    private void DrawLine
    (
        Vector2    startPos,
        Vector2    lineEndPos,
        Vector2    markerPos,
        ICharacter chara,
        bool       isMarkerVisible,
        bool       isAlert   = false,
        string?    extraInfo = null
    )
    {
        var drawList = ImGui.GetForegroundDrawList();
        var lineColor = isAlert ?
                            AlertLineColor :
                            InfoLineColor;
        var labelColor = isAlert ?
                             AlertLabelColor :
                             InfoLabelColor;
        var startRadius  = START_MARKER_RADIUS  * GlobalUIScale;
        var markerRadius = TARGET_MARKER_RADIUS * GlobalUIScale;

        drawList.AddLine(startPos, lineEndPos, LineOutlineColor, LINE_OUTLINE_THICKNESS * GlobalUIScale);
        drawList.AddLine(startPos, lineEndPos, lineColor,        LINE_THICKNESS         * GlobalUIScale);

        drawList.AddCircleFilled(startPos, startRadius + (START_MARKER_OUTLINE_SIZE * GlobalUIScale), LineOutlineColor);
        drawList.AddCircleFilled(startPos, startRadius,                                               lineColor);

        if (isMarkerVisible)
        {
            drawList.AddCircleFilled(markerPos, markerRadius + (TARGET_MARKER_OUTLINE_SIZE * GlobalUIScale), LineOutlineColor);
            drawList.AddCircle(markerPos, markerRadius, lineColor, TARGET_MARKER_SEGMENTS, TARGET_MARKER_THICKNESS * GlobalUIScale);
            drawList.AddCircleFilled(markerPos, TARGET_MARKER_CORE_RADIUS                                          * GlobalUIScale, MarkerCoreColor);
        }

        var viewportCenter = ImGui.GetMainViewport().GetCenter();
        var labelPivot = new Vector2
        (
            markerPos.X >= viewportCenter.X ?
                1f :
                0f,
            markerPos.Y >= viewportCenter.Y ?
                1f :
                0f
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

    private void EnsureOverlay()
    {
        if (Overlay != null) return;

        Overlay            =  new(this);
        Overlay.Flags      &= ~ImGuiWindowFlags.NoTitleBar;
        Overlay.Flags      &= ~ImGuiWindowFlags.AlwaysAutoResize;
        Overlay.WindowName =  $"{Lang.Get("AutoCountPlayers-PlayersAroundInfo")}###AutoCountPlayers-Overlay";
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

        var viewportCenter = viewportPosition  + (viewportSize * 0.5f);
        var direction      = projectedPosition - viewportCenter;

        if (!float.IsFinite(direction.X) ||
            !float.IsFinite(direction.Y) ||
            direction.LengthSquared() < MIN_DIRECTION_LENGTH_SQUARED)
            direction = Vector2.UnitY;

        var halfWidth  = Math.Max(viewportSize.X * 0.5f, 1f);
        var halfHeight = Math.Max(viewportSize.Y * 0.5f, 1f);
        var horizontalT = MathF.Abs(direction.X) > float.Epsilon ?
                              halfWidth / MathF.Abs(direction.X) :
                              float.MaxValue;
        var verticalT = MathF.Abs(direction.Y) > float.Epsilon ?
                            halfHeight / MathF.Abs(direction.Y) :
                            float.MaxValue;
        var edgePosition        = viewportCenter + (direction * MathF.Min(horizontalT, verticalT));
        var normalizedDirection = Vector2.Normalize(direction);

        return
            (
                edgePosition + (normalizedDirection * lineOverflow),
                edgePosition - (normalizedDirection * markerInset)
            );
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

    private const float SECTION_HEADER_PADDING_HORIZONTAL = 6f;
    private const float SECTION_HEADER_PADDING_VERTICAL   = 4f;
    private const float SECTION_HEADER_LINE_THICKNESS     = 1f;
    private const float SECTION_PLAYER_INDENT             = 12f;
    private const float PLAYER_ROW_ICON_SPACING           = 4f;
    private const float PLAYER_ROW_SUB_SPACING            = 2f;

    private const float INFO_LINE_OPACITY    = 0.78f;
    private const float ALERT_LINE_OPACITY   = 0.88f;
    private const float INFO_LABEL_OPACITY   = 0.96f;
    private const float ALERT_LABEL_OPACITY  = 0.98f;
    private const float LINE_OUTLINE_OPACITY = 0.4f;
    private const float MARKER_CORE_OPACITY  = 0.88f;

    private const float SECTION_HEADER_TEXT_OPACITY  = 0.78f;
    private const float SECTION_HEADER_COUNT_OPACITY = 0.45f;
    private const float SECTION_HEADER_LINE_OPACITY  = 0.08f;
    private const float PLAYER_ROW_SUB_OPACITY       = 0.68f;

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

    private static readonly uint SectionHeaderTextColor =
        (KnownColor.LightGray.ToVector4() with { W = SECTION_HEADER_TEXT_OPACITY }).ToUInt();

    private static readonly uint SectionHeaderCountColor =
        (KnownColor.Gray.ToVector4() with { W = SECTION_HEADER_COUNT_OPACITY }).ToUInt();

    private static readonly uint SectionHeaderLineColor =
        (KnownColor.White.ToVector4() with { W = SECTION_HEADER_LINE_OPACITY }).ToUInt();

    private static readonly uint PlayerRowSubTextColor =
        (KnownColor.Gray.ToVector4() with { W = PLAYER_ROW_SUB_OPACITY }).ToUInt();

    #endregion
}
