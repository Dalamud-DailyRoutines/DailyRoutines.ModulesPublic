using System.Numerics;
using DailyRoutines.Extensions;
using DailyRoutines.Internal;
using DailyRoutines.Manager;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace DailyRoutines.ModulesPublic.BetterTeleport;

public unsafe partial class BetterTeleport
{
    private const uint GIL_ITEM_ID             = 1;
    private const uint TELEPORT_TICKET_ITEM_ID = 7569;

    private static readonly SeString HomeChar     = new SeStringBuilder().AddIcon(BitmapFontIcon.OrangeDiamond).Build();
    private static readonly SeString FreeChar     = new SeStringBuilder().AddIcon(BitmapFontIcon.GoldStar).Build();
    private static readonly SeString FavoriteChar = new SeStringBuilder().AddIcon(BitmapFontIcon.SilverStar).Build();

    private AetheryteRecord? hoveredAetheryte;
    private AetheryteRecord? lastHoveredAetheryte;
    private AetheryteRecord? pinnedAetheryte;
    private float            hoverStartTime;

    private Vector3 contextMenuTargetPos;
    private uint    contextMenuTargetZone;

    private void DrawHoveredTooltip()
    {
        if (hoveredAetheryte != null && PluginConfig.Instance().ConflictKeyBinding.IsPressed())
            pinnedAetheryte = hoveredAetheryte;

        if (pinnedAetheryte != null)
        {
            ImGui.SetNextWindowBgAlpha(0.8f);

            if (ImGui.Begin
                (
                    "###PinnedAetheryteMap",
                    ImGuiWindowFlags.NoDecoration     |
                    ImGuiWindowFlags.AlwaysAutoResize |
                    ImGuiWindowFlags.NoSavedSettings
                ))
            {
                DrawAetheryteMap(DService.Instance().Texture.GetFromGame(pinnedAetheryte.GetMap().GetTexturePath()), pinnedAetheryte, true);

                if (!ImGui.IsWindowFocused() && !ImGui.IsPopupOpen("BetterTeleport_Map_ContextMenu"))
                {
                    pinnedAetheryte = null;
                    config.Save(this);
                }

                ImGui.End();
            }
        }

        if (hoveredAetheryte == null)
        {
            lastHoveredAetheryte = null;
            return;
        }

        if (pinnedAetheryte != null && hoveredAetheryte.RowID == pinnedAetheryte.RowID)
            return;

        if (lastHoveredAetheryte != hoveredAetheryte)
        {
            lastHoveredAetheryte = hoveredAetheryte;
            hoverStartTime       = (float)ImGui.GetTime();
        }

        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero))
        using (ImRaii.PushColor(ImGuiCol.PopupBg, ImGui.GetColorU32(ImGuiCol.WindowBg)))
        using (ImRaii.Tooltip())
            DrawAetheryteMap(DService.Instance().Texture.GetFromGame(hoveredAetheryte.GetMap().GetTexturePath()), hoveredAetheryte, false);
    }

    private void DrawAetheryteMap
    (
        ISharedImmediateTexture tex,
        AetheryteRecord         aetheryte,
        bool                    isPinned
    )
    {
        var drawList = ImGui.GetWindowDrawList();
        var warp     = tex.GetWrapOrEmpty();
        if (warp.Handle == nint.Zero || warp.Width < 64 || warp.Height < 64) return;

        if (pinnedAetheryte != null && ImGui.IsWindowHovered() && ImGui.GetIO().MouseWheel != 0)
        {
            config.MapZoom += ImGui.GetIO().MouseWheel * 0.1f;
            config.MapZoom =  Math.Clamp(config.MapZoom, 0.2f, 4.0f);
        }

        var widthScale = Math.Min(1f, warp.Width / 2048f);
        var imageSize  = ScaledVector2(384f      * widthScale * config.MapZoom);
        var scale      = imageSize.X / 2048f;

        if (scale <= 0.001f) return;

        if (!isPinned)
            ImGuiOm.ScaledDummy(0f, 2f);

        var hint     = isPinned ? Lang.Get("BetterTeleport-MapHint-Zoom") : Lang.Get("BetterTeleport-MapHint-Pin");
        var hintSize = ImGui.CalcTextSize(hint);
        if (imageSize.X > hintSize.X)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ((imageSize.X - hintSize.X) / 2));
        ImGui.TextDisabled(hint);

        var orig = ImGui.GetCursorScreenPos();

        ImGui.Image(warp.Handle, imageSize);

        if (isPinned &&
            ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            var mousePos   = ImGui.GetMousePos();
            var relPos     = mousePos - orig;
            var texturePos = relPos / scale;
            var worldPos   = PositionHelper.TextureToWorld(texturePos, aetheryte.GetMap());

            var nearest = AllRecords.Where(x => x.GetZone().RowId == aetheryte.GetZone().RowId)
                                    .MinBy(x => Vector2.DistanceSquared(new(x.Position.X, x.Position.Z), worldPos));

            if (nearest != null)
            {
                contextMenuTargetZone = nearest.ZoneID;
                contextMenuTargetPos  = worldPos.ToVector3(nearest.Position.Y);
                ImGui.OpenPopup("BetterTeleport_Map_ContextMenu");
            }
        }

        using (var popup = ImRaii.Popup("BetterTeleport_Map_ContextMenu"))
        {
            if (popup)
            {
                if (ImGui.MenuItem(Lang.Get("BetterTeleport-TeleportToThisPosition")))
                {
                    if (GameState.TerritoryType != contextMenuTargetZone || IsWithPermission())
                    {
                        TaskHelper.Enqueue(() => MovementManager.Instance().TPSmart_BetweenZone(contextMenuTargetZone, contextMenuTargetPos));
                        TaskHelper.Enqueue
                        (() =>
                            {
                                if (MovementManager.Instance().IsManagerBusy || DService.Instance().ObjectTable.LocalPlayer == null)
                                    return false;

                                MovementManager.Instance().TPGround();
                                if (DService.Instance().Condition.IsBetweenAreas || DService.Instance().Condition[ConditionFlag.Jumping]) return false;

                                return true;
                            }
                        );
                    }
                    else
                    {
                        TaskHelper.Enqueue(() => MovementManager.Instance().TeleportNearestAetheryte(contextMenuTargetPos, contextMenuTargetZone));
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
        }

        if (ImGui.IsPopupOpen("BetterTeleport_Map_ContextMenu"))
        {
            var texFlag = DService.Instance().Texture.GetFromGameIcon(new(60561)).GetWrapOrEmpty();

            if (texFlag.Handle != nint.Zero)
            {
                var flagPos       = PositionHelper.WorldToTexture(contextMenuTargetPos, aetheryte.GetMap()) * scale;
                var flagCenterPos = orig + flagPos;
                var flagSize      = ScaledVector2(24f * config.MapZoom);
                var flagHalfSize  = flagSize / 2;

                drawList.AddImage(texFlag.Handle, flagCenterPos - flagHalfSize, flagCenterPos + flagHalfSize);
            }
        }

        drawList.AddRect(orig, orig + imageSize, ImGui.GetColorU32(ImGuiCol.Border), 0f, ImDrawFlags.None, 2f);

        var mapID    = aetheryte.GetMap().RowId;
        var siblings = AllRecords.Where(x => x.GetMap().RowId == mapID).ToList();

        var texAetheryte = DService.Instance().Texture.GetFromGameIcon(new(60453)).GetWrapOrEmpty();
        var texAethernet = DService.Instance().Texture.GetFromGameIcon(new(60430)).GetWrapOrEmpty();

        var sizeNormal = ScaledVector2(18f * config.MapZoom);
        var sizeTarget = ScaledVector2(24f * config.MapZoom);

        foreach (var record in siblings)
        {
            if (record.RowID == aetheryte.RowID) continue;

            var recordPos = config.Positions.TryGetValue(GetConfigKey(record), out var redirected) ? redirected : record.Position;
            var pos       = PositionHelper.WorldToTexture(recordPos, record.GetMap()) * scale;
            var centerPos = orig + pos;

            var texture  = record.IsAetheryte ? texAetheryte : texAethernet;
            var halfSize = sizeNormal / 2;

            drawList.AddImage(texture.Handle, centerPos - halfSize, centerPos + halfSize, Vector2.Zero, Vector2.One, 0xCCFFFFFF);

            if (isPinned)
            {
                var min = centerPos - halfSize;
                var max = centerPos + halfSize;

                if (ImGui.IsMouseHoveringRect(min, max))
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    ImGui.SetTooltip(record.Name);

                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    {
                        HandleTeleport(record);
                        pinnedAetheryte = null;
                    }
                }
            }
        }

        {
            var recordPos = config.Positions.TryGetValue(GetConfigKey(aetheryte), out var redirected) ? redirected : aetheryte.Position;
            var pos       = PositionHelper.WorldToTexture(recordPos, aetheryte.GetMap()) * scale;
            var centerPos = orig + pos;

            var time     = (float)ImGui.GetTime();
            var animTime = time - hoverStartTime;

            var pulse = ((float)Math.Sin(time * 10f) * 0.2f) + 1.0f;

            var pingRadius = animTime * 100f % 60f;
            var pingAlpha  = 1.0f - (pingRadius / 60f);

            if (pingRadius > 0 && pingAlpha > 0)
            {
                var pingColor = ImGui.GetColorU32(ImGuiCol.CheckMark);
                pingColor = (pingColor & 0x00FFFFFF) | ((uint)(pingAlpha * 255) << 24);
                drawList.AddCircle(centerPos, pingRadius, pingColor, 32, 2f);
            }

            drawList.AddCircleFilled(centerPos, 8f * pulse * GlobalUIScale, ImGui.GetColorU32(ImGuiCol.CheckMark, 0.5f));

            var texture  = aetheryte.IsAetheryte ? texAetheryte : texAethernet;
            var halfSize = sizeTarget / 2;
            drawList.AddImage(texture.Handle, centerPos - halfSize, centerPos + halfSize);

            if (isPinned)
            {
                var min = centerPos - halfSize;
                var max = centerPos + halfSize;

                if (ImGui.IsMouseHoveringRect(min, max))
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    ImGui.SetTooltip(aetheryte.Name);

                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    {
                        HandleTeleport(aetheryte);
                        pinnedAetheryte = null;
                    }
                }
            }

            var text     = config.Remarks.TryGetValue(GetConfigKey(aetheryte), out var remark) ? remark : aetheryte.Name;
            var textSize = ImGui.CalcTextSize(text);
            var padding  = ScaledVector2(2f, 3f);
            var textPos  = centerPos - new Vector2(textSize.X / 2, (20f * GlobalUIScale) + textSize.Y);

            var minPos = orig             + padding;
            var maxPos = orig + imageSize - padding - textSize;

            if (minPos.X < maxPos.X)
                textPos.X = Math.Clamp(textPos.X, minPos.X, maxPos.X);

            if (minPos.Y < maxPos.Y)
                textPos.Y = Math.Clamp(textPos.Y, minPos.Y, maxPos.Y);

            drawList.AddRectFilled(textPos - padding, textPos + textSize + padding, 0x80000000, 4f);
            drawList.AddText(textPos, KnownColor.LightSkyBlue.ToVector4().ToUInt(), text);
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

    private static void DrawAetheryteStateIcon
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
        }

        if (iconStr != null)
        {
            ImGui.SetCursorScreenPos(new Vector2(curX, titleY - (2f * GlobalUIScale)));
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
            config.Save(this);

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
        int?            index      = null,
        bool            isSelected = false
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
            HandleTeleport(aetheryte);
        }

        var isHovered = ImGui.IsItemHovered();
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

        if (index.HasValue)
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

#if DEBUG
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right) && !index.HasValue)
        {
            var localPos = Control.GetLocalPlayer()->Position;
            ImGui.SetClipboardText
            (
                $"// {aetheryte.Name}\n" +
                $"[{aetheryte.RowID}] = new({localPos.X:F2}f, {localPos.Y + 0.1f:F2}f, {localPos.Z:F2}f),"
            );
        }
#endif

        if (isHovered)
        {
            if (index.HasValue)
            {
                var currentMousePos = ImGui.GetMousePos();

                if (!hasUsedArrowKeys) selectedIndex = index.Value;
                else if (currentMousePos != lastMousePos)
                {
                    hasUsedArrowKeys = false;
                    selectedIndex    = index.Value;
                }

                lastMousePos = currentMousePos;
            }

            if (!ImGui.IsPopupOpen("AetheryteContextPopup"))
            {
                hoveredAetheryte = aetheryte;
                if (!index.HasValue)
                    isNeedToLoseFocusSearchBar = true;
            }
        }

        ImGui.SetCursorScreenPos(startPos + new Vector2(0, itemHeight + (index.HasValue ? 2f : 3f)));
    }
}
