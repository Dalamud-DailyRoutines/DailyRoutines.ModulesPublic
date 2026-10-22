using System.Globalization;
using System.Numerics;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using OmenTools.Dalamud;
using OmenTools.Extensions;
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

    private readonly ImGuiMapRenderer recorderRouteMapRenderer = new()
    {
        Zoomable             = true,
        Pannable             = true,
        EnableResizeGrip     = false,
        EnableDefaultMarkers = true
    };

    private const float RECORDER_MIN_DISTANCE = 10f;

    private const string UPSTREAM_POINTS_URL =
        "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/AutoMarksFinder/SpawnPoints.json";

    private const string UPSTREAM_POINTS_URL_FALLBACK =
        "https://raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/AutoMarksFinder/SpawnPoints.json";

    private Task? spawnPointUpdateTask;

    private bool recorderRecording;
    private int  recorderSelectedPointIndex = -1;
    private uint recorderZoneID;

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

        DrawTabItemRecorder();

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
        {
            smallCardMobIndex = 0;
            ResetAutoPilot();
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

                var importedTracks = GetTrainTracks(imported);
                foreach (var key in imported.MonstersFound.Select(x => (x.DataID, x.ZoneID, x.Instance)).Distinct())
                    importedTracks[key] = new AliveTrack { ManualAlive = true };

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

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoMarksFinder-SelectRoutes")}:");

        ImGui.SameLine();
        if (DrawTrainsSelectCombo(200f * GlobalUIScale, ref selectedTrainIndex))
        {
            smallCardMobIndex = 0;
            ResetAutoPilot();
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
                    {
                        selected.Reorder();
                        ResetAutoPilot();
                        config.Save(this);
                    }
                }
            }

            ImGui.SameLine(0, 8f * GlobalUIScale);
            DrawAutoPilotButton();

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

                var importedTracks = GetTrainTracks(imported);
                foreach (var key in imported.MonstersFound.Select(x => (x.DataID, x.ZoneID, x.Instance)).Distinct())
                    importedTracks[key] = new AliveTrack { ManualAlive = true };

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
            {
                selected.MonstersFound.Remove(info);
                ResetAutoPilot();
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
                            selected.MonstersFound.Swap(selected.MonstersFound.IndexOf(info),
                                                        selected.MonstersFound.IndexOf(source[i - 1]));
                            ResetAutoPilot();
                            config.Save(this);
                        }
                    }

                    ImGui.SameLine();

                    using (ImRaii.Disabled(i >= source.Count - 1))
                    {
                        if (ImGuiOm.ButtonIcon("下移", FontAwesomeIcon.ArrowDown, Lang.Get("MoveDown")))
                        {
                            selected.MonstersFound.Swap(selected.MonstersFound.IndexOf(info),
                                                        selected.MonstersFound.IndexOf(source[i + 1]));
                            ResetAutoPilot();
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
            ImGui.TextUnformatted(Lang.Get("AutoMarksFinder-ZoneInfoForEachMark", info.GetZoneName(), info.Instance.ToSESquareCount()));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Lang.Get("AutoMarksFinder-RankForEachMark", info.Rank));

            ImGui.TableNextColumn();
            DrawPositionColumn(info);

            ImGui.TableNextColumn();
            DrawAetheryteColumn(info);
        }
    }

    private void DrawAutoPilotButton()
    {
        var label     = autoPilotOn ? Lang.Get("AutoMarksFinder-AutoPilotOn") : Lang.Get("AutoMarksFinder-AutoPilot");
        var tooltip   = autoPilotOn ? Lang.Get("AutoMarksFinder-AutoPilotTooltipStop") : Lang.Get("AutoMarksFinder-AutoPilotTooltipStart");
        var clicked   = false;

        if (autoPilotOn)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.18f, 0.55f, 0.30f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.22f, 0.65f, 0.35f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.15f, 0.50f, 0.28f, 1f));
            clicked = ImGuiOm.ButtonIcon(label, FontAwesomeIcon.Truck, tooltip);
            ImGui.PopStyleColor(3);
        }
        else
        {
            clicked = ImGuiOm.ButtonIcon(label, FontAwesomeIcon.Truck, tooltip);
        }

        if (clicked)
            ToggleAutoPilot();

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            ImGui.OpenPopup("AutoPilotPopup");

        using var popup = ImRaii.Popup("AutoPilotPopup");
        if (!popup) return;

        ImGui.Text(Lang.Get("AutoMarksFinder-AutoPilotSettings"));

        ImGui.Spacing();
        ImGui.Text(Lang.Get("AutoMarksFinder-AutoPilotDepartTime"));
        ImGui.SetNextItemWidth(160f * GlobalUIScale);
        ImGui.InputText("###AutoPilotDepartTime", ref departTimeInput, 16);

        ImGui.Spacing();
        ImGui.Text(Lang.Get("AutoMarksFinder-AutoPilotLocalTime", DateTime.Now.ToString("HH:mm:ss")));

        if (timeCalibrated)
        {
            ImGui.Text(Lang.Get("AutoMarksFinder-AutoPilotServerTime",
                DateTime.Now.AddSeconds(-timeCalibOffsetSec).ToString("HH:mm:ss")));
            ImGui.Text(Lang.Get("AutoMarksFinder-AutoPilotCalibOffset",
                timeCalibOffsetSec.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture)));
        }
        else
        {
            ImGui.Text(Lang.Get("AutoMarksFinder-AutoPilotServerTimeUncalibrated"));
        }

        if (autoPilotOn && !autoPilotDeparted)
        {
            var remain = departAt - CalibratedNow;
            if (remain > TimeSpan.Zero)
                ImGui.Text(Lang.Get("AutoMarksFinder-AutoPilotRemain", remain.ToString("hh\\:mm\\:ss")));
            else
                ImGui.Text(Lang.Get("AutoMarksFinder-AutoPilotDepartNow"));
        }
        else if (autoPilotOn && autoPilotDeparted)
        {
            ImGui.Text(Lang.Get("AutoMarksFinder-AutoPilotProgress", autoPilotIndex + 1));
        }

        ImGui.Spacing();

        if (ImGui.Button(Lang.Get("AutoMarksFinder-AutoPilotCalibrate")))
        {
            var serverNow = Framework.GetServerTime()
                                    .ToUTCDateTimeFromUnixSeconds()
                                    .ToLocalTime();
            timeCalibOffsetSec = (DateTime.Now - serverNow).TotalSeconds;
            timeCalibrated     = true;

            if (autoPilotOn && !autoPilotDeparted && TryParseDepartTime(out var parsed))
                departAt = parsed;
        }

        ImGui.SameLine();
        if (ImGui.Button(Lang.Get("AutoMarksFinder-AutoPilotApply")))
        {
            if (TryParseDepartTime(out var parsed))
            {
                departAt = parsed;
                NotifyHelper.Instance().NotificationInfo
                (
                    Lang.Get("AutoMarksFinder-AutoPilotDepartTimeSet", departAt.ToString("HH:mm:ss")),
                    Lang.Get("AutoMarksFinder-AutoPilot")
                );
                ImGui.CloseCurrentPopup();
            }
        }
    }

    private void DrawMonsterStatusToggle
    (
        NotoriousMonsterList? train,
        NotoriousMonsterFound? info
    )
    {
        if (info == null) return;
        using var id = ImRaii.PushId(info.ToString());

        using (ImRaii.Group())
        {
            if (ImGuiOm.ButtonIcon("切换存活状态", FontAwesomeIcon.HeartCircleBolt, Lang.Get("AutoMarksFinder-SwitchMarkAlive")))
            {
                var key = (info.DataID, info.ZoneID, info.Instance);
                var isAlive = !info.IsAlive;

                if (train != null)
                {
                    SetGroupState(train.MonstersFound
                                       .Where(x => (x.DataID, x.ZoneID, x.Instance) == key), isAlive);

                    var trainTracks = GetTrainTracks(train);
                    if (!trainTracks.TryGetValue(key, out var track))
                    {
                        track             = new AliveTrack();
                        trainTracks[key] = track;
                    }

                    track.SeenThisStay   = false;
                    track.HasBeenDamaged = false;
                    track.AutoKilled     = false;
                    track.ManualAlive    = isAlive;
                    track.MissingSince   = 0;
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
                DrawScanRouteMap(scanRouteMapRenderer, scanZoneID, scanPoints, currentIndex);

            ImGui.End();
        }
    }

    private void DrawScanRouteMap
    (
        ImGuiMapRenderer                renderer,
        uint                            zoneID,
        List<NotoriousMonsterScanPoint> points,
        int                             currentIndex = -1
    )
    {
        if (!LuminaGetter.TryGetRow<TerritoryType>(zoneID, out var territory) ||
            !LuminaGetter.TryGetRow<Map>(territory.Map.RowId, out var mapData))
            return;

        renderer.SetMap(mapData.RowId);

        currentIndex--;

        renderer.OnCustomMapDraw = (r, drawList) =>
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

        renderer.OnCustomForegroundDraw = (r, drawList) =>
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

        renderer.ClearMarkers();

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
            renderer.AddMarker(marker);
        }

        renderer.Draw(ScaledVector2(300f));
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
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoMarksFinder-RelayAutoPilotStart")}:");

        ImGui.SameLine();
        ImGui.InputText("###RelayAutoPilotStartMacro", ref config.AutoPilotStartMacroPattern, 255);
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);

        ImGui.SameLine();

        if (ImGuiOm.ButtonIcon("PreviewAutoPilotStart", FontAwesomeIcon.ShareSquare, Lang.Get("Preview")))
        {
            if (!string.IsNullOrWhiteSpace(config.AutoPilotStartMacroPattern))
                ChatManager.Instance().SendMessage($"/e {config.AutoPilotStartMacroPattern}");
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoMarksFinder-RelayAutoPilotEnd")}:");

        ImGui.SameLine();
        ImGui.InputText("###RelayAutoPilotEndMacro", ref config.AutoPilotEndMacroPattern, 255);
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);

        ImGui.SameLine();

        if (ImGuiOm.ButtonIcon("PreviewAutoPilotEnd", FontAwesomeIcon.ShareSquare, Lang.Get("Preview")))
        {
            if (!string.IsNullOrWhiteSpace(config.AutoPilotEndMacroPattern))
                ChatManager.Instance().SendMessage($"/e {config.AutoPilotEndMacroPattern}");
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

    private void DrawTabItemRecorder()
    {
        using var tabRecorder = ImRaii.TabItem(Lang.Get("AutoMarksFinder-Recorder"));
        if (!tabRecorder) return;
        if (IObjectTable.Instance().LocalPlayer == null) return;

        var zoneID   = GameState.TerritoryType;
        var isLocked = scanner != null;

        if (recorderZoneID != zoneID)
        {
            recorderZoneID             = zoneID;
            recorderRecording          = false;
            recorderSelectedPointIndex = -1;
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("CurrentZone")}:");

        ImGui.SameLine();
        ImGui.TextUnformatted($"{LuminaWrapper.GetZonePlaceName(zoneID)} ({zoneID})");

        ImGui.SameLine(0f, 8f * GlobalUIScale);
        ImGui.TextDisabled("|");
        ImGui.SameLine(0f, 8f * GlobalUIScale);
        ImGui.TextColored
        (
            KnownColor.Orange.ToVector4(),
            Lang.Get(config.UseUpstreamPoints ? "AutoMarksFinder-ScanSourceUpstream" : "AutoMarksFinder-ScanSourceLocal")
        );

        ImGui.NewLine();

        if (!config.ScanRoutes.TryGetValue(zoneID, out var route))
        {
            using (ImRaii.Disabled(isLocked))
            {
                if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, Lang.Get("AutoMarksFinder-NewRoute")))
                {
                    var now = StandardTimeManager.Instance().UTCNowOffset.ToUnixTimeMilliseconds();

                    config.ScanRoutes[zoneID] = new()
                    {
                        Name        = LuminaWrapper.GetZonePlaceName(zoneID),
                        ZoneID      = zoneID,
                        CreatedTime = now,
                        UpdatedTime = now
                    };
                    config.Save(this);
                }
            }

            ImGui.NewLine();
            ImGui.TextDisabled(Lang.Get("AutoMarksFinder-NoPoints"));
            return;
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoMarksFinder-RouteName")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(220f * GlobalUIScale);

        using (ImRaii.Disabled(isLocked))
        {
            ImGui.InputText("###AutoMarksFinderRouteName", ref route.Name, 64);

            if (ImGui.IsItemDeactivatedAfterEdit())
                SaveRecorderRoute(route);
        }

        ImGui.SameLine();

        using (ImRaii.Disabled(isLocked))
        {
            if (ImGuiOm.ButtonIcon("AutoMarksFinder-DeleteRoute", FontAwesomeIcon.Trash, Lang.Get("AutoMarksFinder-DeleteRoute")))
            {
                config.ScanRoutes.Remove(zoneID);
                config.SelectedZones.Remove(zoneID);
                config.Save(this);

                recorderRecording          = false;
                recorderSelectedPointIndex = -1;
                return;
            }
        }

        ImGui.NewLine();

        using (ImRaii.Disabled(isLocked))
        {
            if (ImGuiOm.ButtonIconWithText
                (
                    recorderRecording ? FontAwesomeIcon.Stop : FontAwesomeIcon.Circle,
                    Lang.Get(recorderRecording ? "AutoMarksFinder-StopRecording" : "AutoMarksFinder-StartRecording")
                ))
                recorderRecording = !recorderRecording;

            ImGui.SameLine();

            using (ImRaii.Disabled(!recorderRecording))
            {
                if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.MapMarkerAlt, Lang.Get("AutoMarksFinder-AddPoint")))
                    AddRecorderPoint(route);
            }

            ImGui.SameLine();

            using (ImRaii.Disabled(route.Points.Count == 0))
            {
                if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Undo, Lang.Get("AutoMarksFinder-UndoPoint")))
                {
                    route.Points.RemoveAt(route.Points.Count - 1);
                    recorderSelectedPointIndex = route.Points.Count - 1;
                    SaveRecorderRoute(route);
                }
            }

            ImGui.SameLine();

            using (ImRaii.Disabled(route.Points.Count == 0))
            {
                if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Eraser, Lang.Get("AutoMarksFinder-ClearZonePoints")))
                {
                    route.Points.Clear();
                    recorderSelectedPointIndex = -1;
                    SaveRecorderRoute(route);
                }
            }
        }

        ImGui.NewLine();

        using (ImRaii.Disabled(isLocked || route.Points.Count <= 1))
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.SortAmountUp, Lang.Get("AutoMarksFinder-AutoSort")))
            {
                route.Points               = SortPoints(route.Points);
                recorderSelectedPointIndex = -1;
                SaveRecorderRoute(route);
            }

            ImGui.SameLine();

            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.ExchangeAlt, Lang.Get("AutoMarksFinder-ReverseSort")))
            {
                route.Points.Reverse();
                recorderSelectedPointIndex = -1;
                SaveRecorderRoute(route);
            }

            ImGui.SameLine();
        }

        using (ImRaii.Disabled(isLocked))
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Save, Lang.Get("AutoMarksFinder-SaveRoute")))
            {
                SaveRecorderRoute(route);
                NotifyHelper.Instance().NotificationInfo(Lang.Get("AutoMarksFinder-RouteSaved"), Lang.Get("AutoMarksFinderTitle"));
            }
        }

        ImGui.SameLine(0f, 8f * GlobalUIScale);

        using (ImRaii.Disabled(isLocked || recorderRecording || config.ScanRoutes.Count == 0))
        {
            if (ImGuiOm.ButtonIcon
                (
                    "AutoMarksFinder-ExportRoutes",
                    FontAwesomeIcon.FileExport,
                    Lang.Get("AutoMarksFinder-ExportRoutes")
                ))
                ExportToClipboard(config.ScanRoutes);
        }

        ImGui.SameLine();

        using (ImRaii.Disabled(isLocked || recorderRecording))
        {
            if (ImGuiOm.ButtonIcon
                (
                    "AutoMarksFinder-ImportRoutes",
                    FontAwesomeIcon.FileImport,
                    Lang.Get("AutoMarksFinder-ImportRoutes")
                ))
            {
                if (ImportFromClipboard<Dictionary<uint, ScanRoute>>() is { } imported)
                {
                    foreach (var (importedZoneID, importedRoute) in imported)
                    {
                        if (importedRoute == null) continue;

                        importedRoute.ZoneID = importedZoneID;
                        importedRoute.Name   = string.IsNullOrWhiteSpace(importedRoute.Name) ?
                                                    LuminaWrapper.GetZonePlaceName(importedZoneID) :
                                                    importedRoute.Name;
                        importedRoute.Points ??= [];

                        config.ScanRoutes[importedZoneID] = importedRoute;
                    }

                    config.Save(this);

                    NotifyHelper.Instance().NotificationInfo
                        (Lang.Get("AutoMarksFinder-RoutesImported", imported.Count), Lang.Get("AutoMarksFinderTitle"));
                }
            }
        }

        ImGui.NewLine();

        using (var pointsList = ImRaii.Child("###AutoMarksFinderRecorderPoints", new Vector2(0f, 220f * GlobalUIScale), true))
        {
            if (pointsList)
            {
                for (var i = 0; i < route.Points.Count; i++)
                {
                    ImGui.PushID(i);

                    var point = route.Points[i];

                    if (ImGui.Selectable
                        (
                            $"#{i + 1}  ({point.Position.X:F1}, {point.Position.Y:F1}, {point.Position.Z:F1})",
                            recorderSelectedPointIndex == i,
                            ImGuiSelectableFlags.None,
                            new Vector2(300f * GlobalUIScale, 0f)
                        ))
                        recorderSelectedPointIndex = i;

                    ImGui.SameLine();

                    using (ImRaii.Disabled(isLocked || i == 0))
                    {
                        if (ImGuiOm.ButtonIcon("Up", FontAwesomeIcon.ArrowUp, Lang.Get("MoveUp")))
                        {
                            (route.Points[i - 1], route.Points[i]) = (route.Points[i], route.Points[i - 1]);
                            recorderSelectedPointIndex             = i - 1;
                            SaveRecorderRoute(route);
                        }
                    }

                    ImGui.SameLine();

                    using (ImRaii.Disabled(isLocked || i >= route.Points.Count - 1))
                    {
                        if (ImGuiOm.ButtonIcon("Down", FontAwesomeIcon.ArrowDown, Lang.Get("MoveDown")))
                        {
                            (route.Points[i + 1], route.Points[i]) = (route.Points[i], route.Points[i + 1]);
                            recorderSelectedPointIndex             = i + 1;
                            SaveRecorderRoute(route);
                        }
                    }

                    ImGui.SameLine();

                    using (ImRaii.Disabled(isLocked))
                    {
                        if (ImGuiOm.ButtonIcon("Delete", FontAwesomeIcon.Trash, Lang.Get("Delete")))
                        {
                            route.Points.RemoveAt(i);
                            recorderSelectedPointIndex = Math.Min(recorderSelectedPointIndex, route.Points.Count - 1);
                            SaveRecorderRoute(route);

                            ImGui.PopID();
                            break;
                        }
                    }

                    ImGui.PopID();
                }
            }
        }

        ImGui.NewLine();

        if (route.Points.Count > 0)
            DrawScanRouteMap(recorderRouteMapRenderer, zoneID, route.Points, recorderSelectedPointIndex + 1);
        else
            ImGui.TextDisabled(Lang.Get("AutoMarksFinder-NoPoints"));
    }

    private void AddRecorderPoint
    (
        ScanRoute route
    )
    {
        if (IObjectTable.Instance().LocalPlayer is not { } localPlayer) return;

        var rawPosition = localPlayer.Position;
        var position = vnavmeshIPC.QueryNearestPointOnMesh(rawPosition, 10, 10000) is { } meshPosition ?
                           new Vector3(meshPosition.X, rawPosition.Y, meshPosition.Z) :
                           rawPosition;

        if (route.Points.Count > 0 &&
            Vector2.Distance(route.Points[^1].Position.ToVector2(), position.ToVector2()) < RECORDER_MIN_DISTANCE)
        {
            NotifyHelper.Instance().NotificationWarning
            (
                Lang.Get("AutoMarksFinder-PointTooClose", RECORDER_MIN_DISTANCE),
                Lang.Get("AutoMarksFinderTitle")
            );
            return;
        }

        route.Points.Add
        (
            new()
            {
                Zone            = route.ZoneID,
                Position        = position,
                CancelAnimation = true
            }
        );

        recorderSelectedPointIndex = route.Points.Count - 1;
        SaveRecorderRoute(route);
    }

    private void SaveRecorderRoute
    (
        ScanRoute route
    )
    {
        route.UpdatedTime = StandardTimeManager.Instance().UTCNowOffset.ToUnixTimeMilliseconds();

        config.ScanRoutes[route.ZoneID] = route;
        config.Save(this);
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
                config.SelectedZones.RemoveWhere(zoneID => !TryGetZonePoints(zoneID, out _));
                config.Save(this);

                if (config.SelectedZones.Count > 0)
                {
                    ClearScanner();
                    scanner = new(this, config.SelectedRanks, config.SelectedZones);
                    scanner.StartScan();
                }
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

        using (ImRaii.Disabled(scanner != null))
        {
            if (ImGui.Checkbox($"{Lang.Get("AutoMarksFinder-UseUpstreamPoints")}###AutoMarksFinder-UseUpstreamPoints", ref config.UseUpstreamPoints))
            {
                config.SelectedZones.Clear();
                config.Save(this);

                if (config.UseUpstreamPoints && config.UpstreamScanPoints.Count == 0)
                    StartUpdateUpstreamPoints();
            }
        }

        if (config.UseUpstreamPoints)
        {
            ImGui.SameLine();

            using (ImRaii.Disabled(scanner != null || spawnPointUpdateTask is { IsCompleted: false }))
            {
                if (ImGui.SmallButton(Lang.Get("AutoMarksFinder-UpdateSpawnPoints")))
                    StartUpdateUpstreamPoints();
            }

            ImGui.SameLine();
            ImGui.TextDisabled
            (
                config.UpstreamScanPoints.Count == 0 ?
                    Lang.Get("AutoMarksFinder-NoUpstreamPoints") :
                    Lang.Get("AutoMarksFinder-UpstreamPointsInfo", config.UpstreamScanPoints.Count, config.UpstreamScanPoints.Sum(x => x.Value.Count))
            );
        }

        var data = (config.UseUpstreamPoints ?
                        config.UpstreamScanPoints
                              .Where(x => x.Value.Count > 0)
                              .Select(x => new { ZoneID = x.Key, Label = string.Empty, Count = x.Value.Count }) :
                        config.ScanRoutes.Values
                              .OrderBy(x => x.CreatedTime)
                              .Select(x => new { ZoneID = x.ZoneID, Label = x.Name, Count = x.Points.Count }))
                   .Select(x => new { x.ZoneID, x.Label, x.Count, Zone = LuminaGetter.GetRow<TerritoryType>(x.ZoneID).GetValueOrDefault() })
                   .GroupBy(x => x.Zone.ExVersion.RowId)
                   .OrderBy(x => x.Key)
                   .ToDictionary(x => x.Key, x => x.ToList());
        ImGui.SetNextItemWidth(250f * GlobalUIScale);

        using (var combo = ImRaii.Combo
               (
                   $"{Lang.Get(config.UseUpstreamPoints ? "AutoMarksFinder-TargetZones" : "AutoMarksFinder-RouteConfig")}###ZoneSelectCombo",
                   Lang.Get(config.UseUpstreamPoints ? "AutoMarksFinder-SelectedZonePreview" : "AutoMarksFinder-SelectedRoutePreview", config.SelectedZones.Count),
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
                        var routesToOperate = zones.Where(x => x.Count > 0)
                                                   .Select(x => x.ZoneID)
                                                   .ToArray();

                        if (routesToOperate.Length > 0)
                        {
                            if (config.SelectedZones.ContainsAny(routesToOperate))
                                config.SelectedZones.RemoveRange(routesToOperate);
                            else
                                config.SelectedZones.AddRange(routesToOperate);

                            config.Save(this);
                        }
                    }

                    foreach (var entry in zones)
                    {
                        using (ImRaii.Disabled(entry.Count == 0))
                        {
                            if (ImGui.Selectable
                                (
                                    $"{(string.IsNullOrEmpty(entry.Label) ? string.Empty : $"{entry.Label} - ")}{entry.Zone.ExtractPlaceName()} ({entry.Count})",
                                    config.SelectedZones.Contains(entry.ZoneID),
                                    ImGuiSelectableFlags.DontClosePopups
                                ))
                            {
                                if (!config.SelectedZones.Remove(entry.ZoneID))
                                    config.SelectedZones.Add(entry.ZoneID);

                                config.Save(this);
                            }
                        }
                    }
                }
            }
        }
    }

    private void StartUpdateUpstreamPoints()
    {
        if (spawnPointUpdateTask is { IsCompleted: false }) return;

        spawnPointUpdateTask = Task.Run
        (async () =>
            {
                try
                {
                    string json;

                    try
                    {
                        json = await HTTPClientHelper.Instance().Get().GetStringAsync(UPSTREAM_POINTS_URL)
                                                    .ConfigureAwait(false);
                    }
                    catch
                    {
                        json = await HTTPClientHelper.Instance().Get().GetStringAsync(UPSTREAM_POINTS_URL_FALLBACK)
                                                    .ConfigureAwait(false);
                    }

                    var points = JsonConvert.DeserializeObject<Dictionary<uint, List<NotoriousMonsterScanPoint>>>
                        (json, JsonSerializerSettings.GetShared());

                    if (points is not { Count: > 0 }) return;

                    points = points.Where(x => x.Value is { Count: > 0 }).ToDictionary();
                    if (points.Count == 0) return;

                    config.UpstreamScanPoints = points;
                    config.Save(this);

                    NotifyHelper.Instance().NotificationInfo
                    (
                        Lang.Get("AutoMarksFinder-UpdateSpawnPointsRet", points.Sum(x => x.Value.Count)),
                        Lang.Get("AutoMarksFinderTitle")
                    );
                }
                catch
                {
                    // Keep the previous cache when the request fails; the user can retry manually
                }
            }
        ).ContinueWith(_ => spawnPointUpdateTask = null);
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
                    var target   = config.Trains[selectedTrainIndex];
                    var existing = target.MonstersFound.ToHashSet();
                    var found    = scanner.FoundOnes.SelectMany(x => x.Value).Distinct();
                    var newOnes  = found.Where(x => !existing.Contains(x)).Select(x => x.Copy()).ToList();

                    if (newOnes.Count > 0)
                    {
                        target.MonstersFound.AddRange(newOnes);
                        ResetAutoPilot();
                        config.Save(this);
                    }

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
