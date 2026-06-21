using System.Numerics;
using DailyRoutines.Extensions;
using DailyRoutines.Internal;
using DailyRoutines.Manager;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using OmenTools.ImGuiOm.Widgets.MapRenderer;
using OmenTools.Info.Game.AetheryteRecord;
using OmenTools.Info.Game.AetheryteRecord.Enums;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Utils.FuzzyMatcher;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace DailyRoutines.ModulesPublic.Interface.BetterTeleport;

public unsafe partial class BetterTeleport
{
    private FuzzyMatcher<AetheryteRecord>? recordMatcher;
    
    private List<AetheryteRecord> favorites = [];

    private bool shouldFocusSearchBar;
    
    private AetheryteRecord? hoveredAetheryte;
    private AetheryteRecord? pinnedAetheryte;

    private Vector3 contextMenuTargetPos;
    private uint    contextMenuTargetZone;

    private readonly ImGuiMapRenderer mapRenderer = new()
    {
        MinZoom              = 0.2f,
        MaxZoom              = 4.0f,
        LerpSpeed            = 15.0f,
        EnableDefaultMarkers = true,
        DefaultMarkerFilter  = marker => marker.DataType is not (3 or 4)
    };
    
    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Command")}:");

        ImGui.SameLine();
        ImGui.TextWrapped($"{COMMAND} {Lang.Get("BetterTeleport-CommandHelp")}");

        ImGui.NewLine();

        DrawGeneralSettings(150f * GlobalUIScale);
    }
    
    private FuzzyMatcher<AetheryteRecord> CreateRecordMatcher() =>
        new
        (
            AllRecords,
            x =>
            {
                var keys = new List<(IEnumerable<string?>, FuzzySearchWeight)>();

                if (config.Remarks.TryGetValue(GetConfigKey(x), out var remark) && !string.IsNullOrWhiteSpace(remark))
                    keys.Add(([remark], FuzzySearchWeight.Title));

                keys.Add(([x.Name], FuzzySearchWeight.Name));

                var zoneName = x.GetZone().ExtractPlaceName();
                keys.Add(([x.RegionName, zoneName], FuzzySearchWeight.Default));

                return keys;
            }
        );

    private void SetupMapRenderer(AetheryteRecord aetheryte, bool isPinned)
    {
        mapRenderer.SetMap(aetheryte.MapID);
        mapRenderer.Zoomable         = isPinned;
        mapRenderer.Pannable         = isPinned;
        mapRenderer.EnableResizeGrip = isPinned;

        if (!isPinned)
            mapRenderer.ResetView();
        else if (mapRenderer.CustomViewportSize == Vector2.Zero)
            mapRenderer.CustomViewportSize = new(450f * GlobalUIScale);

        mapRenderer.ClearMarkers();

        var mapID    = aetheryte.GetMap().RowId;
        var siblings = AllRecords.Where(x => x.GetMap().RowId == mapID).ToList();

        // 1. 绘制其他水晶 Marker
        foreach (var record in siblings)
        {
            if (record.RowID == aetheryte.RowID) continue;

            var recordPos   = config.Positions.TryGetValue(GetConfigKey(record), out var redirected) ? redirected : record.Position;
            var isAetheryte = record.IsAetheryte;

            var marker = new ImGuiMapMarker
            {
                ID          = GetConfigKey(record),
                Position    = recordPos,
                IconID      = isAetheryte ? 60453U : 60430U,
                Name        = record.Name,
                Color       = 0xCCFFFFFF,
                Size        = ScaledVector2(28f),
                ShowLabel   = false,
                TooltipText = record.Name,
                OnClick = m =>
                {
                    if (isPinned)
                    {
                        HandleTeleport(record);
                        pinnedAetheryte = null;
                        config.Save(this);
                    }
                }
            };
            mapRenderer.AddMarker(marker);
        }

        // 2. 绘制当前主水晶 Marker (带高亮脉冲、Label)
        var targetPos   = config.Positions.TryGetValue(GetConfigKey(aetheryte), out var redirectedPos) ? redirectedPos : aetheryte.Position;
        var displayName = config.Remarks.TryGetValue(GetConfigKey(aetheryte), out var remark) ? remark : aetheryte.Name;

        var mainMarker = new ImGuiMapMarker
        {
            ID          = GetConfigKey(aetheryte),
            Position    = targetPos,
            IconID      = aetheryte.IsAetheryte ? 60453U : 60430U,
            Name        = aetheryte.Name,
            Color       = 0xFFFFFFFF,
            Size        = ScaledVector2(32f),
            PulseEffect = true,
            PulseColor  = ImGui.GetColorU32(ImGuiCol.CheckMark),
            Label       = displayName,
            ShowLabel   = true,
            LabelColor  = KnownColor.LightSkyBlue.ToVector4().ToUInt(),
            OnClick = m =>
            {
                if (isPinned)
                {
                    HandleTeleport(aetheryte);
                    pinnedAetheryte = null;
                    config.Save(this);
                }
            }
        };
        mapRenderer.AddMarker(mainMarker);

        // 3. 处理红旗 Marker (如果右键菜单打开了)
        if (ImGui.IsPopupOpen("BetterTeleport_Map_ContextMenu"))
        {
            var flagMarker = new ImGuiMapMarker
            {
                ID          = "ContextMenuTargetFlag",
                Position    = contextMenuTargetPos,
                IconID      = 60561U,
                Size        = ScaledVector2(32f),
                ShowLabel   = false,
                ShowTooltip = false
            };
            mapRenderer.AddMarker(flagMarker);
        }

        // 4. 处理右键空白处，寻找最近水晶并弹出菜单
        mapRenderer.OnMapClicked = (_, clickedWorldPos, _, button) =>
        {
            if (isPinned && button == ImGuiMouseButton.Right)
            {
                contextMenuTargetZone = aetheryte.ZoneID;
                contextMenuTargetPos  = clickedWorldPos;
                ImGui.OpenPopup("BetterTeleport_Map_ContextMenu");
            }
        };
    }

    private void DrawHoveredTooltip()
    {
        if (hoveredAetheryte != null && PluginConfig.Instance().ConflictKeyBinding.IsPressed())
            pinnedAetheryte = hoveredAetheryte;

        if (pinnedAetheryte != null)
        {
            ImGui.SetNextWindowBgAlpha(0.85f);

            if (ImGui.Begin
                (
                    "###PinnedAetheryteMap",
                    ImGuiWindowFlags.NoDecoration     |
                    ImGuiWindowFlags.AlwaysAutoResize |
                    ImGuiWindowFlags.NoSavedSettings
                ))
            {
                var hint     = Lang.Get("BetterTeleport-MapHint-Zoom");
                var hintSize = ImGui.CalcTextSize(hint);
                var size     = mapRenderer.CustomViewportSize.X > 50f ? mapRenderer.CustomViewportSize : new Vector2(400f * GlobalUIScale, 400f * GlobalUIScale);

                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ((size.X - hintSize.X) / 2));
                ImGui.TextDisabled(hint);

                SetupMapRenderer(pinnedAetheryte, true);
                mapRenderer.Draw(size);

                DrawMapContextMenu();

                if (!ImGui.IsWindowFocused() && !ImGui.IsPopupOpen("BetterTeleport_Map_ContextMenu"))
                {
                    pinnedAetheryte = null;
                    config.Save(this);
                }

                ImGui.End();
            }
        }

        if (hoveredAetheryte == null || ImGui.IsPopupOpen("AetheryteContextPopup")) return;

        if (pinnedAetheryte != null && hoveredAetheryte.RowID == pinnedAetheryte.RowID)
            return;

        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero))
        using (ImRaii.PushColor(ImGuiCol.PopupBg, ImGui.GetColorU32(ImGuiCol.WindowBg)))
        using (ImRaii.Tooltip())
        {
            var tex  = DService.Instance().Texture.GetFromGame(hoveredAetheryte.GetMap().GetTexturePath());
            var warp = tex.GetWrapOrEmpty();

            if (warp.Handle != nint.Zero)
            {
                var widthScale = Math.Min(1f, warp.Width / 2048f);
                var imageSize  = ScaledVector2(384f      * widthScale);

                ImGuiOm.ScaledDummy(0f, 2f);
                var hint     = Lang.Get("BetterTeleport-MapHint-Pin");
                var hintSize = ImGui.CalcTextSize(hint);
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ((imageSize.X - hintSize.X) / 2));
                ImGui.TextDisabled(hint);

                SetupMapRenderer(hoveredAetheryte, false);
                mapRenderer.Draw(imageSize);
            }
        }
    }

    private void DrawMapContextMenu()
    {
        using var popup = ImRaii.Popup("BetterTeleport_Map_ContextMenu");
        if (!popup) return;

        if (ImGui.MenuItem(Lang.Get("BetterTeleport-TeleportToThisPosition")))
        {
            TaskHelper.Abort();

            if (GameState.TerritoryType != contextMenuTargetZone || IsWithPermission())
            {
                TaskHelper.Enqueue(() => MovementManager.Instance().TPSmart_BetweenZone(contextMenuTargetZone, contextMenuTargetPos));
                TaskHelper.Enqueue
                (() =>
                    {
                        if (MovementManager.Instance().IsManagerBusy || DService.Instance().ObjectTable.LocalPlayer == null)
                            return false;

                        MovementManager.Instance().TPGround();
                        if (DService.Instance().Condition.IsBetweenAreas || DService.Instance().Condition[ConditionFlag.Jumping])
                            return false;

                        return true;
                    }
                );
            }
            else
            {
                TaskHelper.Enqueue(() => MovementManager.Instance().TeleportNearestAetheryte(contextMenuTargetZone, contextMenuTargetPos));
                TaskHelper.Enqueue(() => DService.Instance().Condition.IsBetweenAreas && DService.Instance().ObjectTable.LocalPlayer != null);
                TaskHelper.Enqueue
                (() =>
                    {
                        if (!DService.Instance().Condition.IsBetweenAreas) return true;
                        MovementManager.Instance().TPSmart_InZone(contextMenuTargetPos, false);
                        return false;
                    }
                );
            }

            ImGui.CloseCurrentPopup();
        }
    }

    private static void DrawBottomToolbar()
    {
        var drawList = ImGui.GetWindowDrawList();
        var startPos = ImGui.GetCursorScreenPos();
        var height   = 24f * GlobalUIScale;

        long gil        = 0;
        var  tickets    = 0;
        var  invManager = InventoryManager.Instance();

        if (invManager != null)
        {
            gil     = invManager->GetGil();
            tickets = invManager->GetInventoryItemCount(TELEPORT_TICKET_ITEM_ID);
        }

        var curX = startPos.X + (2f * GlobalUIScale);
        var curY = startPos.Y + (2f * GlobalUIScale);

        var pillBgCol     = ImGui.GetColorU32(ImGuiCol.FrameBg);
        var pillBorderCol = ImGui.GetColorU32(ImGuiCol.Border);
        var paddingX      = 8f  * GlobalUIScale;
        var pillHeight    = 20f * GlobalUIScale;

        // 1. 绘制金币 Pill
        var   gilText      = gil.ToChineseString();
        var   gilUnit      = LuminaWrapper.GetItemName(GIL_ITEM_ID);
        var   gilTextWidth = ImGui.CalcTextSize(gilText).X;
        float gilUnitWidth;
        using (FontManager.Instance().UIFont60.Push())
            gilUnitWidth = ImGui.CalcTextSize(gilUnit).X;

        var gilIcon      = DService.Instance().Texture.GetFromGameIcon(LuminaWrapper.GetItemIconID(GIL_ITEM_ID)).GetWrapOrEmpty();
        var gilPillWidth = (paddingX * 2) + (20f * GlobalUIScale) + gilTextWidth + (4f * GlobalUIScale) + gilUnitWidth;

        var gilPillPos = new Vector2(curX, curY);
        drawList.AddRectFilled(gilPillPos, gilPillPos + new Vector2(gilPillWidth, pillHeight), pillBgCol, 4f     * GlobalUIScale);
        drawList.AddRect(gilPillPos, gilPillPos       + new Vector2(gilPillWidth, pillHeight), pillBorderCol, 4f * GlobalUIScale);

        var gilDrawX    = curX + paddingX;
        var iconSize    = 14f                                      * GlobalUIScale;
        var iconOffsetY = (pillHeight - iconSize)                  / 2f;
        var textOffsetY = (pillHeight - ImGui.GetTextLineHeight()) / 2f;

        drawList.AddImage(gilIcon.Handle, new(gilDrawX, curY + iconOffsetY), new(gilDrawX + iconSize, curY + iconOffsetY + iconSize));
        gilDrawX += iconSize + (4f * GlobalUIScale);

        drawList.AddText(new Vector2(gilDrawX, curY + textOffsetY), ImGui.GetColorU32(ImGuiCol.Text), gilText);
        gilDrawX += gilTextWidth + (4f * GlobalUIScale);

        using (FontManager.Instance().UIFont60.Push())
        {
            var unitTextHeight = ImGui.GetTextLineHeight();
            drawList.AddText
                (new Vector2(gilDrawX, curY + ((pillHeight - unitTextHeight) / 2f) + (1f * GlobalUIScale)), ImGui.GetColorU32(ImGuiCol.TextDisabled), gilUnit);
        }

        curX += gilPillWidth + (8f * GlobalUIScale);

        // 2. 绘制使用券 Pill
        var   ticketText      = tickets.ToChineseString();
        var   ticketUnit      = LuminaWrapper.GetItemName(TELEPORT_TICKET_ITEM_ID);
        var   ticketTextWidth = ImGui.CalcTextSize(ticketText).X;
        float ticketUnitWidth;
        using (FontManager.Instance().UIFont60.Push()) ticketUnitWidth = ImGui.CalcTextSize(ticketUnit).X;

        var ticketIcon      = DService.Instance().Texture.GetFromGameIcon(LuminaWrapper.GetItemIconID(TELEPORT_TICKET_ITEM_ID)).GetWrapOrEmpty();
        var ticketPillWidth = (paddingX * 2) + (20f * GlobalUIScale) + ticketTextWidth + (4f * GlobalUIScale) + ticketUnitWidth;

        var ticketPillPos = new Vector2(curX, curY);
        drawList.AddRectFilled(ticketPillPos, ticketPillPos + new Vector2(ticketPillWidth, pillHeight), pillBgCol, 4f     * GlobalUIScale);
        drawList.AddRect(ticketPillPos, ticketPillPos       + new Vector2(ticketPillWidth, pillHeight), pillBorderCol, 4f * GlobalUIScale);

        var ticketDrawX = curX + paddingX;
        drawList.AddImage(ticketIcon.Handle, new Vector2(ticketDrawX, curY + iconOffsetY), new Vector2(ticketDrawX + iconSize, curY + iconOffsetY + iconSize));
        ticketDrawX += iconSize + (4f * GlobalUIScale);

        drawList.AddText(new Vector2(ticketDrawX, curY + textOffsetY), ImGui.GetColorU32(ImGuiCol.Text), ticketText);
        ticketDrawX += ticketTextWidth + (4f * GlobalUIScale);

        using (FontManager.Instance().UIFont60.Push())
        {
            var unitTextHeight = ImGui.GetTextLineHeight();
            drawList.AddText
                (new Vector2(ticketDrawX, curY + ((pillHeight - unitTextHeight) / 2f) + (1f * GlobalUIScale)), ImGui.GetColorU32(ImGuiCol.TextDisabled), ticketUnit);
        }

        ImGui.SetCursorScreenPos(startPos + new Vector2(0, height + (4f * GlobalUIScale)));
    }

    private static float DrawAetheryteIcon
    (
        ImDrawListPtr   drawList,
        AetheryteRecord aetheryte,
        Vector2         startPos,
        float           itemHeight,
        float           padding,
        float           animOffset
    )
    {
        var iconID = (uint)(LuminaGetter.GetRow<GeneralAction>(7)?.Icon ?? 60752);
        if (aetheryte.Group == 255)
            iconID = 60752;
        else if (aetheryte.IsAetheryte)
            iconID = 60453;
        else
            iconID = 60430;

        var texWrap       = DService.Instance().Texture.GetFromGameIcon(new(iconID)).GetWrapOrEmpty();
        var contentStartX = startPos.X + padding + 6f + animOffset;
        var iconSize      = 24f * GlobalUIScale;
        var iconY         = startPos.Y + ((itemHeight - iconSize) / 2f);

        if (texWrap.Handle != nint.Zero)
            drawList.AddImage(texWrap.Handle, new Vector2(contentStartX, iconY), new Vector2(contentStartX + iconSize, iconY + iconSize));

        return contentStartX + iconSize + padding + 4f;
    }

    private float DrawAetheryteIndicators
    (
        ImDrawListPtr   drawList,
        AetheryteRecord aetheryte,
        float           startX,
        float           titleY
    )
    {
        var curX      = startX;
        var hasRemark = config.Remarks.ContainsKey(GetConfigKey(aetheryte));

        if (hasRemark)
        {
            var remarkIcon     = FontAwesomeIcon.Edit.ToIconString();
            var remarkIconSize = ImGui.CalcTextSize(remarkIcon);
            drawList.AddText(new Vector2(curX, titleY), ImGui.GetColorU32(ImGuiCol.TextDisabled), remarkIcon);
            curX += remarkIconSize.X + (6f * GlobalUIScale);
        }

        var hasCustomPos = config.Positions.ContainsKey(GetConfigKey(aetheryte));

        if (hasCustomPos)
        {
            var posIcon     = FontAwesomeIcon.MapPin.ToIconString();
            var posIconSize = ImGui.CalcTextSize(posIcon);
            drawList.AddText(new Vector2(curX, titleY), ImGui.GetColorU32(KnownColor.Orange.ToVector4().ToUInt()), posIcon);
            curX += posIconSize.X + (6f * GlobalUIScale);
        }

        return curX;
    }

    private void DrawAetheryteStateIcon
    (
        AetheryteRecord aetheryte,
        float           curX,
        float           titleY
    )
    {
        SeString? iconStr = null;

        switch (aetheryte.State)
        {
            case AetheryteRecordState.Home:
                iconStr = HomeChar;
                break;
            case AetheryteRecordState.Free:
            case AetheryteRecordState.FreePS:
                iconStr = FreeChar;
                break;
            case AetheryteRecordState.Favorite:
                iconStr = FavoriteChar;
                break;
            default:
                if (config.Favorites.Contains(aetheryte.RowID))
                    iconStr = FavoriteChar;
                break;
        }

        if (iconStr != null)
        {
            ImGui.SetCursorScreenPos(new(curX, titleY));
            ImGuiHelpers.SeStringWrapped(iconStr.Encode());
        }
    }

    private void DrawAetheryteContextMenu(AetheryteRecord aetheryte)
    {
        using var context = ImRaii.ContextPopupItem("AetheryteContextPopup");
        if (!context) return;

        ImGui.TextUnformatted($"{aetheryte.Name}");

        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.MenuItem(Lang.Get("Favorite"), string.Empty, config.Favorites.Contains(aetheryte.RowID)))
        {
            if (!config.Favorites.Add(aetheryte.RowID))
                config.Favorites.Remove(aetheryte.RowID);

            RefreshFavoritesInfo();
            config.Save(this);
            RefreshDefaultOverlayItems();
        }

        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted(Lang.Get("Note"));

        var hasRemark = config.Remarks.TryGetValue(GetConfigKey(aetheryte), out var remark);
        var input     = hasRemark ? remark : string.Empty;
        ImGui.SetNextItemWidth(Math.Max(150f * GlobalUIScale, ImGui.CalcTextSize(aetheryte.Name).X));

        if (ImGui.InputText("###Note", ref input, 128))
        {
            if (string.IsNullOrWhiteSpace(input))
                config.Remarks.Remove(GetConfigKey(aetheryte));
            else
                config.Remarks[GetConfigKey(aetheryte)] = input;
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.Save(this);
            recordMatcher?.Dispose();
            recordMatcher = CreateRecordMatcher();
        }

        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted(Lang.Get("Position"));

        var hasPosition = config.Positions.TryGetValue(GetConfigKey(aetheryte), out var position);
        using (FontManager.Instance().UIFont60.Push())
            ImGui.TextUnformatted($"{(hasPosition ? position : aetheryte.Position):F1}");

        if (ImGui.MenuItem(Lang.Get("BetterTeleport-RedirectedToCurrentPos")))
        {
            config.Positions[GetConfigKey(aetheryte)] = Control.GetLocalPlayer()->Position;
            config.Save(this);
        }

        using (ImRaii.Disabled(!config.Positions.ContainsKey(GetConfigKey(aetheryte))))
        {
            if (ImGui.MenuItem($"{Lang.Get("Clear")}###DeleteRedirected"))
            {
                config.Positions.Remove(GetConfigKey(aetheryte));
                config.Save(this);
            }
        }
    }

    private void DrawAetheryteItem
    (
        AetheryteRecord aetheryte,
        int?            index           = null,
        bool            isSelected      = false,
        bool            drawHotkeyBadge = true,
        string?         searchText      = null
    )
    {
        if (config.HideAethernetInParty && !aetheryte.IsAetheryte && DService.Instance().PartyList.Length > 1)
            return;

        var hasRemark   = config.Remarks.TryGetValue(GetConfigKey(aetheryte), out var remark);
        var displayName = hasRemark ? remark : aetheryte.Name;
        var cost        = aetheryte.Cost;

        using var id = ImRaii.PushId($"{aetheryte}");

        var startPos   = ImGui.GetCursorScreenPos();
        var width      = ImGui.GetContentRegionAvail().X;
        var lineHeight = ImGui.GetTextLineHeight();
        var padding    = ImGui.GetStyle().ItemSpacing.X;
        var itemHeight = (lineHeight * 2.2f) + padding;

        if (ImGui.InvisibleButton("##ItemBtn", new Vector2(width, itemHeight)))
        {
            if (index.HasValue)
                hasUsedArrowKeys = false;
            HandleTeleport(aetheryte, searchText);
        }

        var isHovered = ImGui.IsItemHovered() && !hasUsedArrowKeys;
        var isActive  = ImGui.IsItemActive();

        // 统一右键菜单
        DrawAetheryteContextMenu(aetheryte);

        // 统一 hover 状态进度动画
        var key = (aetheryte.RowID, aetheryte.SubIndex);
        hoverProgress.TryAdd(key, 0f);

        var targetProgress  = isHovered || isSelected ? 1f : 0f;
        var speed           = ImGui.GetIO().DeltaTime * 12f;
        var currentProgress = hoverProgress[key];

        if (Math.Abs(currentProgress - targetProgress) > 0.001f)
        {
            currentProgress    += (targetProgress - currentProgress) * Math.Min(speed, 1.0f);
            currentProgress    =  Math.Clamp(currentProgress, 0f, 1f);
            hoverProgress[key] =  currentProgress;
        }

        var animOffset      = currentProgress * 8.0f;
        var indicatorHeight = itemHeight      * 0.7f * currentProgress;

        var drawList    = ImGui.GetWindowDrawList();
        var baseColor   = ImGui.GetColorU32(ImGuiCol.FrameBgHovered);
        var activeColor = ImGui.GetColorU32(ImGuiCol.FrameBgActive);

        uint bgCol = 0;

        if (isActive)
            bgCol = activeColor;
        else if (isSelected)
            bgCol = ImGui.GetColorU32(ImGuiCol.HeaderActive);
        else if (currentProgress > 0.01f)
        {
            var alpha = (uint)(currentProgress * ((baseColor >> 24) & 0xFF));
            bgCol = (baseColor & 0x00FFFFFF) | (alpha << 24);
        }

        if (bgCol != 0)
        {
            drawList.AddRectFilledMultiColor
            (
                startPos,
                startPos + new Vector2(width, itemHeight),
                bgCol,
                (bgCol & 0x00FFFFFF) | (((uint)((bgCol >> 24) * 0.5f) & 0xFF) << 24),
                (bgCol & 0x00FFFFFF) | (((uint)((bgCol >> 24) * 0.5f) & 0xFF) << 24),
                bgCol
            );
        }

        if (currentProgress > 0.01f)
        {
            var indicatorColor = ImGui.GetColorU32(ImGuiCol.CheckMark);

            switch (aetheryte.State)
            {
                case AetheryteRecordState.Home:
                    indicatorColor = 0xFF00A5FF;
                    break;
                case AetheryteRecordState.Favorite:
                    indicatorColor = 0xFF00D7FF;
                    break;
            }

            var centerY = startPos.Y + (itemHeight / 2);
            drawList.AddRectFilled
            (
                startPos with { Y = centerY - (indicatorHeight / 2) },
                new Vector2(startPos.X + 3f, centerY + (indicatorHeight / 2)),
                indicatorColor,
                1.5f
            );
        }

        var contentStartX = DrawAetheryteIcon(drawList, aetheryte, startPos, itemHeight, padding, animOffset);

        var titleY = startPos.Y + 4f;
        drawList.AddText(new Vector2(contentStartX, titleY), ImGui.GetColorU32(ImGuiCol.Text), displayName);

        var nameWidth = ImGui.CalcTextSize(displayName).X;
        var curX      = contentStartX + nameWidth + (8f * GlobalUIScale);

        curX = DrawAetheryteIndicators(drawList, aetheryte, curX, titleY);
        DrawAetheryteStateIcon(aetheryte, curX, titleY);

        using (FontManager.Instance().UIFont80.Push())
        {
            var subY = titleY + lineHeight + (2f * GlobalUIScale);
            drawList.AddText(new Vector2(contentStartX, subY), ImGui.GetColorU32(ImGuiCol.TextDisabled), aetheryte.GetZone().ExtractPlaceName());
        }

        var rightEndX = startPos.X + width - padding - 6f - animOffset;

        if (index.HasValue && drawHotkeyBadge)
        {
            string? hotkeyLabel = null;
            if (index.Value is >= 0 and < 8)
                hotkeyLabel = $"Ctrl+{index.Value + 1}";
            else if (index.Value == 8)
                hotkeyLabel = "Ctrl+9";

            if (hotkeyLabel != null)
            {
                var badgeSize   = ImGui.CalcTextSize(hotkeyLabel);
                var badgeWidth  = badgeSize.X + 8f;
                var badgeHeight = badgeSize.Y + 4f;
                var badgePos    = new Vector2(rightEndX - badgeWidth, startPos.Y + ((itemHeight - badgeHeight) / 2));

                var badgeBgCol = ImGui.GetColorU32(ImGuiCol.FrameBg);
                drawList.AddRectFilled(badgePos, badgePos + new Vector2(badgeWidth, badgeHeight), badgeBgCol, 4f);
                drawList.AddRect(badgePos, badgePos       + new Vector2(badgeWidth, badgeHeight), ImGui.GetColorU32(ImGuiCol.Border), 4f);

                drawList.AddText(badgePos + new Vector2(4f, 2f), ImGui.GetColorU32(ImGuiCol.TextDisabled), hotkeyLabel);

                rightEndX -= badgeWidth + padding;
            }
        }

        var costText    = $"{cost}";
        var costStrFull = $"{costText}\uE049";
        var costSize    = ImGui.CalcTextSize(costStrFull);
        var costPos     = new Vector2(rightEndX - costSize.X, startPos.Y + ((itemHeight - costSize.Y) / 2));

        drawList.AddText(costPos, ImGui.GetColorU32(ImGuiCol.Text), costStrFull);

        if (isHovered && !isSearchingInputting)
        {
            if (index.HasValue)
                selectedIndex = index.Value;

            if (!ImGui.IsPopupOpen("AetheryteContextPopup"))
            {
                hoveredAetheryte = aetheryte;
                if (!index.HasValue)
                    isNeedToLoseFocusSearchBar = true;
            }
        }

        ImGui.SetCursorScreenPos(startPos + new Vector2(0, itemHeight + (index.HasValue ? 2f : 3f)));
    }

    private void DrawGeneralSettings(float width = -1f)
    {
        var defaultPage = (int)config.DefaultPage;
        var options     = new[] { Lang.Get("BetterTeleport-PageSearch"), Lang.Get("BetterTeleport-PageFull") };

        if (width > 0)
            ImGui.SetNextItemWidth(width);
        if (ImGui.Combo
            (
                $"{Lang.Get("BetterTeleport-DefaultPage")}###BetterTeleportDefaultPageCombo",
                ref defaultPage,
                options,
                options.Length
            ))
        {
            config.DefaultPage = (PageType)defaultPage;
            config.Save(this);
        }

        if (ImGui.Checkbox(Lang.Get("BetterTeleport-HideAethernetInParty"), ref config.HideAethernetInParty))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("BetterTeleport-FocusSearchOnOpen"), ref config.FocusSearchOnOpen))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("BetterTeleport-CloseOnLoseFocus"), ref config.CloseOnLoseFocus))
            config.Save(this);
    }

    #region 常量

    private const uint GIL_ITEM_ID             = 1;
    private const uint TELEPORT_TICKET_ITEM_ID = 7569;

    private static readonly SeString HomeChar     = new SeStringBuilder().AddIcon(BitmapFontIcon.OrangeDiamond).Build();
    private static readonly SeString FreeChar     = new SeStringBuilder().AddIcon(BitmapFontIcon.GoldStar).Build();
    private static readonly SeString FavoriteChar = new SeStringBuilder().AddIcon(BitmapFontIcon.SilverStar).Build();

    #endregion
}
