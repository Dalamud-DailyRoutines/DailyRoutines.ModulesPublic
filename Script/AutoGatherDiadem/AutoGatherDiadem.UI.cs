using DailyRoutines.Common.Extensions;
using DailyRoutines.Extensions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic;

internal unsafe partial class AutoGatherDiadem
{
    protected override void OverlayUI()
    {
        if (!isOnAutoGather &&
            GameState.TerritoryType is not (ZONE_DIADEM or ZONE_FIRMAMENT))
        {
            Overlay.IsOpen = false;
            return;
        }

        if (!GatheringModeLoc.TryGetValue(config.Mode, out var modeInfo))
        {
            Overlay.IsOpen = false;
            return;
        }

        using var font = FontManager.Instance().UIFont80.Push();

        DrawOperation(true);

        ImGui.NewLine();

        using var modeHeading = ImRaii.Heading1($"{Lang.Get("Mode")}: {modeInfo.Title}");

        switch (config.Mode)
        {
            case GatheringMode.练级:
                using (var table = ImRaii.Table("###LevelTable", 2))
                {
                    if (table)
                    {
                        ImGui.TableSetupColumn("左栏", ImGuiTableColumnFlags.WidthStretch, 40);
                        ImGui.TableSetupColumn("右栏", ImGuiTableColumnFlags.WidthStretch, 10);

                        foreach (var (jobID, _) in JobInfos)
                        {
                            if (!LuminaGetter.TryGetRow<ClassJob>(jobID, out var jobData)) continue;
                            if (!ITextureProvider.Instance().TryGetFromGameIcon(jobData.GetIcon(), out var jobIcon)) continue;

                            ImGui.TableNextRow();
                            ImGui.TableNextColumn();
                            ImGuiOm.TextImage
                            (
                                jobData.Name.ToString(),
                                jobIcon.GetWrapOrEmpty().Handle,
                                new(ImGui.GetTextLineHeight())
                            );

                            ImGui.TableNextColumn();
                            ImGui.AlignTextToFramePadding();
                            ImGui.TextUnformatted($"{LocalPlayerState.GetClassJobLevel(jobID)}");
                        }
                    }
                }

                break;
            case GatheringMode.冲分:
                using (var table = ImRaii.Table("###SkillPTTable", 2))
                {
                    if (table)
                    {
                        ImGui.TableSetupColumn("左栏", ImGuiTableColumnFlags.WidthStretch, 40);
                        ImGui.TableSetupColumn("右栏", ImGuiTableColumnFlags.WidthStretch, 10);

                        foreach (var (jobID, jobInfo) in JobInfos)
                        {
                            if (!LuminaGetter.TryGetRow<ClassJob>(jobID, out var jobData)) continue;
                            if (!ITextureProvider.Instance().TryGetFromGameIcon(jobData.GetIcon(), out var jobIcon)) continue;

                            ImGui.TableNextRow();
                            ImGui.TableNextColumn();
                            ImGuiOm.TextImage
                            (
                                jobData.Name.ToString(),
                                jobIcon.GetWrapOrEmpty().Handle,
                                new(ImGui.GetTextLineHeight())
                            );

                            var point = AchievementManager.Instance().TryGetAchievement(jobInfo.Achievement500K, out var achievementInfo) ?
                                            achievementInfo.Current :
                                            0;
                            ImGui.TableNextColumn();
                            ImGui.AlignTextToFramePadding();
                            ImGui.TextUnformatted($"{point}");
                        }
                    }
                }

                break;
            case GatheringMode.绿材:
                var manager = InventoryManager.Instance();

                using (var table = ImRaii.Table("###ItemTable", 2))
                {
                    if (table)
                    {
                        ImGui.TableSetupColumn("左栏", ImGuiTableColumnFlags.WidthStretch, 40);
                        ImGui.TableSetupColumn("右栏", ImGuiTableColumnFlags.WidthStretch, 10);

                        foreach (var itemID in SpecialItems)
                        {
                            if (!LuminaGetter.TryGetRow<Item>(itemID, out var itemData)) continue;
                            if (!ITextureProvider.Instance().TryGetFromGameIcon(new(itemData.Icon), out var itemIcon)) continue;

                            ImGui.TableNextRow();
                            ImGui.TableNextColumn();
                            ImGuiOm.TextImage
                            (
                                itemData.Name.ToString(),
                                itemIcon.GetWrapOrEmpty().Handle,
                                new(ImGui.GetTextLineHeight())
                            );

                            ImGui.TableNextColumn();
                            if (ImGui.Selectable
                                (
                                    $"{manager->GetInventoryItemCount(itemID)}###{itemID}",
                                    false,
                                    ImGuiSelectableFlags.SpanAllColumns
                                ))
                                ChatManager.Instance().SendMessage($"/isearch \"{itemData.Name.ToString()}\"");
                        }
                    }
                }

                break;
        }
    }

