using System.Numerics;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using OmenTools.ImGuiOm.Widgets.MapRenderer;
using OmenTools.Info.Lumina;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using Action = System.Action;
using Map = Lumina.Excel.Sheets.Map;
using NotifyHelper = OmenTools.OmenService.NotifyHelper;
using Task = System.Threading.Tasks.Task;

namespace DailyRoutines.Modules.AutoMarksFinder;

public partial class AutoMarksFinder
{
    private readonly ImGuiMapRenderer scanRouteMapRenderer = new()
    {
        Zoomable             = true,
        Pannable             = true,
        EnableResizeGrip     = false,
        EnableDefaultMarkers = true
    };

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Command")}");

        using (ImRaii.PushIndent())
            ImGui.TextUnformatted($"{COMMAND} → {Lang.Get("AutoMarksFinder-HelpMessage")}");

        ImGui.Spacing();
        MarksFinderUI();
    }

    public void MarksFinderUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("PreCondition"));

        using (ImRaii.PushIndent())
            ImGui.TextUnformatted(Lang.Get("IPC-InstalledAndEnabledPlugin", "vnavmesh"));

        ImGui.NewLine();

        using var tabbar = ImRaii.TabBar("###TabBar");
        if (!tabbar) return;

        DrawTabItemTrain();

        DrawTabItemScanner();

        DrawTabItemSettings();
    }

    public void DrawSmallCard()
    {
        using var fontPush = FontManager.Instance().GetUIFont(config.UIScale).Push();

        var selected =
            selectedTrainIndex == -1 ||
            selectedTrainIndex > config.Trains.Count - 1 ?
                null :
                config.Trains[selectedTrainIndex];

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("AutoMarksFinder-SelectRoutes"));

        ImGui.SameLine();
        if (DrawTrainsSelectCombo(200f * GlobalUIScale, ref selectedTrainIndex))
            smallCardMobIndex = 0;

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
                config.Trains.Add(imported);
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
            using (FontManager.Instance().GetUIFont(config.UIScale + 0.2f).Push())
                ImGui.TextColored
                (
                    info.IsAlive ?
                        KnownColor.LightGreen.ToVector4() :
                        KnownColor.Red.ToVector4(),
                    $"{info.GetBNPCName()}"
                );

            ImGui.TextUnformatted
            (
                $"{Lang.Get
                ("AutoMarksFinder-ZoneInfoForEachMark",
                 info.GetZoneName(), info.Instance.ToSESquareCount())}"
            );
        }

        ImGui.TableNextColumn();
        {
            var info = source[smallCardMobIndex];
            using (FontManager.Instance().GetUIFont(config.UIScale + 0.2f).Push())
                ImGui.TextColored
                (
                    info.IsAlive ?
                        KnownColor.LightGreen.ToVector4() :
                        KnownColor.Red.ToVector4(),
                    $"{info.GetBNPCName()}"
                );

            ImGui.TextUnformatted
            (
                $"{Lang.Get
                ("AutoMarksFinder-ZoneInfoForEachMark",
                 info.GetZoneName(), info.Instance.ToSESquareCount())}"
            );
        }

        ImGui.TableNextColumn();

        if (smallCardMobIndex < source.Count - 1)
        {
            var info = source[smallCardMobIndex + 1];
            using (FontManager.Instance().GetUIFont(config.UIScale + 0.2f).Push())
                ImGui.TextColored
                (
                    info.IsAlive ?
                        KnownColor.LightGreen.ToVector4() :
                        KnownColor.Red.ToVector4(),
                    $"{info.GetBNPCName()}"
                );

            ImGui.TextUnformatted
            (
                $"{Lang.Get
                ("AutoMarksFinder-ZoneInfoForEachMark",
                 info.GetZoneName(), info.Instance.ToSESquareCount())}"
            );
        }

        ImGui.TableNextRow();

        ImGui.TableNextColumn();

        ImGui.TableNextColumn();
        {
            var info = source[smallCardMobIndex];
            DrawPositionColumn(info);

            ImGui.SameLine();
            DrawAetheryteColumn(info);

            DrawMonsterStatusToggle(info);

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

            DrawMonsterStatusToggle(info);

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

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoMarksFinder-SelectRoutes")}:");

        ImGui.SameLine();
        DrawTrainsSelectCombo(200f * GlobalUIScale, ref selectedTrainIndex);

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
                                            startAetheryte == x.RowId,
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
                        selected.Reorder();
                }
            }

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
                config.Trains.Add(imported);
                config.Save(this);
            }
        }

        ImGui.Spacing();

        if (selected == null) return;

        var       source = selected.MonstersFound.OrderByDescending(x => x.IsAlive).ToList();
        using var table  = ImRaii.Table("##TrainTable", 6, ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable | ImGuiTableFlags.Hideable);
        if (!table) return;

        ImGui.TableSetupColumn(Lang.Get("Serial"),                           ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn(Lang.Get("Name"),                             ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn(Lang.Get("Zone"),                             ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn(Lang.Get("AutoMarksFinder-Rank"),             ImGuiTableColumnFlags.WidthFixed);
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
                selected.MonstersFound.Remove(info);

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
                            selected.MonstersFound.Swap(i, i - 1);
                            config.Save(this);
                        }
                    }

                    ImGui.SameLine();

                    using (ImRaii.Disabled(i >= source.Count - 1))
                    {
                        if (ImGuiOm.ButtonIcon("下移", FontAwesomeIcon.ArrowDown, Lang.Get("MoveDown")))
                        {
                            selected.MonstersFound.Swap(i, i + 1);
                            config.Save(this);
                        }
                    }
                }
            }

            ImGui.SameLine();
            ImGui.TextUnformatted($"{i + 1:00}");

            ImGui.TableNextColumn();
            DrawMonsterStatusToggle(info);

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
            ImGui.TextUnformatted(Lang.Get("AutoMarksFinder-ZoneInfoForEachMark", info.GetZoneName(), info.Instance.ToSESquareCount()));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Lang.Get("AutoMarksFinder-RankForEachMark", info.Rank));

            ImGui.TableNextColumn();
            DrawPositionColumn(info);

            ImGui.TableNextColumn();
            DrawAetheryteColumn(info);
        }
    }

    private static void DrawMonsterStatusToggle
    (
        NotoriousMonsterFound? info
    )
    {
        if (info == null) return;
        using var id = ImRaii.PushId(info.ToString());

        using (ImRaii.Group())
        {
            if (ImGuiOm.ButtonIcon("切换存活状态", FontAwesomeIcon.HeartCircleBolt, Lang.Get("AutoMarksFinder-SwitchMarkAlive")))
                info.SetState(info.IsAlive ^ true);
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

        DrawScannerSection();
    }

    private void DrawScanRoutePreview()
    {
        if (scanner == null) return;

        if (ImGui.Begin
            (
                "AutoMarksFinder-ScannerRouteMap",
                ImGuiWindowFlags.AlwaysAutoResize  |
                ImGuiWindowFlags.NoTitleBar        |
                ImGuiWindowFlags.NoDocking         |
                ImGuiWindowFlags.NoCollapse        |
                ImGuiWindowFlags.NoScrollbar       |
                ImGuiWindowFlags.NoScrollWithMouse |
                ImGuiWindowFlags.NoBackground
            ))
        {
            var (scanZoneID, scanPoints, currentIndex) = scanner.GetCurrentScanState();
            if (scanZoneID != 0 && scanPoints.Count > 0)
                DrawScanRouteMap(scanZoneID, scanPoints, currentIndex);

            ImGui.End();
        }
    }

    private void DrawScanRouteMap
    (
        uint                            zoneID,
        List<NotoriousMonsterScanPoint> points,
        int                             currentIndex = -1
    )
    {
        if (!LuminaGetter.TryGetRow<TerritoryType>(zoneID, out var territory) ||
            !LuminaGetter.TryGetRow<Map>(territory.Map.RowId, out var mapData))
            return;

        scanRouteMapRenderer.SetMap(mapData.RowId);

        currentIndex--;

        scanRouteMapRenderer.OnCustomMapDraw = (r, drawList) =>
        {
            if (points.Count > 1)
            {
                for (var i = 0; i < points.Count - 1; i++)
                {
                    var startPos = r.WorldToScreen(points[i].Position);
                    var endPos   = r.WorldToScreen(points[i + 1].Position);

                    var lineColor = i < currentIndex ?
                                        ImGui.ColorConvertFloat4ToU32(KnownColor.GreenYellow.ToVector4()) :
                                        ImGui.ColorConvertFloat4ToU32(KnownColor.Orange.ToVector4());

                    drawList.AddLine(startPos, endPos, lineColor, 2f * GlobalUIScale);
                }
            }
        };

        scanRouteMapRenderer.OnCustomForegroundDraw = (r, drawList) =>
        {
            if (IObjectTable.Instance().LocalPlayer is { } localPlayer &&
                GameState.TerritoryType == zoneID)
            {
                var playerPos   = r.WorldToScreen(localPlayer.Position);
                var playerColor = ImGui.ColorConvertFloat4ToU32(KnownColor.LightSkyBlue.ToVector4());
                drawList.AddCircleFilled(playerPos, 6f * GlobalUIScale, playerColor);
                drawList.AddCircle(playerPos, 6f       * GlobalUIScale, ImGui.ColorConvertFloat4ToU32(Vector4.One), 0, 2f * GlobalUIScale);
            }
        };

        scanRouteMapRenderer.ClearMarkers();

        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];

            Vector4 pointColor;
            if (i < currentIndex)
                pointColor = KnownColor.GreenYellow.ToVector4();
            else if (i == currentIndex)
                pointColor = KnownColor.Red.ToVector4();
            else
                pointColor = KnownColor.Orange.ToVector4();

            var marker = new ImGuiMapMarker
            {
                ID          = $"ScanPoint_{i}",
                Position    = point.Position,
                Color       = ImGui.ColorConvertFloat4ToU32(pointColor),
                Size        = new(8f * GlobalUIScale),
                ShowLabel   = false,
                ShowTooltip = true,
                TooltipText = $"#{i + 1} {point.Position:F0}"
            };
            scanRouteMapRenderer.AddMarker(marker);
        }

        scanRouteMapRenderer.Draw(ScaledVector2(300f));
    }

    private void DrawTabItemSettings()
    {
        using var tabSettings = ImRaii.TabItem(Lang.Get("Settings"));
        if (!tabSettings) return;

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("FontScale")}:");

        for (var i = 0.6f; i < 1.8f; i += 0.2f)
        {
            var fontScale = (float)Math.Round(i, 1);

            using (ImRaii.Disabled(config.UIScale == fontScale))
            {
                ImGui.SameLine();

                if (ImGui.Button($"{fontScale}##FontScale"))
                {
                    config.UIScale = fontScale;
                    config.Save(this);
                }
            }
        }

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.Orange.ToVector4(), Lang.Get("AutoMarksFinder-RelaySetting"));
        ImGuiOm.HelpMarker(Lang.Get("AutoMarksFinder-RelayArgsHelp"));

        using var indent = ImRaii.PushIndent();

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoMarksFinder-RelayPosition")}:");

        ImGui.SameLine();
        ImGui.InputText("###RelayPositionMacro", ref config.RelayMacroPattern, 255);
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);

        ImGui.SameLine();

        if (ImGuiOm.ButtonIcon("PreviewPosition", FontAwesomeIcon.ShareSquare, Lang.Get("Preview")))
        {
            ChatManager.Instance().SendMessage
            (
                $"/e {string.Format
                (config.RelayMacroPattern, "<pos>", "<1>",
                 !InstancesManager.IsInstancedArea ?
                     null :
                     Lang.Get("AutoMarksFinder-RelayInstanceDisplay", InstancesManager.CurrentInstance.ToSESquareCount()))}"
            );
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoMarksFinder-RelayAetheryte")}:");

        ImGui.SameLine();
        ImGui.InputText("###RelayAetheryteMacro", ref config.WaitMacroPattern, 255);
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);

        ImGui.SameLine();

        if (ImGuiOm.ButtonIcon("PreviewAetheryte", FontAwesomeIcon.ShareSquare, Lang.Get("Preview")))
        {
            ChatManager.Instance().SendMessage
            (
                $"/e {string.Format
                (config.WaitMacroPattern, "<pos>", "<1>",
                 !InstancesManager.IsInstancedArea ?
                     null :
                     Lang.Get("AutoMarksFinder-RelayInstanceDisplay", InstancesManager.CurrentInstance.ToSESquareCount()))}"
            );
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoMarksFinder-RelayChatTypes")}:");

        ImGui.SameLine();
        using var combo = ImRaii.Combo
        (
            "###RelayCommandCombo",
            Lang.Get("AutoOpenMapLinks-AlreadyAddedChannelCount", config.RelayCommands.Count),
            ImGuiComboFlags.HeightLarge
        );

        if (combo)
        {
            foreach (var command in SupportedRelayCommands)
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

    private unsafe void DrawPositionColumn
    (
        NotoriousMonsterFound? info
    )
    {
        if (info == null) return;
        using var id = ImRaii.PushId($"{info}");

        var agentMap = AgentMap.Instance();

        if (ImGuiOm.ButtonIcon($"Relay{info}Position", FontAwesomeIcon.Microphone, Lang.Get("AutoMarksFinder-RelayPosition")))
        {
            agentMap->OpenMap(info.GetMapID(), info.ZoneID, $"{info.GetBNPCName()}");
            agentMap->SetFlagMapMarker(info.ZoneID, info.GetMapID(), info.Position);

            foreach (var command in config.RelayCommands)
            {
                var formattedPosition = FormatPattern
                (
                    config.RelayMacroPattern,
                    "<flag>",
                    info.GetBNPCName(),
                    info.Instance == 0 ?
                        null :
                        $"{Lang.Get("AutoMarksFinder-RelayInstanceDisplay", info.Instance.ToSESquareCount())}"
                );
                ChatManager.Instance().SendMessage($"{command} {formattedPosition}");
            }
        }

        ImGui.SameLine();
        ImGui.TextUnformatted($"{info.Position:F0}");
        if (ImGui.IsItemHovered())
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            var title =
                $"{info.GetBNPCName()} " +
                $"({Lang.Get("AutoMarksFinder-ZoneInfoForEachMark", info.GetZoneName(), info.Instance.ToSESquareCount())})";

            agentMap->SetFlagMapMarker(info.ZoneID, info.GetMapID(), info.Position);
            agentMap->OpenMap(info.GetMapID(), info.ZoneID, title);
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            agentMap->SetFlagMapMarker(info.ZoneID, info.GetMapID(), info.Position);
            NotifyHelper.Instance().NotificationInfo
            (
                Lang.Get
                (
                    "AutoMarksFinder-SetFlagMapMarkerSucceed",
                    info.GetZoneName(),
                    $"{info.Position:F0}"
                ),
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
        {
            if (info.NearestAetherytePosition is not null)
            {
                agentMap->OpenMap(info.GetMapID(), info.ZoneID, $"{info.NearestAetheryte?.PlaceName.Value.Name.ToString()}");
                agentMap->SetFlagMapMarker(info.ZoneID, info.GetMapID(), (Vector3)info.NearestAetherytePosition);

                foreach (var command in config.RelayCommands)
                {
                    var formattedAetheryte = FormatPattern
                    (
                        config.WaitMacroPattern,
                        "<flag>",
                        info.GetBNPCName(),
                        info.Instance == 0 ?
                            null :
                            $"{Lang.Get("AutoMarksFinder-RelayInstanceDisplay", info.Instance.ToSESquareCount())}"
                    );
                    ChatManager.Instance().SendMessage($"{command} {formattedAetheryte}");
                }
            }
        }

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

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("CurrentZone")}:");

        ImGui.SameLine();
        ImGui.TextUnformatted($"{LuminaWrapper.GetZonePlaceName(GameState.TerritoryType)} ({GameState.TerritoryType})");

        ImGui.SameLine(0, 8f * GlobalUIScale);
        ImGui.TextDisabled("|");

        ImGui.SameLine(0, 8f * GlobalUIScale);
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoMarksFinder-ScanPoints")}:");

        ImGui.SameLine();
        ImGui.TextUnformatted
        (
            isZoneValid ?
                infos.Count.ToString() :
                "0"
        );

        ImGui.SameLine(0, 8f * GlobalUIScale);

        using (ImRaii.Disabled(spawnPointUpdateTask is { IsCompleted: false }))
        {
            if (ImGui.SmallButton(Lang.Get("AutoMarksFinder-UpdateSpawnPoints")))
            {
                spawnPointUpdateTask = Task.Run
                (async () =>
                    {
                        try
                        {
                            const string URL = "https://gh.atmoomen.top/StaticAssets/main/DailyRoutines/module/AutoMarksFinder_SpawnPoints.json";

                            var content = await HTTPClientHelper.Instance().Get().GetStringAsync(URL).ConfigureAwait(false);

                            var parsed = JsonConvert.DeserializeObject<Dictionary<uint, List<NotoriousMonsterScanPoint>>>
                                (content, JsonSerializerSettings.GetShared());
                            if (parsed is not { Count: > 0 }) return;

                            config.ScanPoints = parsed;
                            config.Save(ModuleManager.Instance().GetModule<AutoMarksFinder>());

                            NotifyHelper.Instance().NotificationInfo
                            (
                                Lang.Get("AutoMarksFinder-UpdateSpawnPointsRet", parsed.SelectMany(x => x.Value).Count()),
                                Lang.Get("AutoMarksFinderTitle")
                            );
                        }
                        catch
                        {
                            // ignored
                        }
                    }
                ).ContinueWith(_ => spawnPointUpdateTask = null);
            }
        }
    }

    private void DrawScannerSection()
    {
        DrawScannerOperation();

        ImGui.NewLine();

        DrawScannerSetting();

        ImGui.NewLine();

        DrawScanResult();
    }

    private void DrawScannerOperation()
    {
        ImGui.TextColored(KnownColor.Orange.ToVector4(), Lang.Get("Operation"));
        ImGui.Spacing();

        using (ImRaii.Disabled(scanner != null || config.SelectedZones.Count == 0))
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Play, Lang.Get("Start")))
            {
                ClearScanner();
                scanner = new(this, config.SelectedRanks, config.SelectedZones);
                scanner.StartScan();
            }
        }

        ImGui.SameLine();

        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Stop, Lang.Get("Stop")))
            StopScanner();

        ImGui.SameLine();

        using (ImRaii.Disabled(scanner == null))
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.TrashAlt, Lang.Get("Clear")))
                ClearScanner();
        }
    }

    private void DrawScannerSetting()
    {
        ImGui.TextColored(KnownColor.Orange.ToVector4(), Lang.Get("AutoMarksFinder-ScannerSettings"));
        ImGui.Spacing();

        using var indent = ImRaii.PushIndent();

        ImGui.SetNextItemWidth(150f * GlobalUIScale);
        ImGui.InputInt($"{Lang.Get("AutoMarksFinder-PointDelayMS")}##AutoMarksFinder-PointDelayMS", ref config.DelayMS, 500);
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);

        ImGui.SameLine(0, 8f * GlobalUIScale);
        ImGui.TextDisabled("|");

        ImGui.SameLine(0, 8f * GlobalUIScale);
        ImGui.TextUnformatted($"{Lang.Get("AutoMarksFinder-TargetRank")}:");

        ImGui.SameLine(0, 8f * GlobalUIScale);

        using (ImRaii.Group())
        {
            var isFirst = true;

            foreach (var rank in Enum.GetValues<NotoriousMonsterRank>())
            {
                if (rank == NotoriousMonsterRank.S) continue;

                if (!isFirst)
                    ImGui.SameLine(0, 4f * GlobalUIScale);

                isFirst = false;

                var isContainRank = config.SelectedRanks.Contains(rank);

                if (ImGui.Checkbox(rank.ToString(), ref isContainRank))
                {
                    if (!config.SelectedRanks.Remove(rank))
                        config.SelectedRanks.Add(rank);

                    config.Save(this);
                }
            }
        }

        var data = config.ScanPoints
                         .Select
                         (x => new
                             {
                                 Zone   = LuminaGetter.GetRow<TerritoryType>(x.Key).GetValueOrDefault(),
                                 Points = x.Value.Count
                             }
                         )
                         .GroupBy(x => x.Zone.ExVersion.RowId)
                         .OrderBy(x => x.Key)
                         .ToDictionary(x => x.Key, x => x.ToList());
        ImGui.SetNextItemWidth(250f * GlobalUIScale);

        using (var combo = ImRaii.Combo
               (
                   $"{Lang.Get("AutoMarksFinder-TargetZones")}###ZoneSelectCombo",
                   Lang.Get("AutoMarksFinder-SelectedZonePreview", config.SelectedZones.Count),
                   ImGuiComboFlags.HeightLarge
               ))
        {
            if (combo)
            {
                var isFirst = true;

                foreach (var (version, zones) in data)
                {
                    if (!isFirst)
                    {
                        ImGui.Spacing();

                        ImGui.Separator();
                        ImGui.Separator();

                        ImGui.Spacing();
                    }

                    isFirst = false;

                    ImGui.TextUnformatted($"{version + 2}.0");
                    if (ImGui.IsItemHovered())
                        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

                    if (ImGui.IsItemClicked())
                    {
                        var zonesToOperate = zones.Select(x => x.Zone.RowId).ToArray();
                        if (config.SelectedZones.ContainsAny(zonesToOperate))
                            config.SelectedZones.RemoveRange(zonesToOperate);
                        else
                            config.SelectedZones.AddRange(zonesToOperate);

                        config.Save(this);
                    }

                    foreach (var zone in zones)
                    {
                        if (ImGui.Selectable
                                ($"{zone.Zone.ExtractPlaceName()}", config.SelectedZones.Contains(zone.Zone.RowId), ImGuiSelectableFlags.DontClosePopups))
                        {
                            if (!config.SelectedZones.Remove(zone.Zone.RowId))
                                config.SelectedZones.Add(zone.Zone.RowId);

                            config.Save(this);
                        }
                    }
                }
            }
        }
    }

    private unsafe void DrawScanResult()
    {
        ImGui.TextColored(KnownColor.Orange.ToVector4(), Lang.Get("AutoMarksFinder-ScanResults"));
        ImGui.Spacing();

        using var indent = ImRaii.PushIndent();

        if (scanner == null)
        {
            ImGui.TextUnformatted(Lang.Get("AutoMarksFinder-NoRunningScanner"));
            return;
        }

        using (ImRaii.Disabled(scanner.FoundOnes.Count == 0))
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("AutoMarksFinder-SaveTo"));

            ImGui.SameLine();
            DrawTrainsSelectCombo(400f * GlobalUIScale, ref selectedTrainIndex);

            var selected =
                selectedTrainIndex == -1 ||
                selectedTrainIndex > config.Trains.Count - 1 ?
                    null :
                    config.Trains[selectedTrainIndex];

            using (ImRaii.Disabled(selected == null))
            {
                ImGui.SameLine();

                if (ImGui.Button(Lang.Get("Save")))
                {
                    var result = scanner.FoundOnes.SelectMany(x => x.Value).Distinct().ToList();
                    config.Trains[selectedTrainIndex].MonstersFound.AddRange(result);
                    config.Save(ModuleManager.Instance().GetModule<AutoMarksFinder>());

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
                        Lang.Get
                        (
                            "AutoMarksFinder-ZoneInfoForEachMark",
                            monster.Position,
                            monster.Instance
                        )
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

    private Task? spawnPointUpdateTask;

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
