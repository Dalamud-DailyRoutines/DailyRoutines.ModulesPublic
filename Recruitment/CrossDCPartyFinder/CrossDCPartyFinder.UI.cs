using System.Numerics;
using DailyRoutines.Extensions;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.CrossDCPartyFinder;

public partial class CrossDCPartyFinder
{
    protected override unsafe void OverlayUI()
    {
        var addon = LookingForGroup;

        if (addon == null)
        {
            Overlay.IsOpen = false;
            return;
        }

        if (selectedDataCenter == LocatedDataCenter) return;

        var nodeInfo0  = addon->GetNodeById(38)->GetNodeState();
        var nodeInfo1  = addon->GetNodeById(31)->GetNodeState();
        var nodeInfo2  = addon->GetNodeById(41)->GetNodeState();
        var size       = nodeInfo0.Size + new Vector2(0, nodeInfo1.Height + nodeInfo2.Height);
        var sizeOffset = new Vector2(4, 4);
        ImGui.SetNextWindowPos(new(addon->GetNodeById(31)->ScreenX - 4f, addon->GetNodeById(31)->ScreenY));
        ImGui.SetNextWindowSize(size + (2 * sizeOffset));

        if (ImGui.Begin
            (
                "###CrossDCPartyFinder_PartyListWindow",
                ImGuiWindowFlags.NoTitleBar            |
                ImGuiWindowFlags.NoResize              |
                ImGuiWindowFlags.NoDocking             |
                ImGuiWindowFlags.NoBringToFrontOnFocus |
                ImGuiWindowFlags.NoCollapse            |
                ImGuiWindowFlags.NoScrollbar           |
                ImGuiWindowFlags.NoScrollWithMouse
            ))
        {
            var isNeedToResetY = false;

            using (ImRaii.Disabled(isNeedToDisable))
            {
                var totalPages     = (int)Math.Ceiling(listingsDisplay.Count / (float)config.PageSize);
                var availableWidth = ImGui.GetContentRegionAvail().X;

                var closeBtnWidth    = 56f  * GlobalUIScale;
                var paginationWidth  = 144f * GlobalUIScale;
                var searchInputWidth = availableWidth - closeBtnWidth - paginationWidth - (12f * GlobalUIScale);

                ImGui.SetNextItemWidth(searchInputWidth);
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
                    if (ImGui.Button("<<"))
                    {
                        isNeedToResetY = true;
                        currentPage    = 0;
                    }

                    ImGui.SameLine();

                    if (ImGui.Button("<"))
                    {
                        isNeedToResetY = true;
                        currentPage    = Math.Max(0, currentPage - 1);
                    }

                    ImGui.SameLine();

                    using (FontManager.Instance().UIFont90.Push())
                    {
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextUnformatted($" {currentPage + 1}/{Math.Max(1, totalPages)} ");
                    }

                    ImGuiOm.TooltipHover($"{listingsDisplay.Count} 条结果");

                    ImGui.SameLine();

                    if (ImGui.Button(">"))
                    {
                        isNeedToResetY = true;
                        currentPage    = Math.Min(totalPages - 1, currentPage + 1);
                    }

                    ImGui.SameLine();

                    if (ImGui.Button(">>"))
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
                    if (ImGui.Button("关闭", new Vector2(closeBtnWidth, 0)))
                    {
                        selectedDataCenter = LocatedDataCenter;

                        foreach (var x in checkboxNodes)
                            x.Value.IsChecked = x.Key == LocatedDataCenter;

                        AgentId.LookingForGroup.SendEvent(1, 17);
                        isNeedToDisable = false;
                    }
                }
            }

            var sizeAfter = size - new Vector2(0, ImGui.GetFrameHeightWithSpacing());

            using (var child = ImRaii.Child("Child", sizeAfter, false, ImGuiWindowFlags.NoBackground))
            {
                if (child)
                {
                    if (isNeedToResetY)
                        ImGui.SetScrollHereY();

                    if (isNeedToDisable)
                    {
                        const string LOADING_TEXT = "正在获取招募数据中，请稍候...";
                        var          textSize     = ImGui.CalcTextSize(LOADING_TEXT);
                        var          childSize    = ImGui.GetContentRegionAvail();

                        ImGui.SetCursorPos(new Vector2((childSize.X - textSize.X) / 2f, (childSize.Y - textSize.Y) / 2f));
                        using (ImRaii.PushColor(ImGuiCol.Text, KnownColor.DarkGray.ToVector4())) ImGui.TextUnformatted(LOADING_TEXT);
                    }
                    else DrawPartyFinderList();

                    ImGuiOm.ScaledDummy(8f);
                }
            }

            ImGui.End();
        }
    }

    private void DrawPartyFinderList()
    {
        var startIndex = currentPage * config.PageSize;
        var pageItems  = listingsDisplay.Skip(startIndex).Take(config.PageSize).ToList();

        pageItems.ForEach(x => Task.Run(async () => await x.RequestAsync(), cancelSource.Token).ConfigureAwait(false));

        var jobIconSize      = new Vector2(ImGui.GetTextLineHeight() * 1.15f);
        var categoryIconSize = new Vector2(ImGui.GetTextLineHeight() * 1.25f);
        var availableWidth   = ImGui.GetContentRegionAvail().X;
        var cardSpacing      = 6f * GlobalUIScale;

        foreach (var listing in pageItems)
        {
            using var id = ImRaii.PushId(listing.ID);

            var descLineCount = 0;
            using (FontManager.Instance().UIFont90.Push())
                descLineCount = (int)MathF.Ceiling(ImGui.CalcTextSize(listing.Description).X / (availableWidth - (8 * ImGui.GetStyle().ItemSpacing.X)));

            var cardHeight = (3 + MathF.Max(1, descLineCount)) * ImGui.GetTextLineHeightWithSpacing();

            using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 8f * GlobalUIScale))
            using (ImRaii.PushStyle(ImGuiStyleVar.ChildBorderSize, 1f * GlobalUIScale))
            using (ImRaii.PushColor(ImGuiCol.Border, KnownColor.DarkSlateBlue.ToVector4()))
            using (ImRaii.Child
                   (
                       $"Card_{listing.ID}",
                       new(availableWidth, cardHeight),
                       true,
                       ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
                   ))
            {
                var drawList       = ImGui.GetWindowDrawList();
                var startCursorPos = ImGui.GetCursorPos();

                var accentColor = listing.MinItemLevel > 0
                                      ? KnownColor.Gold.ToUInt()
                                      : KnownColor.LightSkyBlue.ToUInt();

                var winPos = ImGui.GetWindowPos();
                var pMin   = winPos + new Vector2(1f, 4f * GlobalUIScale);
                var pMax   = winPos + new Vector2(6f     * GlobalUIScale, cardHeight - (4f * GlobalUIScale));
                drawList.AddRectFilled(pMin, pMax, accentColor, 2f * GlobalUIScale);

                ImGui.SetCursorPos(startCursorPos + new Vector2(8f * GlobalUIScale, 4f * GlobalUIScale));
                using (ImRaii.Group())
                {
                    var dutyName = string.IsNullOrEmpty(listing.Duty) ? LuminaWrapper.GetAddonText(7) : listing.Duty;

                    var hasCategoryIcon = DService.Instance().Texture.TryGetFromGameIcon(new(listing.CategoryIcon), out var categoryTexture);

                    var titleLineHeight = categoryIconSize.Y;
                    var titleStartY     = ImGui.GetCursorPosY();

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
                        var ilvlTextHeight = ImGui.GetTextLineHeight();
                        ImGui.SetCursorPosY(titleStartY + ((titleLineHeight - ilvlTextHeight) / 2f));
                        using (ImRaii.PushColor(ImGuiCol.Text, KnownColor.Gold.ToVector4())) 
                            ImGui.TextUnformatted($"[装等 {listing.MinItemLevel}]");
                    }

                    ImGui.SetCursorPosY(titleStartY + titleLineHeight + (2f * GlobalUIScale));

                    var hostStartPos = ImGui.GetCursorPos();

                    using (ImRaii.PushColor(ImGuiCol.Text, KnownColor.DarkGray.ToVector4()))
                    using (FontManager.Instance().UIFont80.Push())
                    using (ImRaii.Group())
                        ImGuiOm.RenderPlayerInfo(listing.PlayerName, listing.HomeWorldName);

                    ImGui.SameLine();
                    ImGui.SetCursorPos(hostStartPos);

                    var hostTextSize = ImGui.CalcTextSize($"{listing.PlayerName}@{listing.HomeWorldName}");
                    ImGui.InvisibleButton($"PlayerName##{listing.ID}", hostTextSize);

                    if (ImGui.IsItemHovered())
                    {
                        var screenPos = ImGui.GetItemRectMin();
                        var screenMax = ImGui.GetItemRectMax();
                        drawList.AddLine(screenPos with { Y = screenMax.Y - 1f }, screenMax, KnownColor.LightGray.ToUInt());
                    }

                    ImGuiOm.TooltipHover($"{listing.PlayerName}@{listing.HomeWorldName}");
                    ImGuiOm.ClickToCopyAndNotify($"{listing.PlayerName}@{listing.HomeWorldName}");

                    var isDescEmpty = string.IsNullOrEmpty(listing.Description);
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (10f * GlobalUIScale));
                    var descStartPos = ImGui.GetCursorPos();

                    using (FontManager.Instance().UIFont90.Push())
                    using (ImRaii.PushColor(ImGuiCol.Text, isDescEmpty ? KnownColor.DarkGray.ToVector4() : KnownColor.LightGray.ToVector4()))
                        ImGui.TextWrapped(isDescEmpty ? $"({LuminaWrapper.GetAddonText(11100)})" : $"{listing.Description}");
                    
                    if (!isDescEmpty)
                    {
                        ImGui.SameLine();
                        ImGui.SetCursorPos(descStartPos);
                        ImGui.InvisibleButton($"Description##{listing.ID}", ImGui.GetContentRegionAvail());

                        if (ImGui.IsItemHovered())
                            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

                        ImGuiOm.TooltipHover($"{listing.Description}");
                        ImGuiOm.ClickToCopyAndNotify(listing.Description);
                    }
                }

                var rightContentWidth = 230f * GlobalUIScale;
                ImGui.SameLine();
                ImGui.SetCursorPosX(availableWidth - rightContentWidth - (8f * GlobalUIScale));

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

                                        using (ImRaii.PushStyle
                                                   (ImGuiStyleVar.Alpha, 0.35f, !slot.Filled)) ImGui.Image(jobTexture.GetWrapOrEmpty().Handle, jobIconSize);
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
                                var          extraSpace  = rightContentWidth - ImGui.CalcTextSize(NO_SLOTS_TEXT).X;
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

                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (6f * GlobalUIScale));

                    using (FontManager.Instance().UIFont80.Push())
                    {
                        var remaining     = listing.SlotAvailable - listing.SlotFilled;
                        var remainingText = $"空位 {remaining}/{listing.SlotAvailable}";
                        var timeText      = $"剩余 {TimeSpan.FromSeconds(listing.TimeLeft).TotalMinutes:F0} 分";
                        var worldText     = listing.CreatedAtWorldName;

                        var padX           = 6f * GlobalUIScale;
                        var padY           = 2f * GlobalUIScale;
                        var capsuleSpacing = 4f * GlobalUIScale;

                        var remainSize = ImGui.CalcTextSize(remainingText);
                        var timeSize   = ImGui.CalcTextSize(timeText);
                        var worldSize  = ImGui.CalcTextSize(worldText);

                        var totalCapsuleWidth = remainSize.X + (padX * 2) + capsuleSpacing + (timeSize.X + (padX * 2)) + capsuleSpacing + (worldSize.X + (padX * 2));

                        var capsuleExtraSpace = rightContentWidth - totalCapsuleWidth;
                        if (capsuleExtraSpace > 0)
                            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + capsuleExtraSpace);

                        var slotBgColor = remaining > 0 ? KnownColor.DarkGreen : KnownColor.DimGray;
                        var slotFgColor = remaining > 0 ? KnownColor.LimeGreen : KnownColor.Gray;
                        DrawCapsuleBadge(drawList, remainingText, remainSize, padX, padY, slotBgColor.ToVector4(), slotFgColor.ToVector4());

                        ImGui.SameLine(0, capsuleSpacing);
                        DrawCapsuleBadge(drawList, timeText, timeSize, padX, padY, KnownColor.SaddleBrown.ToVector4(), KnownColor.Orange.ToVector4());
                        
                        ImGui.SameLine(0, capsuleSpacing);
                        DrawCapsuleBadge(drawList, worldText, worldSize, padX, padY, KnownColor.DarkSlateBlue.ToVector4(), KnownColor.LightPink.ToVector4());
                    }
                }
            }

            ImGui.Dummy(new Vector2(0, cardSpacing));
        }
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