    protected override void ConfigUI()
    {
        using var tabBar = ImRaii.TabBar("AutoGatherDiadem");
        if (!tabBar) return;

        using (var item = ImRaii.TabItem(Lang.Get("General")))
        {
            if (item)
            {
                DrawOperation();

                ImGui.NewLine();

                DrawModeConfig();
            }
        }

        using (var item = ImRaii.TabItem(Lang.Get("Settings")))
        {
            if (item)
                DrawOthersConfig();
        }
    }

    private void DrawOperation
    (
        bool isFromOverlay = false
    )
    {
        using var heading = ImRaii.Heading1(Lang.Get("Operation"));

        using (ImRaii.Disabled(isOnAutoGather || machine.CurrentPhase != Phase.Idle))
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Play, Lang.Get("Start")))
            {
                isOnAutoGather = true;
                lastLevel      = AgentHUD.Instance()->ExpLevel;
                machine.Reset();
                machine.SetPhase(Phase.Registering);
            }
        }

        ImGui.SameLine();

        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Stop, Lang.Get("Stop")))
        {
            StopAutomation();
        }

        ImGui.SameLine();

        using (ImRaii.Disabled(GameState.TerritoryType != ZONE_FIRMAMENT || machine.CurrentPhase != Phase.Idle))
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.ExchangeAlt, Lang.Get("Exchange")))
            {
                BeginExchange(false);
            }
        }

        if (isFromOverlay)
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Cog, Lang.Get("Settings")))
                ChatManager.Instance().SendCommand($"/pdr toggleconfig {nameof(AutoGatherDiadem)}");

            ImGui.SameLine();
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.WindowClose, Lang.Get("Close")))
                Overlay.IsOpen = false;
        }
    }

    private void DrawModeConfig()
    {
        using var heading = ImRaii.Heading1(Lang.Get("Mode"));

        if (!GatheringModeLoc.TryGetValue(config.Mode, out var info)) return;

        ImGui.SetNextItemWidth(200f * GlobalUIScale);

        using (var combo = ImRaii.Combo($"{Lang.Get("Mode")}###ModeSelectCombo", info.Title, ImGuiComboFlags.HeightLarge))
        {
            if (combo)
            {
                foreach (var (mode, (title, description)) in GatheringModeLoc)
                {
                    if (ImGui.Selectable($"{title}##{mode}"))
                    {
                        config.Mode = mode;
                        config.Save(this);
                    }

                    ImGuiOm.TooltipHover($"{description}");
                }
            }
        }

        ImGuiOm.TextDisabledWrapped(GatheringModeLoc.GetValueOrDefault(config.Mode).Description);

        switch (config.Mode)
        {
            case GatheringMode.练级:
                DrawLevelingModeConfig();
                break;
            case GatheringMode.冲分:
                DrawSkillPTModeConfig();
                break;
            case GatheringMode.绿材:
                DrawCloudedModeConfig();
                break;
        }
    }

    private void DrawLevelingModeConfig()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoGatherDiadem-LevelThreshold")}");

        ImGui.SameLine(0, 6f * GlobalUIScale);
        if (ImGui.SmallButton(Lang.Get("Add")))
            ImGui.OpenPopup("AddNewLevelThresholdPopup");

        using (var popup = ImRaii.Popup("AddNewLevelThresholdPopup"))
        {
            if (popup)
            {
                ImGui.SetNextItemWidth(150f * GlobalUIScale);
                ImGui.InputUInt("###LevelThresholdInput", ref addNewLevelThresholdInput);

                using (ImRaii.Disabled(addNewLevelThresholdInput == 0))
                {
                    if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Check, Lang.Get("Confirm")) &&
                        !config.LevelThresholds.Contains(addNewLevelThresholdInput))
                    {
                        config.LevelThresholds.Add(addNewLevelThresholdInput);
                        config.LevelThresholds.Sort();
                        config.LevelThresholds = [.. config.LevelThresholds.Distinct()];
                        config.Save(this);

                        ImGui.CloseCurrentPopup();
                        addNewLevelThresholdInput = 0;
                    }
                }
            }
        }

        using (ImRaii.PushIndent())
        {
            foreach (var threshold in config.LevelThresholds.ToList())
            {
                ImGui.TextUnformatted($"{threshold:000}");

                ImGui.SameLine(0, 12f * GlobalUIScale);

                if (ImGui.SmallButton($"{Lang.Get("Delete")}##{threshold}"))
                {
                    config.LevelThresholds.Remove(threshold);
                    config.LevelThresholds.Sort();
                    config.Save(this);
                }
            }
        }
    }

    private void DrawCloudedModeConfig()
    {
        using var width = ImRaii.ItemWidth(200f * GlobalUIScale);

        var selectedBlacklistWeathers = string.Join
        (
            ',',
            config.BlacklistWeathers
                  .Select(x => LuminaGetter.GetRow<Weather>(x)?.Name.ToString() ?? string.Empty)
                  .Distinct()
        );

        using (var combo = ImRaii.Combo
               (
                   $"{Lang.Get("AutoGatherDiadem-BlacklistWeathers")}###BlacklistWeathersCombo",
                   selectedBlacklistWeathers,
                   ImGuiComboFlags.HeightLarge
               ))
        {
            if (combo)
            {
                foreach (var (weather, job) in SpecialWeathers)
                {
                    if (!LuminaGetter.TryGetRow<Weather>(weather, out var data)) continue;
                    if (!LuminaGetter.TryGetRow<ClassJob>(job, out var jobData)) continue;

                    var icon = ITextureProvider.Instance().GetFromGameIcon(new((uint)data.Icon));

                    if (ImGuiOm.SelectableImageWithText
                        (
                            icon.GetWrapOrEmpty().Handle,
                            new(ImGui.GetTextLineHeightWithSpacing()),
                            $"{data.Name.ToString()} [{jobData.Name.ToString()}]",
                            config.BlacklistWeathers.Contains(weather),
                            ImGuiSelectableFlags.DontClosePopups
                        ))
                    {
                        if (!config.BlacklistWeathers.Add(weather))
                            config.BlacklistWeathers.Remove(weather);

                        config.Save(this);
                    }
                }
            }
        }

        ImGuiOm.TextDisabledWrapped(Lang.Get("AutoGatherDiadem-BlacklistWeathersHelp"));

        if (gatheringItemCombo.DrawCheckbox())
            config.Save(this);

        ImGui.SameLine();
        ImGui.TextUnformatted(Lang.Get("AutoGatherDiadem-GatheringItems"));

        ImGuiOm.TextDisabledWrapped(Lang.Get("AutoGatherDiadem-GatheringItemsHelp"));
    }

    private void DrawSkillPTModeConfig()
    {
        using var width = ImRaii.ItemWidth(200f * GlobalUIScale);

        if (gatheringItemCombo.DrawCheckbox())
            config.Save(this);

        ImGui.SameLine();
        ImGui.TextUnformatted(Lang.Get("AutoGatherDiadem-GatheringItems"));

        ImGuiOm.TextDisabledWrapped(Lang.Get("AutoGatherDiadem-GatheringItemsHelp"));

        if (ImGui.Checkbox($"{Lang.Get("AutoGatherDiadem-IgnoreSkillPoints")}", ref config.IgnoreSkillPoints))
            config.Save(this);
        ImGuiOm.TextDisabledWrapped(Lang.Get("AutoGatherDiadem-IgnoreSkillPointsHelp"));
    }

    private void DrawOthersConfig()
    {
        ImGui.SetNextItemWidth(200f * GlobalUIScale);
        if (ImGui.InputUInt(Lang.Get("AutoGatherDiadem-LeaveDutyThresholdMinute"), ref config.RoundMinute))
            config.RoundMinute = Math.Clamp(config.RoundMinute, 5, 175);
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);
        ImGuiOm.TextDisabledWrapped(Lang.Get("AutoGatherDiadem-LeaveDutyThresholdMinuteHelp"));

        ImGui.SetNextItemWidth(200f * GlobalUIScale);
        ImGui.InputText(Lang.Get("AutoGatherDiadem-AutoDiscardGroupName"), ref config.AutoDiscardGroupName, 256);
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);
        ImGuiOm.TextDisabledWrapped(Lang.Get("AutoGatherDiadem-AutoDiscardGroupNameHelp"));

        if (ImGui.Checkbox(Lang.Get("AutoGatherDiadem-UseCordial"), ref config.UseCordial))
            config.Save(this);
        ImGuiOm.TextDisabledWrapped(Lang.Get("AutoGatherDiadem-UseCordialHelp"));

        ImGui.NewLine();

        using (ImRaii.Heading1(Lang.Get("AutoGatherDiadem-ItemToExchange")))
        {
            ImGuiOm.TextDisabledWrapped(Lang.Get("AutoGatherDiadem-ItemToExchangeHelp"));

            if (exchangeItemCombo.DrawRadio())
            {
                config.ExchangeItem = ExchangeItems.FirstOrDefault(item => item.ItemID == exchangeItemCombo.SelectedID);
                config.Save(this);
            }

            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("Clear")))
            {
                exchangeItemCombo.SelectedID = 0;
                config.ExchangeItem          = null;
                config.Save(this);
            }
        }

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("AutoGatherDiadem-BlacklistMonsters"));

        ImGui.SameLine(0, 6f * GlobalUIScale);
        if (ImGui.SmallButton(Lang.Get("Add")))
            ImGui.OpenPopup("AddNewBlacklistMonsterPopup");

        using (ImRaii.PushIndent())
        {
            ImGuiOm.TextDisabledWrapped(Lang.Get("AutoGatherDiadem-BlacklistMonstersHelp"));

            using (var popup = ImRaii.Popup("AddNewBlacklistMonsterPopup"))
            {
                if (popup)
                {
                    ImGui.InputText("###BlacklistMonsters", ref addNewBlacklistMonsterInput);

                    using (ImRaii.Disabled
                           (
                               string.IsNullOrWhiteSpace(addNewBlacklistMonsterInput) ||
                               config.BlacklistMonsters.Contains(addNewBlacklistMonsterInput)
                           ))
                    {
                        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, Lang.Get("Confirm")))
                        {
                            config.BlacklistMonsters.Add(addNewBlacklistMonsterInput);
                            config.Save(this);

                            ImGui.CloseCurrentPopup();
                            addNewBlacklistMonsterInput = string.Empty;
                        }
                    }
                }
            }

            foreach (var monsterName in config.BlacklistMonsters)
            {
                ImGui.TextUnformatted($"{monsterName}");

                ImGui.SameLine(0, 12f * GlobalUIScale);

                if (ImGui.SmallButton($"{Lang.Get("Delete")}###{monsterName}"))
                {
                    config.BlacklistMonsters.Remove(monsterName);
                    config.Save(this);
                }
            }
        }
    }
}
