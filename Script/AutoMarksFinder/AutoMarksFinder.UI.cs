using System.Globalization;
using System.Numerics;
using DailyRoutines.Common.Extensions;
using DailyRoutines.Extensions;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using OmenTools.Dalamud;
using OmenTools.Info.Lumina;
using OmenTools.Interop.Game.Lumina;
using Action = System.Action;
using NotifyHelper = OmenTools.OmenService.NotifyHelper;
using Task = System.Threading.Tasks.Task;

namespace DailyRoutines.ModulesPublic;

public partial class AutoMarksFinder
{
    private ScanZoneCombo? scanZoneCombo;
    private int            scanResultTrainIndex = -1;
    private bool           confirmClearScanResults;

    protected override void ConfigUI()
    {
        using (ImRaii.Heading1(Lang.Get("Command")))
            ImGui.TextUnformatted($"{COMMAND} → {Lang.Get("AutoMarksFinder-HelpMessage")}");

        ImGui.Spacing();
        MarksFinderUI();
    }

    public void MarksFinderUI()
    {
        using (ImRaii.Heading1(Lang.Get("PreCondition")))
            ImGuiOm.TextDisabledWrapped(Lang.Get("IPC-InstalledAndEnabledPlugin", "vnavmesh"));

        ImGui.NewLine();

        using var tabBar = ImRaii.TabBar("###TabBar");
        if (!tabBar) return;

        DrawTabItemTrain();

        DrawTabItemScanner();

        DrawTabItemSettings();
    }

    public void DrawSmallCard()
    {
        var selected =
            selectedTrainIndex == -1 ||
            selectedTrainIndex > config.Trains.Count - 1 ?
                null :
                config.Trains[selectedTrainIndex];

        using var heading = ImRaii.Heading1(Lang.Get("AutoMarksFinder-SelectRoutes"));

        if (DrawTrainsSelectCombo(200f * GlobalUIScale, ref selectedTrainIndex))
        {
            smallCardMobIndex = 0;
            StopAutoBroadcast();
            selected = SelectedTrain;
        }

        ImGui.SameLine();
        ImGui.TextDisabled("|");

        using (ImRaii.Disabled(selected == null))
        {
            ImGui.SameLine();

            if (ImGuiOm.ButtonIcon("导出", FontAwesomeIcon.FileExport, Lang.Get("Export")))
            {
                if (selected != null)
                    ExportToClipboard(selected);
            }
        }

        ImGui.SameLine();

        if (ImGuiOm.ButtonIcon("导入", FontAwesomeIcon.FileImport, Lang.Get("Import")))
        {
            var imported = ImportFromClipboard<NotoriousMonsterList>();

            if (imported != null)
            {
                foreach (var monster in imported.MonstersFound)
                    monster.SetState(true);

                config.Trains.Add(imported);

                aliveMonitor?.InitializeTracks(imported);

                config.Save(this);
            }
        }

        ImGui.SameLine();
        ImGui.TextDisabled("|");

        ImGui.SameLine();
        if (ImGuiOm.ButtonIcon("设置", FontAwesomeIcon.Cog, Lang.Get("Settings")))
            ChatManager.Instance().SendMessage($"/pdr search {GetType().Name}");

        ImGui.Spacing();

        if (selected == null) return;

        var source = selected.MonstersFound.ToList();
        if (source.Count == 0) return;

        using var table = ImRaii.Table("##SmallCardTable", 3, ImGuiTableFlags.Resizable);
        if (!table) return;

        ImGui.TableSetupColumn("Left",  ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Mid",   ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Right", ImGuiTableColumnFlags.WidthStretch);

        ImGui.TableNextRow();

        ImGui.TableNextColumn();

        if (smallCardMobIndex > 0)
        {
            if (ImGuiOm.ButtonIcon("##FormerMob", FontAwesomeIcon.ArrowLeft))
                smallCardMobIndex--;
        }

        ImGui.TableNextColumn();
        ImGuiOm.Text($"{Lang.Get("Current")}");

        ImGui.TableNextColumn();

        if (smallCardMobIndex < source.Count - 1)
        {
            if (ImGuiOm.ButtonIcon("##NextMob", FontAwesomeIcon.ArrowRight))
                smallCardMobIndex++;
        }

        ImGui.TableNextRow();

        ImGui.TableNextColumn();

        if (smallCardMobIndex > 0)
        {
            var info = source[smallCardMobIndex - 1];
            using (FontManager.Instance().UIFont120.Push())
                ImGui.TextColored
                (
                    info.IsAlive ?
                        KnownColor.LightGreen.ToVector4() :
                        KnownColor.Red.ToVector4(),
                    $"{info.GetBNPCName()}"
                );

            ImGui.TextUnformatted(FormatZoneInfo(info.GetZoneName(), info.Instance));
        }

        ImGui.TableNextColumn();
        {
            var info = source[smallCardMobIndex];
            using (FontManager.Instance().UIFont120.Push())
                ImGui.TextColored
                (
                    info.IsAlive ?
                        KnownColor.LightGreen.ToVector4() :
                        KnownColor.Red.ToVector4(),
                    $"{info.GetBNPCName()}"
                );

            ImGui.TextUnformatted(FormatZoneInfo(info.GetZoneName(), info.Instance));
        }

        ImGui.TableNextColumn();

        if (smallCardMobIndex < source.Count - 1)
        {
            var info = source[smallCardMobIndex + 1];
            using (FontManager.Instance().UIFont120.Push())
                ImGui.TextColored
                (
                    info.IsAlive ?
                        KnownColor.LightGreen.ToVector4() :
                        KnownColor.Red.ToVector4(),
                    $"{info.GetBNPCName()}"
                );

            ImGui.TextUnformatted(FormatZoneInfo(info.GetZoneName(), info.Instance));
        }

        ImGui.TableNextRow();

        ImGui.TableNextColumn();

        ImGui.TableNextColumn();
        {
            var info = source[smallCardMobIndex];
            DrawPositionColumn(info);

            ImGui.SameLine();
            DrawAetheryteColumn(info);

            DrawMonsterStatusToggle(selected, info);

            ImGui.SameLine();
            DrawTeleportToMonsterZone(info);
        }

        ImGui.TableNextColumn();

        if (smallCardMobIndex < source.Count - 1)
        {
            var info = source[smallCardMobIndex + 1];
            DrawPositionColumn(info);

            ImGui.SameLine();
            DrawAetheryteColumn(info);

            DrawMonsterStatusToggle(selected, info);

            ImGui.SameLine();
            DrawTeleportToMonsterZone(info);
        }
    }

