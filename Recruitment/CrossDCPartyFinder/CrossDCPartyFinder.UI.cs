using System.Numerics;
using DailyRoutines.Extensions;
using Dalamud.Interface.Colors;
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
                var totalPages = (int)Math.Ceiling(listingsDisplay.Count / (float)config.PageSize);

                using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(2, 0)))
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
                    ImGui.TextUnformatted($" {currentPage + 1} / {Math.Max(1, totalPages)} ");
                    ImGuiOm.TooltipHover($"{listingsDisplay.Count}");

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

                ImGui.SameLine();
                if (ImGui.Button("关闭"))
                    selectedDataCenter = LocatedDataCenter;

                ImGui.SameLine();
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                ImGui.InputTextWithHint("###SearchString", Lang.Get("PleaseSearch"), ref currentSeach, 128);

                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    isNeedToResetY = true;
                    SendRequestDynamic();
                }
            }

            var sizeAfter = size - new Vector2(0, ImGui.GetTextLineHeightWithSpacing());

            using (var child = ImRaii.Child("Child", sizeAfter, false, ImGuiWindowFlags.NoBackground))
            {
                if (child)
                {
                    if (isNeedToResetY)
                        ImGui.SetScrollHereY();
                    if (!isNeedToDisable)
                        DrawPartyFinderList(sizeAfter);

                    ImGuiOm.ScaledDummy(8f);
                }
            }

            ImGui.End();
        }
    }

    private void DrawPartyFinderList(Vector2 size)
    {
        using var table = ImRaii.Table("###ListingsTable", 2, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg, new Vector2(ImGui.GetContentRegionAvail().X, size.Y));
        if (!table) return;

        ImGui.TableSetupColumn("招募详情", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("队伍状态", ImGuiTableColumnFlags.WidthFixed, 220f * GlobalUIScale);

        var startIndex = currentPage * config.PageSize;
        var pageItems  = listingsDisplay.Skip(startIndex).Take(config.PageSize).ToList();

        pageItems.ForEach(x => Task.Run(async () => await x.RequestAsync(), cancelSource.Token).ConfigureAwait(false));

        var jobIconSize = new Vector2(ImGui.GetTextLineHeight() * 1.15f);
        var categoryIconSize = new Vector2(ImGui.GetTextLineHeight() * 1.25f);

        foreach (var listing in pageItems)
        {
            using var id = ImRaii.PushId(listing.ID);

            ImGui.TableNextRow();

            // --- COLUMN 0: 招募详情 ---
            ImGui.TableNextColumn();

            using (ImRaii.Group())
            {
                var dutyName = string.IsNullOrEmpty(listing.Duty) ? LuminaWrapper.GetAddonText(7) : listing.Duty;

                // 1. Category Icon + Duty Name + MinItemLevel
                var hasCategoryIcon = DService.Instance().Texture.TryGetFromGameIcon(new(listing.CategoryIcon), out var categoryTexture);
                if (hasCategoryIcon)
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.Image(categoryTexture.GetWrapOrEmpty().Handle, categoryIconSize);
                    ImGui.SameLine(0, 6f * GlobalUIScale);
                }

                using (FontManager.Instance().UIFont120.Push())
                {
                    if (!hasCategoryIcon) ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), dutyName);
                }

                if (listing.MinItemLevel > 0)
                {
                    ImGui.SameLine(0, 8f * GlobalUIScale);
                    using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow))
                    {
                        ImGui.TextUnformatted($"[装等 {listing.MinItemLevel}]");
                    }
                }

                // 2. Creator/Host Info (Dedicated elegant line under the title, preventing overflow/collision)
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (1f * GlobalUIScale));
                var startCursorPos = ImGui.GetCursorPos();
                using (ImRaii.PushColor(ImGuiCol.Text, KnownColor.DarkGray.ToVector4()))
                using (FontManager.Instance().UIFont80.Push())
                using (ImRaii.Group())
                {
                    ImGuiOm.RenderPlayerInfo(listing.PlayerName, listing.HomeWorldName);
                }

                ImGui.SameLine();
                ImGui.SetCursorPos(startCursorPos);
                ImGui.InvisibleButton($"PlayerName##{listing.ID}", ImGui.CalcTextSize($"{listing.PlayerName}@{listing.HomeWorldName}"));

                ImGuiOm.TooltipHover($"{listing.PlayerName}@{listing.HomeWorldName} (点击复制)");
                ImGuiOm.ClickToCopyAndNotify($"{listing.PlayerName}@{listing.HomeWorldName}");

                // 3. Recruitment Description
                var isDescEmpty = string.IsNullOrEmpty(listing.Description);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (2f * GlobalUIScale));
                using (FontManager.Instance().UIFont90.Push())
                {
                    ImGui.TextWrapped(isDescEmpty ? $"({LuminaWrapper.GetAddonText(11100)})" : $"{listing.Description}");
                }
                if (!isDescEmpty)
                {
                    ImGuiOm.TooltipHover(listing.Description);
                    ImGuiOm.ClickToCopyAndNotify(listing.Description);
                }

                ImGui.Dummy(new Vector2(0, 4f * GlobalUIScale));
            }

            // --- COLUMN 1: 队伍状态 ---
            ImGui.TableNextColumn();

            using (ImRaii.Group())
            {
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (4f * GlobalUIScale));

                // 1. Party Slots (Job Icons) - Right Aligned with safety padding
                if (listing.Detail != null)
                {
                    var validSlotsCount = listing.Detail.Slots.Count(slot => slot.JobIcons.Count > 0);
                    if (validSlotsCount > 0)
                    {
                        var slotsWidth = (validSlotsCount * jobIconSize.X) + ((validSlotsCount - 1) * (3f * GlobalUIScale));
                        var extraSpace = ImGui.GetContentRegionAvail().X - slotsWidth - (8f * GlobalUIScale);

                        if (extraSpace > 0)
                        {
                            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + extraSpace);
                        }

                        using (ImRaii.Group())
                        {
                            var slotsCount = listing.Detail.Slots.Count;
                            var drawnCount = 0;
                            for (int i = 0; i < slotsCount; i++)
                            {
                                var slot = listing.Detail.Slots[i];
                                if (slot.JobIcons.Count == 0) continue;

                                using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, 0.4f, !slot.Filled))
                                {
                                    var displayIcon = slot.JobIcons.Count > 1 ? 62146 : slot.JobIcons[0];

                                    if (DService.Instance().Texture.TryGetFromGameIcon(new(displayIcon), out var jobTexture))
                                    {
                                        if (drawnCount > 0)
                                        {
                                            ImGui.SameLine(0, 3f * GlobalUIScale);
                                        }
                                        ImGui.Image(jobTexture.GetWrapOrEmpty().Handle, jobIconSize);
                                        drawnCount++;
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        // No slots defined (e.g. Map digging, RP, etc.) - Right Aligned with safety padding
                        using (FontManager.Instance().UIFont90.Push())
                        {
                            var noSlotsText = "不设职业限制";
                            var extraSpace = ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(noSlotsText).X - (8f * GlobalUIScale);

                            if (extraSpace > 0)
                            {
                                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + extraSpace);
                            }

                            using (ImRaii.PushColor(ImGuiCol.Text, KnownColor.DarkGray.ToVector4()))
                            {
                                ImGui.TextUnformatted(noSlotsText);
                            }
                        }
                    }
                }
                else
                {
                    using (FontManager.Instance().UIFont90.Push())
                    {
                        var loadingText = "正在加载队伍...";
                        var extraSpace = ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(loadingText).X - (8f * GlobalUIScale);

                        if (extraSpace > 0)
                        {
                            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + extraSpace);
                        }

                        using (ImRaii.PushColor(ImGuiCol.Text, KnownColor.Gray.ToVector4()))
                        {
                            ImGui.TextUnformatted(loadingText);
                        }
                    }
                }

                // 2. Status Row (Availability, Time, Created World) - Right Aligned with safety padding
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (6f * GlobalUIScale));

                using (FontManager.Instance().UIFont80.Push())
                {
                    var remaining = listing.SlotAvailable - listing.SlotFilled;
                    var remainingText = $"空位: {remaining}/{listing.SlotAvailable}";
                    var timeText = $"剩: {TimeSpan.FromSeconds(listing.TimeLeft).TotalMinutes:F0}分";
                    var worldText = listing.CreatedAtWorldName;

                    var remainingWidth = ImGui.CalcTextSize(remainingText).X;
                    var timeWidth = ImGui.CalcTextSize(timeText).X;
                    var worldWidth = ImGui.CalcTextSize(worldText).X;

                    var spacing = 10f * GlobalUIScale;
                    var totalWidth = remainingWidth + spacing + timeWidth + spacing + worldWidth;
                    var statusExtraSpace = ImGui.GetContentRegionAvail().X - totalWidth - (8f * GlobalUIScale);

                    if (statusExtraSpace > 0)
                    {
                        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + statusExtraSpace);
                    }

                    var slotColor = remaining > 0 ? KnownColor.LimeGreen : KnownColor.Gray;
                    ImGui.TextColored(slotColor.ToVector4(), remainingText);

                    ImGui.SameLine(0, spacing);
                    ImGui.TextColored(KnownColor.Orange.ToVector4(), timeText);

                    ImGui.SameLine(0, spacing);
                    ImGui.TextColored(KnownColor.LightPink.ToVector4(), worldText);
                }

                ImGui.Dummy(new Vector2(0, 4f * GlobalUIScale));
            }
        }
    }
}
