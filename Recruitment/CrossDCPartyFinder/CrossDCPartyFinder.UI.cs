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
                if (ImGui.Checkbox("倒序", ref config.OrderByDescending))
                {
                    isNeedToResetY = true;

                    config.Save(this);
                    SendRequestDynamic();
                }

                var totalPages = (int)Math.Ceiling(listingsDisplay.Count / (float)config.PageSize);

                using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(2, 0)))
                {
                    ImGui.SameLine(0, 4f * GlobalUIScale);

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
        using var table = ImRaii.Table("###ListingsTable", 3, ImGuiTableFlags.BordersInnerH, size);
        if (!table) return;

        ImGui.TableSetupColumn("招募图标", ImGuiTableColumnFlags.WidthFixed,   (ImGui.GetTextLineHeightWithSpacing() * 3) + ImGui.GetStyle().ItemSpacing.X);
        ImGui.TableSetupColumn("招募详情", ImGuiTableColumnFlags.WidthStretch, 50);
        ImGui.TableSetupColumn("招募信息", ImGuiTableColumnFlags.WidthFixed,   ImGui.CalcTextSize("八个汉字八个汉字").X);

        var startIndex = currentPage * config.PageSize;
        var pageItems  = listingsDisplay.Skip(startIndex).Take(config.PageSize).ToList();

        pageItems.ForEach(x => Task.Run(async () => await x.RequestAsync(), cancelSource.Token).ConfigureAwait(false));

        var iconSize = new Vector2(ImGui.GetTextLineHeightWithSpacing() * 3) +
                       new Vector2(ImGui.GetStyle().ItemSpacing.X, 2    * ImGui.GetStyle().ItemSpacing.Y);
        var jobIconSize = new Vector2(ImGui.GetTextLineHeight());

        foreach (var listing in pageItems)
        {
            using var id = ImRaii.PushId(listing.ID);

            var lineEndPosY = 0f;

            ImGui.TableNextRow();

            ImGui.TableNextColumn();

            if (DService.Instance().Texture.TryGetFromGameIcon(new(listing.CategoryIcon), out var categoryTexture))
            {
                ImGui.Spacing();

                ImGui.Image(categoryTexture.GetWrapOrEmpty().Handle, iconSize);

                ImGui.Spacing();

                lineEndPosY = ImGui.GetCursorPosY();
            }

            ImGui.TableNextColumn();

            using (ImRaii.Group())
            {
                using (FontManager.Instance().UIFont120.Push())
                {
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (4f * GlobalUIScale));
                    ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{listing.Duty}");
                }

                ImGui.SameLine(0, 8f * GlobalUIScale);
                var startCursorPos = ImGui.GetCursorPos();

                using (ImRaii.PushColor(ImGuiCol.Text, KnownColor.DarkGray.ToVector4()))
                using (FontManager.Instance().UIFont90.Push())
                using (ImRaii.Group())
                {
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (3f * GlobalUIScale));
                    ImGuiOm.RenderPlayerInfo(listing.PlayerName, listing.HomeWorldName);
                }

                ImGui.SameLine();
                ImGui.SetCursorPos(startCursorPos);
                ImGui.InvisibleButton($"PlayerName##{listing.ID}", ImGui.CalcTextSize($"{listing.PlayerName}@{listing.HomeWorldName}"));

                ImGuiOm.TooltipHover($"{listing.PlayerName}@{listing.HomeWorldName}");
                ImGuiOm.ClickToCopyAndNotify($"{listing.PlayerName}@{listing.HomeWorldName}");

                var isDescEmpty = string.IsNullOrEmpty(listing.Description);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() - (2f * GlobalUIScale));
                using (FontManager.Instance().UIFont80.Push())
                    ImGui.TextWrapped(isDescEmpty ? $"({LuminaWrapper.GetAddonText(11100)})" : $"{listing.Description}");
                ImGui.Spacing();

                if (!isDescEmpty)
                    ImGuiOm.TooltipHover(listing.Description);
                if (!isDescEmpty)
                    ImGuiOm.ClickToCopyAndNotify(listing.Description);

                lineEndPosY = MathF.Max(ImGui.GetCursorPosY(), lineEndPosY);
            }

            if (listing.Detail != null)
            {
                using (ImRaii.Group())
                {
                    foreach (var slot in listing.Detail.Slots)
                    {
                        if (slot.JobIcons.Count == 0) continue;

                        using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, 0.5f, !slot.Filled))
                        {
                            var displayIcon = slot.JobIcons.Count > 1 ? 62146 : slot.JobIcons[0];

                            if (DService.Instance().Texture.TryGetFromGameIcon(new(displayIcon), out var jobTexture))
                            {
                                ImGui.Image(jobTexture.GetWrapOrEmpty().Handle, jobIconSize);

                                ImGui.SameLine();
                            }
                        }
                    }

                    ImGui.Spacing();

                    if (listing.MinItemLevel > 0)
                    {
                        ImGui.SameLine(0, 6f * GlobalUIScale);
                        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow, listing.MinItemLevel != 0))
                            ImGui.TextUnformatted($"[{listing.MinItemLevel}]");
                    }
                }

                lineEndPosY = MathF.Max(ImGui.GetCursorPosY(), lineEndPosY);
            }

            ImGui.TableNextColumn();

            ImGui.SetCursorPosY(lineEndPosY - (3 * ImGui.GetTextLineHeightWithSpacing()) - (4 * ImGui.GetStyle().ItemSpacing.Y));

            using (ImRaii.Group())
            using (FontManager.Instance().UIFont80.Push())
            {
                ImGui.NewLine();

                ImGui.TextColored(KnownColor.Orange.ToVector4(), "当前位于:");

                ImGui.SameLine();
                ImGui.TextUnformatted($"{listing.CreatedAtWorldName}");

                ImGui.TextColored(KnownColor.Orange.ToVector4(), "剩余人数:");

                ImGui.SameLine();
                ImGui.TextUnformatted($"{listing.SlotAvailable - listing.SlotFilled}");

                ImGui.TextColored(KnownColor.Orange.ToVector4(), "剩余时间:");

                ImGui.SameLine();
                ImGui.TextUnformatted($"{TimeSpan.FromSeconds(listing.TimeLeft).TotalMinutes:F0} 分钟");
            }

            ImGui.TableNextRow();
        }
    }
}
