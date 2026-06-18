using System.Numerics;
using DailyRoutines.Extensions;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.CrossDCPartyFinder;

public partial class CrossDCPartyFinder
{
    private readonly Dictionary<string, float> cardHeights = new();

    protected override unsafe void OverlayUI()
    {
        var addon = LookingForGroup;

        if (addon == null)
        {
            Overlay.IsOpen = false;
            cardHeights.Clear();
            return;
        }

        if (selectedDataCenter == LocatedDataCenter)
        {
            cardHeights.Clear();
            return;
        }

        var nodeInfo0  = addon->GetNodeById(38)->GetNodeState();
        var nodeInfo1  = addon->GetNodeById(31)->GetNodeState();
        var nodeInfo2  = addon->GetNodeById(41)->GetNodeState();
        var size       = nodeInfo0.Size + new Vector2(0, nodeInfo1.Height + nodeInfo2.Height);
        var sizeOffset = new Vector2(4, 4);
        ImGui.SetNextWindowPos(new(addon->GetNodeById(31)->ScreenX - 4f, addon->GetNodeById(31)->ScreenY));
        ImGui.SetNextWindowSize(size + (2 * sizeOffset));

        if (!ImGui.Begin
            (
                "###CrossDCPartyFinder_PartyListWindow",
                ImGuiWindowFlags.NoTitleBar            |
                ImGuiWindowFlags.NoResize              |
                ImGuiWindowFlags.NoDocking             |
                ImGuiWindowFlags.NoBringToFrontOnFocus |
                ImGuiWindowFlags.NoCollapse            |
                ImGuiWindowFlags.NoScrollbar           |
                ImGuiWindowFlags.NoScrollWithMouse
            )) return;

        var isNeedToResetY = false;

        using (ImRaii.Disabled(isNeedToDisable))
        {
            var totalPages     = (int)Math.Ceiling(listingsDisplay.Count / (float)config.PageSize);
            var availableWidth = ImGui.GetContentRegionAvail().X;
            var frameHeight    = ImGui.GetFrameHeight();

            var closeBtnWidth   = frameHeight;
            var pageTextWidth   = 72f * GlobalUIScale;
            var paginationWidth = (frameHeight * 4f) + pageTextWidth + (6f                    * GlobalUIScale);
            var searchWidth     = availableWidth     - closeBtnWidth - paginationWidth - (12f * GlobalUIScale);

            ImGui.SetNextItemWidth(searchWidth);
            ImGui.InputTextWithHint("###SearchString", Lang.Get("PleaseSearch"), ref currentSeach, 128);

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                isNeedToResetY = true;
                SendRequestDynamic();
            }

            ImGui.SameLine(0, 6f * GlobalUIScale);

            using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(2f * GlobalUIScale, 0)))
            using (ImRaii.PushColor(ImGuiCol.Button, KnownColor.DarkSlateGray.ToVector4()))
            using (ImRaii.PushColor(ImGuiCol.ButtonHovered, KnownColor.SlateGray.ToVector4()))
            using (ImRaii.PushColor(ImGuiCol.ButtonActive, KnownColor.DimGray.ToVector4()))
            {
                if (ImGuiOm.ButtonIcon("PageFirst", FontAwesomeIcon.AngleDoubleLeft, string.Empty, true))
                {
                    isNeedToResetY = true;
                    currentPage    = 0;
                }

                ImGui.SameLine();

                if (ImGuiOm.ButtonIcon("PagePrev", FontAwesomeIcon.AngleLeft, string.Empty, true))
                {
                    isNeedToResetY = true;
                    currentPage    = Math.Max(0, currentPage - 1);
                }

                ImGui.SameLine();

                var pageText     = $" {currentPage + 1}/{Math.Max(1, totalPages)} ";
                var pageTextSize = ImGui.CalcTextSize(pageText);
                var capsuleSize  = new Vector2(pageTextSize.X + (12f * GlobalUIScale), frameHeight);
                var capsulePos   = ImGui.GetCursorScreenPos();
                var drawList     = ImGui.GetWindowDrawList();

                drawList.AddRectFilled
                (
                    capsulePos,
                    capsulePos + capsuleSize,
                    ImGui.GetColorU32(ImGuiCol.FrameBg),
                    capsuleSize.Y * 0.5f
                );
                drawList.AddRect
                (
                    capsulePos,
                    capsulePos + capsuleSize,
                    ImGui.GetColorU32(ImGuiCol.Border),
                    capsuleSize.Y * 0.5f,
                    ImDrawFlags.None,
                    1f
                );
                drawList.AddText
                (
                    capsulePos + new Vector2(6f * GlobalUIScale, (capsuleSize.Y - pageTextSize.Y) * 0.5f),
                    ImGui.GetColorU32(ImGuiCol.Text),
                    pageText
                );

                ImGui.InvisibleButton("###PageIndicator", capsuleSize);
                ImGuiOm.TooltipHover($"{listingsDisplay.Count} 条结果");

                ImGui.SameLine();

                if (ImGuiOm.ButtonIcon("PageNext", FontAwesomeIcon.AngleRight, string.Empty, true))
                {
                    isNeedToResetY = true;
                    currentPage    = Math.Min(totalPages - 1, currentPage + 1);
                }

                ImGui.SameLine();

                if (ImGuiOm.ButtonIcon("PageLast", FontAwesomeIcon.AngleDoubleRight, string.Empty, true))
                {
                    isNeedToResetY = true;
                    currentPage    = Math.Max(0, totalPages - 1);
                }
            }

            ImGui.SameLine(0, 6f * GlobalUIScale);

            using (ImRaii.PushColor(ImGuiCol.Button, KnownColor.DarkRed.ToVector4()))
            using (ImRaii.PushColor(ImGuiCol.ButtonHovered, KnownColor.Firebrick.ToVector4()))
            using (ImRaii.PushColor(ImGuiCol.ButtonActive, KnownColor.Maroon.ToVector4()))
            {
                if (ImGuiOm.ButtonIcon("Close", FontAwesomeIcon.Times, "关闭", true))
                {
                    selectedDataCenter = LocatedDataCenter;

                    foreach (var x in checkboxNodes)
                        x.Value.IsChecked = x.Key == LocatedDataCenter;

                    AgentId.LookingForGroup.SendEvent(1, 17);
                    isNeedToDisable = false;
                }
            }
        }

        var contentSize = size - new Vector2(0, ImGui.GetFrameHeightWithSpacing());

        using (var child = ImRaii.Child("###ContentChild", contentSize, false, ImGuiWindowFlags.NoBackground))
        {
            if (child)
            {
                if (isNeedToDisable)
                    DrawCenteredState(FontAwesomeIcon.SyncAlt, "正在获取招募数据中,请稍候...", true);
                else if (listingsDisplay.Count == 0)
                    DrawCenteredState(FontAwesomeIcon.InfoCircle, "当前大区暂无招募信息", false);
                else
                    DrawPartyFinderList(isNeedToResetY);

                ImGuiOm.ScaledDummy(8f);
            }
        }

        ImGui.End();
    }

    private void DrawPartyFinderList(bool isNeedToResetY)
    {
        if (isNeedToResetY)
            ImGui.SetScrollHereY();

        var startIndex = currentPage * config.PageSize;
        var pageItems  = listingsDisplay.Skip(startIndex).Take(config.PageSize).ToList();

        pageItems.ForEach(x => Task.Run(async () => await x.RequestAsync(), cancelSource.Token).ConfigureAwait(false));

        var availableWidth = ImGui.GetContentRegionAvail().X;
        foreach (var listing in pageItems)
            DrawListingCard(listing, availableWidth);
    }

    private void DrawListingCard(PartyFinderList.PartyFinderListing listing, float availableWidth)
    {
        using var id = ImRaii.PushId(listing.ID);

        var isDescEmpty = string.IsNullOrEmpty(listing.Description);
        var displayDesc = isDescEmpty ? $"({LuminaWrapper.GetAddonText(11100)})" : listing.Description;

        if (!cardHeights.TryGetValue(listing.ID, out var cardHeight))
        {
            var descLineCount = 0;
            using (FontManager.Instance().UIFont90.Push())
                descLineCount = (int)MathF.Ceiling(ImGui.CalcTextSize(listing.Description).X / availableWidth);

            cardHeight = (4 + MathF.Max(1, descLineCount)) * ImGui.GetTextLineHeightWithSpacing();
        }

        var rightContentWidth = 230f * GlobalUIScale;
        var jobIconSize       = new Vector2(ImGui.GetTextLineHeight() * 1.15f);
        var categoryIconSize  = new Vector2(ImGui.GetTextLineHeight() * 1.25f);
        var drawList          = ImGui.GetWindowDrawList();

        var rounding        = 10f * GlobalUIScale;
        var borderThickness = 1f  * GlobalUIScale;
        var padX            = 10f * GlobalUIScale;
        var padY            = 8f  * GlobalUIScale;
        var accentWidth     = 4f  * GlobalUIScale;

        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, rounding))
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildBorderSize, borderThickness))
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(padX, padY)))
        using (ImRaii.PushColor(ImGuiCol.Border, new Vector4(0.28f, 0.32f, 0.42f, 0.65f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.07f, 0.08f, 0.11f, 0.45f)))
        using (ImRaii.Child
               (
                   $"Card_{listing.ID}",
                   new(availableWidth, cardHeight),
                   true,
                   ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
               ))
        {
            var startCursorPos = ImGui.GetCursorPos();
            var winPos         = ImGui.GetWindowPos();
            var contentStartX  = startCursorPos.X + accentWidth + (4f * GlobalUIScale);

            var accentColor = listing.MinItemLevel > 0
                                  ? KnownColor.Gold.ToUInt()
                                  : KnownColor.LightSkyBlue.ToUInt();

            drawList.AddRectFilled
            (
                winPos + new Vector2(1f,          4f * GlobalUIScale),
                winPos + new Vector2(accentWidth, cardHeight - (4f * GlobalUIScale)),
                accentColor,
                2f * GlobalUIScale
            );

            // === 第一行:标题(左) + 职业图标(右) ===
            ImGui.SetCursorPos(startCursorPos + new Vector2(accentWidth + (4f * GlobalUIScale), 0));

            var dutyName        = string.IsNullOrEmpty(listing.Duty) ? LuminaWrapper.GetAddonText(7) : listing.Duty;
            var hasCategoryIcon = DService.Instance().Texture.TryGetFromGameIcon(new(listing.CategoryIcon), out var categoryTexture);

            var titleLineHeight = categoryIconSize.Y;
            var titleStartY     = ImGui.GetCursorPosY();

            using (ImRaii.Group())
            {
                if (hasCategoryIcon)
                {
                    ImGui.SetCursorPosY(titleStartY);
                    ImGui.Image(categoryTexture.GetWrapOrEmpty().Handle, categoryIconSize);
                    ImGui.SameLine(0, 6f * GlobalUIScale);
                }

                using (FontManager.Instance().UIFont120.Push())
                {
                    var textHeight = ImGui.GetTextLineHeight();
                    ImGui.SetCursorPosY(titleStartY + ((titleLineHeight - textHeight) / 2f));
                    ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), dutyName);
                }

                if (listing.MinItemLevel > 0)
                {
                    ImGui.SameLine(0, 8f * GlobalUIScale);

                    using (FontManager.Instance().UIFont90.Push())
                    {
                        var ilvlText      = $"装等 {listing.MinItemLevel}";
                        var ilvlTextSize  = ImGui.CalcTextSize(ilvlText);
                        var ilvlPadX      = 6f * GlobalUIScale;
                        var ilvlPadY      = 2f * GlobalUIScale;
                        var ilvlCapsule   = ilvlTextSize + new Vector2(ilvlPadX * 2, ilvlPadY * 2);
                        var ilvlScreenPos = ImGui.GetCursorScreenPos();

                        ilvlScreenPos.Y += (titleLineHeight - ilvlCapsule.Y) * 0.5f;

                        drawList.AddRectFilled
                        (
                            ilvlScreenPos,
                            ilvlScreenPos + ilvlCapsule,
                            KnownColor.DarkGoldenrod.ToUInt(),
                            ilvlCapsule.Y * 0.5f
                        );
                        drawList.AddText
                        (
                            ilvlScreenPos + new Vector2(ilvlPadX, ilvlPadY),
                            KnownColor.Gold.ToUInt(),
                            ilvlText
                        );
                    }
                }
            }

            // 右侧:职业图标
            ImGui.SameLine();
            ImGui.SetCursorPosX(availableWidth - rightContentWidth - (8f            * GlobalUIScale));
            ImGui.SetCursorPosY(titleStartY    + ((titleLineHeight - jobIconSize.Y) / 2f));

            using (ImRaii.Group())
            {
                if (listing.Detail != null)
                {
                    var validSlotsCount = listing.Detail.Slots.Count(slot => slot.JobIcons.Count > 0);

                    if (validSlotsCount > 0)
                    {
                        var slotsWidth = (validSlotsCount * jobIconSize.X) + ((validSlotsCount - 1) * (3f * GlobalUIScale));
                        var extraSpace = rightContentWidth                 - slotsWidth;

                        if (extraSpace > 0)
                            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + extraSpace);

                        using (ImRaii.Group())
                        {
                            var slotsCount = listing.Detail.Slots.Count;
                            var drawnCount = 0;

                            for (var i = 0; i < slotsCount; i++)
                            {
                                var slot = listing.Detail.Slots[i];
                                if (slot.JobIcons.Count == 0) continue;

                                var displayIcon = slot.JobIcons.Count > 1 ? 62146 : slot.JobIcons[0];

                                if (DService.Instance().Texture.TryGetFromGameIcon(new(displayIcon), out var jobTexture))
                                {
                                    if (drawnCount > 0)
                                        ImGui.SameLine(0, 3f * GlobalUIScale);

                                    using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, 0.35f, !slot.Filled))
                                        ImGui.Image(jobTexture.GetWrapOrEmpty().Handle, jobIconSize);
                                    drawnCount++;
                                }
                            }
                        }
                    }
                    else
                    {
                        using (FontManager.Instance().UIFont90.Push())
                        {
                            const string NO_SLOTS_TEXT = "不设职业限制";
                            var          extraSpace    = rightContentWidth - ImGui.CalcTextSize(NO_SLOTS_TEXT).X;
                            if (extraSpace > 0)
                                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + extraSpace);

                            using (ImRaii.PushColor(ImGuiCol.Text, KnownColor.DarkGray.ToVector4())) ImGui.TextUnformatted(NO_SLOTS_TEXT);
                        }
                    }
                }
                else
                {
                    using (FontManager.Instance().UIFont90.Push())
                    {
                        const string LOADING_TEXT = "正在加载队伍...";
                        var          extraSpace   = rightContentWidth - ImGui.CalcTextSize(LOADING_TEXT).X;
                        if (extraSpace > 0)
                            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + extraSpace);

                        using (ImRaii.PushColor(ImGuiCol.Text, KnownColor.Gray.ToVector4())) ImGui.TextUnformatted(LOADING_TEXT);
                    }
                }
            }

            // === 第二行:玩家名(左) + 空位/时间/世界(右) ===
            ImGui.SetCursorPosX(contentStartX);
            ImGui.SetCursorPosY(titleStartY + titleLineHeight + (6f * GlobalUIScale));

            var hostStartPos = ImGui.GetCursorPos();

            using (ImRaii.PushColor(ImGuiCol.Text, KnownColor.DarkGray.ToVector4()))
            using (FontManager.Instance().UIFont80.Push())
            using (ImRaii.Group())
                ImGuiOm.RenderPlayerInfo(listing.PlayerName, listing.HomeWorldName);

            var hostActualSize = ImGui.GetItemRectSize();

            ImGui.SameLine();
            ImGui.SetCursorPos(hostStartPos);
            ImGui.InvisibleButton($"PlayerName##{listing.ID}", hostActualSize);

            if (ImGui.IsItemHovered())
            {
                var screenPos = ImGui.GetItemRectMin();
                var screenMax = ImGui.GetItemRectMax();
                drawList.AddLine(screenPos with { Y = screenMax.Y - 1f }, screenMax, KnownColor.LightGray.ToUInt());
            }

            ImGuiOm.TooltipHover($"{listing.PlayerName}@{listing.HomeWorldName}");
            ImGuiOm.ClickToCopyAndNotify($"{listing.PlayerName}@{listing.HomeWorldName}");

            // 右侧:空位/时间/世界胶囊
            using (FontManager.Instance().UIFont80.Push())
            {
                var remaining     = listing.SlotAvailable - listing.SlotFilled;
                var remainingText = $"空位 {remaining}/{listing.SlotAvailable}";
                var timeText      = $"剩余 {TimeSpan.FromSeconds(listing.TimeLeft).TotalMinutes:F0} 分";
                var worldText     = listing.CreatedAtWorldName;

                var capsulePadX    = 6f * GlobalUIScale;
                var capsulePadY    = 2f * GlobalUIScale;
                var capsuleSpacing = 4f * GlobalUIScale;

                var remainSize = ImGui.CalcTextSize(remainingText);
                var timeSize   = ImGui.CalcTextSize(timeText);
                var worldSize  = ImGui.CalcTextSize(worldText);

                var totalCapsuleWidth = remainSize.X                     +
                                        (capsulePadX * 2)                +
                                        capsuleSpacing                   +
                                        (timeSize.X + (capsulePadX * 2)) +
                                        capsuleSpacing                   +
                                        (worldSize.X + (capsulePadX * 2));
                
                ImGui.SameLine();
                ImGui.SetCursorPosX(availableWidth - totalCapsuleWidth - (8f * GlobalUIScale));

                var slotBgColor = remaining > 0 ? KnownColor.DarkGreen : KnownColor.DimGray;
                var slotFgColor = remaining > 0 ? KnownColor.LimeGreen : KnownColor.Gray;
                DrawCapsuleBadge(drawList, remainingText, remainSize, capsulePadX, capsulePadY, slotBgColor.ToVector4(), slotFgColor.ToVector4());

                ImGui.SameLine(0, capsuleSpacing);
                DrawCapsuleBadge(drawList, timeText, timeSize, capsulePadX, capsulePadY, KnownColor.SaddleBrown.ToVector4(), KnownColor.Orange.ToVector4());

                ImGui.SameLine(0, capsuleSpacing);
                DrawCapsuleBadge(drawList, worldText, worldSize, capsulePadX, capsulePadY, KnownColor.DarkSlateBlue.ToVector4(), KnownColor.LightPink.ToVector4());
            }

            // === 第三行:描述(占满全部宽度) ===
            ImGui.SetCursorPosX(contentStartX);
            ImGui.SetCursorPosY(hostStartPos.Y + hostActualSize.Y + (8f * GlobalUIScale));

            var descStartPos = ImGui.GetCursorPos();

            using (FontManager.Instance().UIFont90.Push())
            using (ImRaii.PushColor
                   (
                       ImGuiCol.Text,
                       isDescEmpty ? KnownColor.DarkGray.ToVector4() : KnownColor.LightGray.ToVector4()
                   ))
            using (ImRaii.TextWrapPos(availableWidth - (8f * GlobalUIScale)))
                ImGui.TextUnformatted(displayDesc);

            var descSize = ImGui.GetItemRectSize();

            if (!isDescEmpty)
            {
                ImGui.SameLine();
                ImGui.SetCursorPos(descStartPos);
                ImGui.InvisibleButton($"Description##{listing.ID}", descSize);

                if (ImGui.IsItemHovered())
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

                ImGuiOm.TooltipHover($"{listing.Description}");
                ImGuiOm.ClickToCopyAndNotify(listing.Description);
            }

            cardHeights[listing.ID] = descStartPos.Y + descSize.Y + (7f * GlobalUIScale);

            if (ImGui.IsMouseHoveringRect(winPos, winPos + new Vector2(availableWidth, cardHeight)))
            {
                drawList.AddRect
                (
                    winPos,
                    winPos + new Vector2(availableWidth, cardHeight),
                    accentColor,
                    rounding,
                    ImDrawFlags.None,
                    1.5f * GlobalUIScale
                );
            }
        }

        var cardSpacing = 6f * GlobalUIScale;
        ImGui.Dummy(new Vector2(0, cardSpacing));
    }

    private static void DrawCenteredState(FontAwesomeIcon icon, string text, bool withPulse)
    {
        var childSize = ImGui.GetContentRegionAvail();
        var iconStr   = icon.ToIconString();

        Vector2 iconSize;
        using (FontManager.Instance().UIFont160.Push())
            iconSize = ImGui.CalcTextSize(iconStr);

        Vector2 textSize;
        using (FontManager.Instance().UIFont90.Push())
            textSize = ImGui.CalcTextSize(text);

        var gap         = 12f * GlobalUIScale;
        var totalHeight = iconSize.Y + gap + textSize.Y;
        var startY      = MathF.Max(0f, (childSize.Y - totalHeight) * 0.5f);
        var centerX     = childSize.X * 0.5f;

        var alpha     = withPulse ? 0.4f + (0.6f * (0.5f + (0.5f * MathF.Sin((float)ImGui.GetTime() * 3f)))) : 0.85f;
        var iconColor = new Vector4(0.45f, 0.65f, 0.95f, alpha);
        var textColor = new Vector4(0.55f, 0.55f, 0.6f,  0.85f);

        ImGui.SetCursorPos(new Vector2(centerX - (iconSize.X * 0.5f), startY));
        using (FontManager.Instance().UIFont160.Push())
            ImGui.TextColored(iconColor, iconStr);

        ImGui.SetCursorPos(new Vector2(centerX - (textSize.X * 0.5f), startY + iconSize.Y + gap));
        using (FontManager.Instance().UIFont90.Push())
            ImGui.TextColored(textColor, text);
    }

    private static void DrawCapsuleBadge
    (
        ImDrawListPtr drawList,
        string        text,
        Vector2       textSize,
        float         padX,
        float         padY,
        Vector4       bgColor,
        Vector4       fgColor
    )
    {
        var capsuleSize = textSize + new Vector2(padX * 2, padY * 2);
        var screenPos   = ImGui.GetCursorScreenPos();

        drawList.AddRectFilled(screenPos, screenPos + capsuleSize, ImGui.GetColorU32(bgColor), capsuleSize.Y / 2f);
        drawList.AddText(screenPos                  + new Vector2(padX, padY), ImGui.GetColorU32(fgColor), text);

        ImGui.Dummy(capsuleSize);
    }
}