    private void DrawTabItemTrain()
    {
        using var tabTrain = ImRaii.TabItem(Lang.Get("AutoMarksFinder-Routes"));
        if (!tabTrain) return;

        var selected =
            selectedTrainIndex == -1 ||
            selectedTrainIndex > config.Trains.Count - 1 ?
                null :
                config.Trains[selectedTrainIndex];

        using var heading = ImRaii.Heading1(Lang.Get("AutoMarksFinder-SelectRoutes"));

        if (DrawTrainsSelectCombo(200f * GlobalUIScale, ref selectedTrainIndex))
        {
            smallCardMobIndex = 0;
            StopAutoBroadcast();
            selected = SelectedTrain;
        }

        ImGui.SameLine();

        using (ImRaii.Disabled(selected == null))
        {
            if (ImGuiOm.ButtonIcon("指定出发点", FontAwesomeIcon.GasPump, Lang.Get("AutoMarksFinder-IntendedStartingPoint")))
                ImGui.OpenPopup("StartAetherytePopup");

            using (var popup = ImRaii.Popup("StartAetherytePopup"))
            {
                if (popup)
                {
                    var startAetheryte = selected.StartAetheryte?.RowId ?? 0;
                    ImGui.SetNextItemWidth(200f * GlobalUIScale);
                    if (ImGuiOm.SingleSelectCombo
                        (
                            "AetheryteSelectCombo",
                            Sheets.Aetherytes,
                            ref startAetheryte,
                            ref startAetheryteInput,
                            x => $"{x.PlaceName.Value.Name.ToString()} ({x.RowId})",
                            [new(Lang.Get("Zone"), ImGuiTableColumnFlags.WidthStretch, 0)],
                            [
                                x => () =>
                                {
                                    if (ImGuiOm.Selectable
                                        (
                                            $"{x.PlaceName.Value.Name.ToString()} ({x.RowId})",
                                            selected.StartAetheryte?.RowId == x.RowId,
                                            ImGuiSelectableFlags.DontClosePopups
                                        ))
                                        selected.StartAetheryte = x;
                                }
                            ],
                            [x => x.PlaceName.Value.Name.ToString(), x => x.RowId.ToString()],
                            true
                        ))
                        selected.StartAetheryte = LuminaGetter.GetRow<Aetheryte>(startAetheryte);

                    ImGui.SameLine();

                    if (ImGui.Button(Lang.Get("AutoMarksFinder-SortAccordingToStartingPoint")))
                    {
                        selected.Reorder();
                        StopAutoBroadcast();
                        config.Save(this);
                    }
                }
            }

            ImGui.SameLine(0, 8f * GlobalUIScale);
            DrawAutoBroadcastButton();

            ImGui.SameLine(0, 8f * GlobalUIScale);
            ImGui.TextDisabled("|");

            ImGui.SameLine(0, 8f * GlobalUIScale);
            if (ImGuiOm.ButtonIcon("导出", FontAwesomeIcon.FileExport, Lang.Get("Export")))
                ExportToClipboard(selected!);
        }

        ImGui.SameLine();

        if (ImGuiOm.ButtonIcon("导入", FontAwesomeIcon.FileImport, Lang.Get("Import")))
        {
            var imported = ImportFromClipboard<NotoriousMonsterList>();

            if (imported != null)
            {
                foreach (var monster in imported.MonstersFound)
                    monster.SetState(true);

                config.Trains.Add(imported);

                aliveMonitor?.InitializeTracks(imported);

                config.Save(this);
            }
        }

        ImGui.Spacing();

        if (selected == null) return;

        var       source = selected.MonstersFound.OrderByDescending(x => x.IsAlive).ToList();
        using var table  = ImRaii.Table("##TrainTable", 5, ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable | ImGuiTableFlags.Hideable);
        if (!table) return;

        ImGui.TableSetupColumn(Lang.Get("Serial"),                           ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn(Lang.Get("Name"),                             ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn(Lang.Get("Zone"),                             ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn(Lang.Get("Position"),                         ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn(Lang.Get("AutoMarksFinder-NearestAetheryte"), ImGuiTableColumnFlags.WidthStretch);

        ImGui.TableHeadersRow();

        for (var i = 0; i < source.Count; i++)
        {
            using var id    = ImRaii.PushId(i);
            using var group = ImRaii.Group();

            var info = source[i];
            ImGui.TableNextRow();

            ImGui.TableNextColumn();

            if (ImGuiOm.ButtonIcon($"删除{info}", FontAwesomeIcon.TrashAlt, Lang.Get("Delete")))
            {
                selected.MonstersFound.Remove(info);
                aliveMonitor?.RemoveTrack(selected, info);
                StopAutoBroadcast();
                config.Save(this);
            }

            ImGui.SameLine();

            using (ImRaii.Group())
            {
                if (info.IsAlive)
                {
                    ImGui.SameLine();

                    using (ImRaii.Disabled(i <= 0))
                    {
                        if (ImGuiOm.ButtonIcon("上移", FontAwesomeIcon.ArrowUp, Lang.Get("MoveUp")))
                        {
                            selected.MonstersFound.Swap
                            (
                                selected.MonstersFound.FindIndex(x => ReferenceEquals(x, info)),
                                selected.MonstersFound.FindIndex(x => ReferenceEquals(x, source[i - 1]))
                            );
                            StopAutoBroadcast();
                            config.Save(this);
                        }
                    }

                    ImGui.SameLine();

                    using (ImRaii.Disabled(i >= source.Count - 1))
                    {
                        if (ImGuiOm.ButtonIcon("下移", FontAwesomeIcon.ArrowDown, Lang.Get("MoveDown")))
                        {
                            selected.MonstersFound.Swap
                            (
                                selected.MonstersFound.FindIndex(x => ReferenceEquals(x, info)),
                                selected.MonstersFound.FindIndex(x => ReferenceEquals(x, source[i + 1]))
                            );
                            StopAutoBroadcast();
                            config.Save(this);
                        }
                    }
                }
            }

            ImGui.SameLine();
            ImGui.TextUnformatted($"{i + 1:00}");

            ImGui.TableNextColumn();
            DrawMonsterStatusToggle(selected, info);

            ImGui.SameLine();
            using (ImRaii.PushColor
                   (
                       ImGuiCol.Text,
                       info.IsAlive ?
                           KnownColor.LightGreen.ToVector4() :
                           KnownColor.Red.ToVector4()
                   ))
                ImGui.TextUnformatted($"{info.GetBNPCName()}");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatZoneInfo(info.GetZoneName(), info.Instance));

            ImGui.TableNextColumn();
            DrawPositionColumn(info);

            ImGui.TableNextColumn();
            DrawAetheryteColumn(info);
        }
    }

    private void DrawAutoBroadcastButton()
    {
        using var disabled = ImRaii.Disabled(scanner?.IsRunning == true);
        var label = autoBroadcast.On ?
                        Lang.Get("AutoMarksFinder-AutoBroadcastOn") :
                        Lang.Get("AutoMarksFinder-AutoBroadcast");
        var tooltip = autoBroadcast.On ?
                          Lang.Get("AutoMarksFinder-AutoBroadcastTooltipStop") :
                          Lang.Get("AutoMarksFinder-AutoBroadcastTooltipStart");
        bool clicked;

        if (autoBroadcast.On)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.18f, 0.55f, 0.30f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.22f, 0.65f, 0.35f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.15f, 0.50f, 0.28f, 1f));
            clicked = ImGuiOm.ButtonIcon(label, FontAwesomeIcon.Truck, tooltip);
            ImGui.PopStyleColor(3);
        }
        else
        {
            clicked = ImGuiOm.ButtonIcon(label, FontAwesomeIcon.Truck, tooltip);
        }

        if (clicked)
            ToggleAutoBroadcast();

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            ImGui.OpenPopup("AutoBroadcastPopup");

        using var popup = ImRaii.Popup("AutoBroadcastPopup");
        if (!popup)
            return;

        using (ImRaii.Heading1(Lang.Get("AutoMarksFinder-AutoBroadcastDepartTime")))
        {
            ImGui.SetNextItemWidth(160f * GlobalUIScale);
            ImGui.InputText("###AutoBroadcastDepartTime", ref autoBroadcast.DepartTimeInput, 16);
        }

        ImGui.Spacing();
        ImGui.Text
        (
            Lang.Get
            (
                "AutoMarksFinder-AutoBroadcastLocalTime",
                new Dictionary<string, object> { ["timeText"] = DateTime.Now.ToString("HH:mm:ss") }
            )
        );

        if (autoBroadcast.TimeCalibrated)
        {
            ImGui.Text
            (
                Lang.Get
                (
                    "AutoMarksFinder-AutoBroadcastServerTime",
                    new Dictionary<string, object> { ["timeText"] = CalibratedNow.ToString("HH:mm:ss") }
                )
            );
            ImGui.Text
            (
                Lang.Get
                (
                    "AutoMarksFinder-AutoBroadcastCalibOffset",
                    new Dictionary<string, object>
                    {
                        ["offsetText"] = autoBroadcast.TimeCalibOffsetSec.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture)
                    }
                )
            );
        }
        else
        {
            ImGui.Text(Lang.Get("AutoMarksFinder-AutoBroadcastServerTimeUncalibrated"));
        }

        var remain = autoBroadcast.DepartAt - CalibratedNow;
        var status = autoBroadcast switch
        {
            { On: true, Departed: false } when remain > TimeSpan.Zero =>
                Lang.Get
                (
                    "AutoMarksFinder-AutoBroadcastRemain",
                    new Dictionary<string, object> { ["remainingTimeText"] = remain.ToString(@"hh\:mm\:ss") }
                ),
            { On: true, Departed: false } => Lang.Get("AutoMarksFinder-AutoBroadcastDepartNow"),
            { On: true, Departed: true } =>
                Lang.Get
                (
                    "AutoMarksFinder-AutoBroadcastProgress",
                    new Dictionary<string, object> { ["pointIndex"] = autoBroadcast.Index + 1 }
                ),
            _ => string.Empty
        };

        if (!string.IsNullOrEmpty(status))
            ImGui.Text(status);

        ImGui.Spacing();

        if (ImGui.Button(Lang.Get("AutoMarksFinder-AutoBroadcastCalibrate")))
        {
            var serverNow = Framework.GetServerTime()
                                     .ToUTCDateTimeFromUnixSeconds()
                                     .ToLocalTime();
            autoBroadcast.TimeCalibOffsetSec = (DateTime.Now - serverNow).TotalSeconds;
            autoBroadcast.TimeCalibrated     = true;

            if (autoBroadcast is { On: true, Departed: false } &&
                TryParseDepartTime(out var parsed))
                autoBroadcast.DepartAt = parsed;
        }

        ImGui.SameLine();

        if (ImGui.Button(Lang.Get("AutoMarksFinder-AutoBroadcastApply")))
        {
            if (!TryParseDepartTime(out var parsed))
            {
                NotifyHelper.Instance().NotificationWarning
                    (Lang.Get("AutoMarksFinder-AutoBroadcastInvalidDepartTime"), Lang.Get("AutoMarksFinder-AutoBroadcast"));
                return;
            }

            autoBroadcast.DepartAt = parsed;
            NotifyHelper.Instance().NotificationInfo
            (
                Lang.Get
                (
                    "AutoMarksFinder-AutoBroadcastDepartTimeSet",
                    new Dictionary<string, object> { ["timeText"] = autoBroadcast.DepartAt.ToString("HH:mm:ss") }
                ),
                Lang.Get("AutoMarksFinder-AutoBroadcast")
            );
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawMonsterStatusToggle
    (
        NotoriousMonsterList?  train,
        NotoriousMonsterFound? info
    )
    {
        if (info == null) return;
        using var id = ImRaii.PushId(info.ToString());

        using (ImRaii.Group())
        {
            if (ImGuiOm.ButtonIcon("切换存活状态", FontAwesomeIcon.HeartCircleBolt, Lang.Get("AutoMarksFinder-SwitchMarkAlive")))
            {
                var isAlive = !info.IsAlive;

                if (train != null)
                {
                    aliveMonitor?.SetManualAlive(train, info, isAlive);
                    info.SetState(isAlive);
                }

                config.Save(this);
            }
        }
    }


    private void DrawTeleportToMonsterZone
    (
        NotoriousMonsterFound? info
    )
    {
        if (info == null) return;
        using var id = ImRaii.PushId(info.ToString());

        if (ImGuiOm.ButtonIcon("传送到目标区域", FontAwesomeIcon.Plane, Lang.Get("AutoMarksFinder-TeleportToMobsZone")))
        {
            if (GameState.TerritoryType != info.ZoneID)
            {
                TaskHelper.Enqueue(IsAbleToTeleport, "等待停止移动");
                TaskHelper.Enqueue
                (
                    () => AetheryteRecordManager.Instance().GetNearestAetheryte
                    (
                        info.ZoneID,
                        info.ZoneID == 818 ?
                            new(573f, 349.1f, -200f) :
                            info.Position
                    )?.TeleportTo(),
                    "传送至目标区域"
                );
            }

            if (info.Instance != 0)
            {
                TaskHelper.Enqueue(() => GameState.TerritoryType == info.ZoneID && UIModule.IsScreenReady(), "等待传送完毕");
                TaskHelper.Enqueue(() => ChatManager.Instance().SendMessage($"/pdr insc {info.Instance}"),   $"切换副本区至 {info.Instance} 线");
            }
        }
    }

    private void DrawTabItemScanner()
    {
        using var tabScanner = ImRaii.TabItem(Lang.Get("AutoMarksFinder-MarkScan"));
        if (!tabScanner) return;
        if (IObjectTable.Instance().LocalPlayer == null) return;

        DrawScanPointSection();

        ImGui.NewLine();

        DrawScannerSetting();

        ImGui.NewLine();

        DrawScannerOperation();

        ImGui.NewLine();

        DrawScanResult();
    }

    private void DrawTabItemSettings()
    {
        using var tabSettings = ImRaii.TabItem(Lang.Get("Settings"));
        if (!tabSettings) return;

        using (ImRaii.Heading1(Lang.Get("AutoMarksFinder-RelayChatTypes")))
        using (var combo = ImRaii.Combo
               (
                   "###RelayCommandCombo",
                   Lang.Get("AutoMarksFinder-Relay-SelectedChannels", new Dictionary<string, object> { ["count"] = config.RelayCommands.Count }),
                   ImGuiComboFlags.HeightLarge
               ))
        {
            if (combo)
            {
                foreach (var command in SUPPORTED_RELAY_COMMANDS)
                {
                    if (ImGui.Selectable(command, config.RelayCommands.Contains(command), ImGuiSelectableFlags.DontClosePopups))
                    {
                        if (!config.RelayCommands.Remove(command))
                            config.RelayCommands.Add(command);
                        config.Save(this);
                    }
                }
            }
        }

        using (ImRaii.Heading1(Lang.Get("AutoMarksFinder-Relay-Placeholders")))
            ImGuiOm.TextDisabledWrapped
            (
                Lang.Get
                (
                    "AutoMarksFinder-Relay-PlaceholderHelp",
                    new Dictionary<string, object>
                    {
                        ["name"]         = "{name}",
                        ["position"]     = "{position}",
                        ["instance"]     = "{instance}",
                        ["instanceText"] = "{instanceText}"
                    }
                )
            );

        DrawRelaySettings(RelayType.Position,      ref config.RelayPositionPattern);
        DrawRelaySettings(RelayType.WaitAetheryte, ref config.RelayWaitAetherytePattern);

        DrawRelaySettings(RelayType.BeforeStart, ref config.AutoRelayBeforeStartPattern);
        DrawRelaySettings(RelayType.AfterEnd,    ref config.AutoRelayAfterEndPattern);
    }

    private void DrawRelaySettings
    (
        RelayType  type,
        ref string content
    )
    {
        var title = type switch
        {
            RelayType.Position      => "AutoMarksFinder-Relay-Pattern-Position",
            RelayType.WaitAetheryte => "AutoMarksFinder-Relay-Pattern-WaitAetheryte",
            RelayType.BeforeStart   => "AutoMarksFinder-RelayAutoBroadcastStart",
            RelayType.AfterEnd      => "AutoMarksFinder-RelayAutoBroadcastEnd",
            _                       => throw new ArgumentOutOfRangeException(nameof(type))
        };

        using var heading = ImRaii.Heading1(Lang.Get(title));
        using var id      = ImRaii.PushId(type.ToString());

        var isMultiline = type is RelayType.BeforeStart or RelayType.AfterEnd;
        ImGui.SetNextItemWidth(500f * GlobalUIScale);
        if (isMultiline)
            ImGui.InputTextMultiline("###Content", ref content, 16_384, new Vector2(500f, 120f) * GlobalUIScale);
        else
            ImGui.InputText("###Content", ref content, 1024);

        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);

        var lineCount    = content.AsSpan().Count('\n') + 1;
        var tooManyLines = isMultiline && lineCount > MAX_RELAY_LINES;

        if (isMultiline)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"{lineCount}/{MAX_RELAY_LINES}");
        }

        if (tooManyLines)
            ImGuiOm.TextDisabledWrapped
            (
                Lang.Get
                (
                    "AutoMarksFinder-Relay-TooManyLines",
                    new Dictionary<string, object> { ["count"] = MAX_RELAY_LINES }
                )
            );

        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(content) || tooManyLines || relayTaskHelper.IsBusy || autoBroadcast.On))
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.ShareSquare, Lang.Get("Preview")))
                Relay(type, preview: true);

            ImGuiOm.TooltipHover(Lang.Get("AutoMarksFinder-Relay-PreviewHint"));
        }
    }

    private static string FormatZoneInfo
    (
        string location,
        uint   instance
    ) => Lang.Get
    (
        "AutoMarksFinder-ZoneInfoForEachMark",
        new Dictionary<string, object>
        {
            ["location"]     = location,
            ["instance"]     = instance,
            ["instanceText"] = instance.ToSESquareCount()
        }
    );

    private unsafe void DrawPositionColumn
    (
        NotoriousMonsterFound? info
    )
    {
        if (info == null) return;
        using var id = ImRaii.PushId($"{info}");

        var agentMap = AgentMap.Instance();

        if (ImGuiOm.ButtonIcon($"Relay{info}Position", FontAwesomeIcon.Microphone, Lang.Get("AutoMarksFinder-RelayPosition")))
            Relay(RelayType.Position, info);

        ImGui.SameLine();
        ImGui.TextUnformatted($"{info.Position:F0}");
        if (ImGui.IsItemHovered())
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            var title =
                $"{info.GetBNPCName()} " +
                $"({FormatZoneInfo(info.GetZoneName(), info.Instance)})";

            agentMap->SetFlagMapMarker(info.ZoneID, info.GetMapID(), info.Position);
            agentMap->OpenMap(info.GetMapID(), info.ZoneID, title);
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            agentMap->SetFlagMapMarker(info.ZoneID, info.GetMapID(), info.Position);
            NotifyHelper.Instance().NotificationInfo
            (
                Lang.Get("AutoMarksFinder-SetFlagMapMarkerSucceed"),
                Lang.Get("AutoMarksFinderTitle")
            );
        }
    }

    private unsafe void DrawAetheryteColumn
    (
        NotoriousMonsterFound? info
    )
    {
        if (info == null) return;
        using var id = ImRaii.PushId($"{info}");

        var agentMap = AgentMap.Instance();

        if (ImGuiOm.ButtonIcon($"Relay{info}Nearest", FontAwesomeIcon.ShareSquare, Lang.Get("AutoMarksFinder-RelayAetheryte")))
            Relay(RelayType.WaitAetheryte, info);

        ImGui.SameLine();
        DrawTeleportToMonsterZone(info);

        ImGui.SameLine();
        ImGui.TextUnformatted($"{info.NearestAetheryte?.PlaceName.Value.Name.ToString()}");
        if (ImGui.IsItemHovered())
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        if (ImGui.IsItemClicked())
        {
            agentMap->OpenMap(info.GetMapID(), info.ZoneID, $"{info.NearestAetheryte?.PlaceName.Value.Name.ToString()}");
            agentMap->SetFlagMapMarker(info.ZoneID, info.GetMapID(), info.NearestAetherytePosition ?? default);
        }
    }

    private void DrawScanPointSection()
    {
        var isZoneValid = config.ScanPoints.TryGetValue(GameState.TerritoryType, out var infos);

        using (ImRaii.Heading1(Lang.Get("CurrentZone")))
        {
            ImGui.TextUnformatted
            (
                $"{LuminaWrapper.GetZonePlaceName(GameState.TerritoryType)}" +
                $"（{Lang.Get("AutoMarksFinder-ScanPoints")}: {(isZoneValid ? infos.Count : 0)}）"
            );

            using (ImRaii.Disabled(spawnPointUpdateTask is { IsCompleted: false } || scanner?.IsRunning == true))
            {
                if (ImGui.SmallButton(Lang.Get("AutoMarksFinder-UpdateSpawnPoints")))
                    spawnPointUpdateTask = UpdateSpawnPoints(spawnPointUpdateCancellation.Token);
            }
        }
    }

    private async Task UpdateSpawnPoints(CancellationToken cancellationToken)
    {
        try
        {
            var content = await HTTPClientHelper.Instance().Get().GetStringAsync(SPAWN_POINTS_URL, cancellationToken).ConfigureAwait(false);
            var parsed  = JsonConvert.DeserializeObject<Dictionary<uint, List<NotoriousMonsterScanPoint>>>(content, JsonSerializerSettings.GetShared());
            if (parsed is not { Count: > 0 })
                return;

            await IFramework.Instance().RunOnFrameworkThread
            (() =>
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;

                    config.ScanPoints = parsed;
                    config.SelectedZones.IntersectWith(parsed.Where(pair => pair.Value.Count > 0).Select(pair => pair.Key));
                    scanZoneCombo = null;
                    config.Save(this);
                    NotifyHelper.Instance().NotificationInfo
                    (
                        Lang.Get("AutoMarksFinder-UpdateSpawnPointsRet", new Dictionary<string, object> { ["count"] = parsed.Sum(pair => pair.Value.Count) }),
                        Lang.Get("AutoMarksFinderTitle")
                    );
                }
            ).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                DLog.Error(exception);
                NotifyHelper.Instance().NotificationWarning(Lang.Get("AutoMarksFinder-UpdateSpawnPointsFailed"), Lang.Get("AutoMarksFinderTitle"));
            }
        }
    }

    private void DrawScannerOperation()
    {
        using var heading = ImRaii.Heading1(Lang.Get("Operation"));

        using (ImRaii.Disabled
               (
                   scanner?.IsRunning         == true             ||
                   scanner?.FoundOnes.Count   > 0                 ||
                   config.SelectedZones.Count == 0                ||
                   spawnPointUpdateTask is { IsCompleted: false } ||
                   TaskHelper.IsBusy                              ||
                   autoBroadcast.On
               ))
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Play, Lang.Get("Start")))
            {
                ClearScanner();
                scanner = new(this);
                scanner.StartScan();
            }
        }

        ImGui.SameLine();

        using (ImRaii.Disabled(scanner?.IsRunning != true))
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Stop, Lang.Get("Stop")))
                scanner?.Dispose();
        }

        ImGui.SameLine();

        using (ImRaii.Disabled(scanner == null || scanner.IsRunning))
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.TrashAlt, Lang.Get("Clear")))
                confirmClearScanResults = true;
        }

        if (confirmClearScanResults)
        {
            ImGui.OpenPopup("###ClearScanResults");
            confirmClearScanResults = false;
        }

        using var confirmation = ImRaii.Popup("###ClearScanResults");

        if (confirmation)
        {
            ImGui.TextUnformatted(Lang.Get("AutoMarksFinder-ClearResultsConfirm"));

            if (ImGui.Button(Lang.Get("Clear")))
            {
                ClearScanner();
                ImGui.CloseCurrentPopup();
            }
        }
    }

    private void DrawScannerSetting()
    {
        using var heading = ImRaii.Heading1(Lang.Get("AutoMarksFinder-TargetZones"));
        scanZoneCombo ??= new(config);
        ImGui.SetNextItemWidth(350f * GlobalUIScale);
        using var disabled = ImRaii.Disabled(scanner?.IsRunning == true);
        if (scanZoneCombo.DrawCheckbox())
            config.Save(this);
    }

    private unsafe void DrawScanResult()
    {
        using var heading = ImRaii.Heading1(Lang.Get("AutoMarksFinder-ScanResults"));

        if (scanner == null)
        {
            ImGuiOm.TextDisabledWrapped(Lang.Get("AutoMarksFinder-NoRunningScanner"));
            return;
        }

        using (ImRaii.Disabled(scanner.IsRunning || scanner.FoundOnes.Count == 0))
        {
            DrawTrainsSelectCombo(300f * GlobalUIScale, ref scanResultTrainIndex);
            ImGui.SameLine();

            using (ImRaii.Disabled(scanResultTrainIndex < 0 || scanResultTrainIndex >= config.Trains.Count))
            {
                var overwrite = ImGui.Button(Lang.Get("AutoMarksFinder-OverwriteResults"));
                ImGui.SameLine();
                var append = ImGui.Button(Lang.Get("AutoMarksFinder-AppendResults"));

                if (overwrite || append)
                {
                    var train = config.Trains[scanResultTrainIndex];
                    var found = scanner.FoundOnes.SelectMany(pair => pair.Value).ToHashSet();

                    if (ReferenceEquals(train, SelectedTrain))
                    {
                        StopAutoBroadcast();
                        smallCardMobIndex = 0;
                    }

                    if (overwrite)
                    {
                        aliveMonitor?.RemoveTrain(train);
                        train.MonstersFound = [.. found.Select(monster => monster.Copy())];
                        aliveMonitor?.InitializeTracks(train);
                    }
                    else
                    {
                        for (var index = 0; index < train.MonstersFound.Count; index++)
                        {
                            if (!found.TryGetValue(train.MonstersFound[index], out var observed))
                                continue;

                            aliveMonitor?.RemoveTrack(train, train.MonstersFound[index]);
                            train.MonstersFound[index] = observed.Copy();
                            found.Remove(observed);
                        }

                        train.MonstersFound.AddRange(found.Select(monster => monster.Copy()));
                    }

                    aliveMonitor?.RefreshTracking();
                    config.Save(this);
                    NotifyHelper.Instance().NotificationInfo(Lang.Get("SavedSuccessfully"));
                }
            }
        }

        foreach (var info in scanner.FoundOnes)
        {
            if (ImGui.CollapsingHeader($"{LuminaGetter.GetRow<TerritoryType>(info.Key)?.ExtractPlaceName()} ({info.Key})"))
            {
                foreach (var monster in info.Value)
                {
                    ImGui.TextUnformatted
                    (
                        monster.GetBNPCName() +
                        " "                   +
                        FormatZoneInfo($"{monster.Position:F0}", monster.Instance)
                    );
                    if (ImGui.IsItemHovered())
                        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

                    if (ImGui.IsItemClicked())
                    {
                        var instance = AgentMap.Instance();
                        instance->SetFlagMapMarker
                        (
                            monster.ZoneID,
                            monster.GetMapID(),
                            monster.Position
                        );
                        instance->OpenMap
                        (
                            monster.GetMapID(),
                            monster.ZoneID,
                            monster.GetBNPCName()
                        );
                    }
                }
            }
        }
    }

    private Task?                    spawnPointUpdateTask;
    private CancellationTokenSource? spawnPointUpdateCancellation;

    private class CustomOverlay : Window, IDisposable
    {
        private const ImGuiWindowFlags WINDOW_FLAGS =
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar;

        private readonly Action customDraw;

        public CustomOverlay
        (
            Action  customDraw,
            string? title = null
        ) : base
        (
            $"{(string.IsNullOrEmpty(title) ? string.Empty : title)}"
        )
        {
            Flags              = WINDOW_FLAGS;
            RespectCloseHotkey = false;
            this.customDraw    = customDraw;

            WindowManager.Instance().AddWindow(this);
        }

        public void Dispose() =>
            WindowManager.Instance().RemoveWindow(this);

        public override void Draw()
        {
            using var roundPush = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 10f);
            using var fontPush  = FontManager.Instance().UIFont.Push();
            customDraw();
        }
    }
}
