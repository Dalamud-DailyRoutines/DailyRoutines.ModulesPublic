using System.Globalization;
using System.Numerics;
using DailyRoutines.Common.Extensions;
using DailyRoutines.Extensions;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using OmenTools.Dalamud;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.ImGuiOm.Widgets.MapRenderer;
using OmenTools.Interop.Game.Lumina;
using NotifyHelper = OmenTools.OmenService.NotifyHelper;
using Task = System.Threading.Tasks.Task;

namespace DailyRoutines.ModulesPublic;

public partial class AutoMarksFinder
{
    private readonly ImGuiMapRenderer scanMapRenderer = new()
    {
        EnableResizeGrip     = false,
        EnableDefaultMarkers = true
    };

    private ScanZoneCombo? scanZoneCombo;
    private int            scanResultTrainIndex = -1;
    private bool           confirmClearScanResults;

    private Task?                    spawnPointUpdateTask;
    private CancellationTokenSource? spawnPointUpdateCancellation;

    private AetheryteCombo? startAetheryteCombo;

    private string trainSearchInput = string.Empty;

    protected override void ConfigUI()
    {
        using (ImRaii.Heading1(Lang.Get("PreCondition")))
            ImGui.TextWrapped(Lang.Get("IPC-InstalledAndEnabledPlugin", "vnavmesh"));

        ImGui.NewLine();

        using var tabBar = ImRaii.TabBar("###TabBar");
        if (!tabBar) return;

        DrawTabItemTrain();

        DrawTabItemScanner();

        DrawTabItemSettings();
    }

    private unsafe void DrawTabItemTrain()
    {
        using var tabTrain = ImRaii.TabItem(Lang.Get("AutoMarksFinder-Routes"));
        if (!tabTrain) return;

        var selected =
            selectedTrainIndex == -1 ||
            selectedTrainIndex > config.Trains.Count - 1 ?
                null :
                config.Trains[selectedTrainIndex];

        if (DrawTrainsSelectCombo(300f * GlobalUIScale, ref selectedTrainIndex))
        {
            smallCardMobIndex = 0;
            StopAutoBroadcast();
            selected = SelectedTrain;
        }

        ImGui.SameLine();

        using (ImRaii.Disabled(selected == null))
            DrawAutoBroadcastButton();

        ImGui.Spacing();

        if (selected == null) return;

        var       source = selected.MonstersFound.ToList();
        using var table  = ImRaii.Table("##TrainTable", 4, ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable | ImGuiTableFlags.Hideable);
        if (!table) return;

        ImGui.TableSetupColumn(Lang.Get("Serial"),                           ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn(Lang.Get("Name"),                             ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn(Lang.Get("Position"),                         ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn(Lang.Get("AutoMarksFinder-NearestAetheryte"), ImGuiTableColumnFlags.WidthStretch);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        
        ImGui.TableNextColumn();
        if (ImGuiOm.ButtonSelectable($"{Lang.Get("AutoMarksFinder-IntendedStartingPoint")}"))
            ImGui.OpenPopup("StartAetherytePopup");

        using (var popup = ImRaii.Popup("StartAetherytePopup"))
        {
            if (popup)
            {
                startAetheryteCombo            ??= new("AutoMarksFinder.StartAetheryte");
                startAetheryteCombo.SelectedID =   selected.StartAetheryte?.RowId ?? 0;
                ImGui.SetNextItemWidth(260f * GlobalUIScale);
                if (startAetheryteCombo.DrawRadio())
                    selected.StartAetheryte = startAetheryteCombo.SelectedID == 0 ?
                                                  null :
                                                  startAetheryteCombo.SelectedItem;

                using (ImRaii.Disabled(selected.StartAetheryte == null))
                {
                    if (ImGui.Button(Lang.Get("AutoMarksFinder-SortAccordingToStartingPoint")))
                    {
                        var current = selected.MonstersFound.ElementAtOrDefault(smallCardMobIndex);
                        selected.Reorder();
                        smallCardMobIndex = Math.Max(0, selected.MonstersFound.FindIndex(monster => ReferenceEquals(monster, current)));
                        StopAutoBroadcast();
                        config.Save(this);
                    }
                }
            }
        }

        ImGui.TableNextColumn();
        ImGui.TableHeader(Lang.Get("Name"));
        ImGui.TableNextColumn();
        ImGui.TableHeader(Lang.Get("Position"));
        ImGui.TableNextColumn();
        ImGui.TableHeader(Lang.Get("AutoMarksFinder-NearestAetheryte"));

        for (var i = 0; i < source.Count; i++)
        {
            using var id    = ImRaii.PushId(i);
            using var group = ImRaii.Group();

            var info = source[i];
            ImGui.TableNextRow();

            ImGui.TableNextColumn();

            if (ImGuiOm.ButtonIcon($"删除{info}", FontAwesomeIcon.TrashAlt, Lang.Get("Delete")))
                ImGui.OpenPopup("###DeleteMonsterPopup");

            using (var deletePopup = ImRaii.Popup("###DeleteMonsterPopup"))
            {
                if (deletePopup)
                {
                    ImGui.TextUnformatted(Lang.Get("AutoMarksFinder-DeleteConfirm"));

                    if (ImGui.Button(Lang.Get("Confirm")))
                    {
                        selected.MonstersFound.RemoveAt(i);
                        aliveMonitor?.RemoveTrack(selected, info);
                        smallCardMobIndex = Math.Clamp
                        (
                            smallCardMobIndex -
                            (i < smallCardMobIndex ?
                                 1 :
                                 0),
                            0,
                            Math.Max(0, selected.MonstersFound.Count - 1)
                        );
                        StopAutoBroadcast();
                        config.Save(this);
                        ImGui.CloseCurrentPopup();
                        return;
                    }
                }
            }

            ImGui.SameLine();
            ImGuiOm.ButtonSelectable($"{i + 1:00}");

            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);

            using (var dragSource = ImRaii.DragDropSource())
            {
                if (dragSource)
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
                    var sourceIndex = i;
                    ImGui.SetDragDropPayload(MONSTER_DRAG_PAYLOAD, new ReadOnlySpan<byte>(&sourceIndex, sizeof(int)));
                    ImGui.TextUnformatted(info.GetBNPCName());
                }
            }

            using (var dragTarget = ImRaii.DragDropTarget())
            {
                if (dragTarget)
                {
                    var payload = ImGui.AcceptDragDropPayload(MONSTER_DRAG_PAYLOAD);

                    if ((ImGuiPayload*)payload != null &&
                        payload.IsDelivery())
                    {
                        var sourceIndex = *(int*)payload.Data;

                        if (sourceIndex != i &&
                            sourceIndex >= 0 &&
                            sourceIndex < selected.MonstersFound.Count)
                        {
                            var current = selected.MonstersFound.ElementAtOrDefault(smallCardMobIndex);
                            var dragged = selected.MonstersFound[sourceIndex];
                            selected.MonstersFound.RemoveAt(sourceIndex);
                            selected.MonstersFound.Insert(i, dragged);
                            smallCardMobIndex = Math.Max(0, selected.MonstersFound.FindIndex(monster => ReferenceEquals(monster, current)));
                            StopAutoBroadcast();
                            config.Save(this);
                            return;
                        }
                    }
                }
            }

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
            DrawPositionColumn(info, true);

            ImGui.TableNextColumn();
            DrawAetheryteColumn(info);
        }
    }

    private void DrawAutoBroadcastButton()
    {
        using var disabled = ImRaii.Disabled(MonsterScanner.Scanner?.IsRunning == true);
        var label = autoBroadcast.On ?
                        Lang.Get("AutoMarksFinder-AutoBroadcastOn") :
                        Lang.Get("AutoMarksFinder-AutoBroadcast");
        var tooltip = autoBroadcast.On ?
                          Lang.Get("AutoMarksFinder-AutoBroadcastTooltipStop") :
                          Lang.Get("AutoMarksFinder-AutoBroadcastTooltipStart");

        if (ImGuiOm.ButtonIcon(label, FontAwesomeIcon.Truck, tooltip))
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
            ImGui.Text(Lang.Get("AutoMarksFinder-AutoBroadcastServerTimeUncalibrated"));

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
                    () => info.NearestAetheryteRecord?.TeleportTo(),
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

    private void DrawScanRoutePreview()
    {
        if (MonsterScanner.Scanner is not { IsRunning: true, ScanPoints.Count: > 0 } ||
            !LuminaGetter.TryGetRow<TerritoryType>(MonsterScanner.Scanner.ZoneID, out var zone))
            return;

        ImGui.SetNextWindowSize(ScaledVector2(400,            470), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(ScaledVector2(320, 380), new(float.MaxValue));
        var visible = ImGui.Begin
        (
            Lang.Get("AutoMarksFinder-ScanPreview") + "###AutoMarksFinderScanPreview",
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoFocusOnAppearing
        );

        if (visible)
        {
            scanMapRenderer.SetMap(zone.Map.RowId);
            scanMapRenderer.ClearMarkers();

            for (var index = 0; index < MonsterScanner.Scanner.ScanPoints.Count; index++)
            {
                var point   = MonsterScanner.Scanner.ScanPoints[index];
                var pending = MonsterScanner.Scanner.PendingPoints.Contains(point);
                scanMapRenderer.AddMarker
                (
                    new()
                    {
                        ID       = $"Point_{index}",
                        Position = point,
                        Size     = ScaledVector2(7),
                        Color = ImGui.ColorConvertFloat4ToU32
                        (
                            pending ?
                                KnownColor.Orange.ToVector4() :
                                KnownColor.GreenYellow.ToVector4()
                        ),
                        ShowLabel   = false,
                        TooltipText = $"#{index + 1} {point:F0}"
                    }
                );
            }

            if (MonsterScanner.Scanner.FoundOnes.TryGetValue(MonsterScanner.Scanner.ZoneID, out var found))
            {
                foreach (var monster in found.Where(monster => monster.Instance == MonsterScanner.Scanner.Instance))
                {
                    scanMapRenderer.AddMarker
                    (
                        new()
                        {
                            ID          = $"Monster_{monster.DataID}",
                            Position    = monster.Position,
                            Size        = ScaledVector2(12),
                            Color       = ImGui.ColorConvertFloat4ToU32(KnownColor.Red.ToVector4()),
                            ShowLabel   = false,
                            TooltipText = $"{monster.GetBNPCName()} {monster.Position:F0}"
                        }
                    );
                }
            }

            scanMapRenderer.Draw(ImGui.GetContentRegionAvail());
        }

        ImGui.End();
    }

    private static void DrawScanMapCoverage(ImGuiMapRenderer renderer, ImDrawListPtr drawList)
    {
        if (MonsterScanner.Scanner == null)
            return;

        var coverageColor = KnownColor.GreenYellow.ToVector4();
        coverageColor.W = 0.12f;

        foreach (var position in MonsterScanner.Scanner.ObservedPositions)
        {
            var center = renderer.WorldToScreen(position);
            var radius = Vector2.Distance(center, renderer.WorldToScreen(position + new Vector3(MonsterScanner.DETECTION_RADIUS, 0, 0)));
            drawList.AddCircleFilled(center, radius, ImGui.ColorConvertFloat4ToU32(coverageColor));
            drawList.AddCircle(center, radius, ImGui.ColorConvertFloat4ToU32(KnownColor.GreenYellow.ToVector4()));
        }

        for (var index = 1; index < MonsterScanner.Scanner.ActivePath.Count; index++)
            drawList.AddLine
            (
                renderer.WorldToScreen(MonsterScanner.Scanner.ActivePath[index - 1]),
                renderer.WorldToScreen(MonsterScanner.Scanner.ActivePath[index]),
                ImGui.ColorConvertFloat4ToU32(KnownColor.LightSkyBlue.ToVector4()),
                2 * GlobalUIScale
            );
    }

    private static void DrawScanMapForeground(ImGuiMapRenderer renderer, ImDrawListPtr drawList)
    {
        if (MonsterScanner.Scanner == null)
            return;

        if (MonsterScanner.Scanner.NavigationTarget is { } target)
        {
            var targetPosition = renderer.WorldToScreen(target);
            drawList.AddCircle(targetPosition, 10 * GlobalUIScale, ImGui.ColorConvertFloat4ToU32(KnownColor.Red.ToVector4()), 0, 2 * GlobalUIScale);
        }

        if (GameState.TerritoryType          != MonsterScanner.Scanner.ZoneID   ||
            InstancesManager.CurrentInstance != MonsterScanner.Scanner.Instance ||
            LocalPlayerState.Object is not { } player)
            return;

        var position = renderer.WorldToScreen(player.Position);
        var radius   = Vector2.Distance(position, renderer.WorldToScreen(player.Position + new Vector3(MonsterScanner.DETECTION_RADIUS, 0, 0)));
        var color    = KnownColor.LightSkyBlue.ToVector4();
        drawList.AddCircle(position, radius, ImGui.ColorConvertFloat4ToU32(color), 0, 2 * GlobalUIScale);
        drawList.AddCircleFilled(position, 5                                            * GlobalUIScale, ImGui.ColorConvertFloat4ToU32(color));
    }

    private unsafe void DrawPositionColumn
    (
        NotoriousMonsterFound? info,
        bool                   includeZone = false
    )
    {
        if (info == null) return;
        using var id = ImRaii.PushId($"{info}");

        var agentMap = AgentMap.Instance();

        if (ImGuiOm.ButtonIcon
            (
                $"Relay{info}Position",
                FontAwesomeIcon.Microphone,
                Lang.Get("AutoMarksFinder-RelayPosition")
            ))
            Relay(RelayType.Position, info);

        ImGui.SameLine();

        if (includeZone)
        {
            ImGui.TextUnformatted(FormatZoneInfo(info.GetZoneName(), info.Instance));
            ImGui.SameLine();
        }

        ImGui.TextUnformatted($"[{info.Position.X:F1}, {info.Position.Y:F1}, {info.Position.Z:F1}]");
        if (ImGui.IsItemHovered())
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            var title =
                $"{info.GetBNPCName()}" +
                $"（{FormatZoneInfo(info.GetZoneName(), info.Instance)}）";

            agentMap->SetMapFlagAndOpen(info.GetMapID(), info.Position, title);
        }
    }

    private void DrawAetheryteColumn
    (
        NotoriousMonsterFound? info
    )
    {
        if (info == null) return;
        using var id = ImRaii.PushId($"{info}");

        if (ImGuiOm.ButtonIcon
            (
                $"Relay{info}Nearest",
                FontAwesomeIcon.Microphone,
                Lang.Get("AutoMarksFinder-RelayAetheryte")
            ))
            Relay(RelayType.WaitAetheryte, info);

        ImGui.SameLine();
        DrawTeleportToMonsterZone(info);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        using var combo = ImRaii.Combo("###NearestAetheryte", info.NearestAetheryteRecord?.Name ?? string.Empty);
        if (!combo) return;

        foreach (var aetheryte in AetheryteRecordManager.Instance().GameRecords.Where(x => x.IsAetheryte && x.ZoneID == info.AetheryteZoneID))
        {
            var selected = aetheryte.RowID == info.NearestAetheryteID;

            if (ImGui.Selectable(aetheryte.Name, selected))
            {
                info.NearestAetheryteID = aetheryte.RowID;
                config.Save(this);
            }

            if (selected)
                ImGui.SetItemDefaultFocus();
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

            using (ImRaii.Disabled(spawnPointUpdateTask is { IsCompleted: false } || MonsterScanner.Scanner?.IsRunning == true))
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
                   MonsterScanner.Scanner?.IsRunning       == true ||
                   MonsterScanner.Scanner?.FoundOnes.Count > 0     ||
                   config.SelectedZones.Count              == 0    ||
                   spawnPointUpdateTask is { IsCompleted: false }  ||
                   TaskHelper.IsBusy                               ||
                   autoBroadcast.On
               ))
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Play, Lang.Get("Start")))
            {
                MonsterScanner.ClearScanner();
                MonsterScanner.Scanner = new(this);
                MonsterScanner.Scanner.StartScan();
            }
        }

        ImGui.SameLine();

        using (ImRaii.Disabled(MonsterScanner.Scanner?.IsRunning != true))
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Stop, Lang.Get("Stop")))
                MonsterScanner.Scanner?.Dispose();
        }

        ImGui.SameLine();

        using (ImRaii.Disabled(MonsterScanner.Scanner == null || MonsterScanner.Scanner.IsRunning))
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

            if (ImGui.Button(Lang.Get("Confirm")))
            {
                MonsterScanner.ClearScanner();
                ImGui.CloseCurrentPopup();
            }
        }
    }

    private void DrawScannerSetting()
    {
        using var heading = ImRaii.Heading1(Lang.Get("AutoMarksFinder-TargetZones"));
        scanZoneCombo ??= new(config);
        ImGui.SetNextItemWidth(350f * GlobalUIScale);
        using var disabled = ImRaii.Disabled(MonsterScanner.Scanner?.IsRunning == true);
        if (scanZoneCombo.DrawCheckbox())
            config.Save(this);
    }

    private unsafe void DrawScanResult()
    {
        using var heading = ImRaii.Heading1(Lang.Get("AutoMarksFinder-ScanResults"));

        if (MonsterScanner.Scanner == null)
        {
            ImGuiOm.TextDisabledWrapped(Lang.Get("AutoMarksFinder-NoRunningScanner"));
            return;
        }

        using (ImRaii.Disabled(MonsterScanner.Scanner.IsRunning || MonsterScanner.Scanner.FoundOnes.Count == 0))
        {
            DrawTrainsSelectCombo(300f * GlobalUIScale, ref scanResultTrainIndex);
            ImGui.SameLine();

            using (ImRaii.Disabled(scanResultTrainIndex < 0 || scanResultTrainIndex >= config.Trains.Count))
            {
                var overwrite = ImGui.Button(Lang.Get("Overwrite"));

                ImGui.SameLine();
                var append = ImGui.Button(Lang.Get("Append"));

                if (overwrite || append)
                {
                    var train = config.Trains[scanResultTrainIndex];
                    var found = MonsterScanner.Scanner.FoundOnes.SelectMany(pair => pair.Value).ToHashSet();

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

        foreach (var info in MonsterScanner.Scanner.FoundOnes)
        {
            if (ImGui.CollapsingHeader($"{LuminaGetter.GetRow<TerritoryType>(info.Key)?.ExtractPlaceName()} ({info.Value.Count})###ScanResults_{info.Key}"))
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

    private bool DrawTrainsSelectCombo
    (
        float   width,
        ref int selectedIndex
    )
    {
        var changed = false;
        var selected = selectedIndex < 0 || selectedIndex >= config.Trains.Count ?
                           null :
                           config.Trains[selectedIndex];

        ImGui.SetNextItemWidth(width);
        ImGui.SetNextWindowSizeConstraints(ScaledVector2(760, 400), ScaledVector2(760, 400));
        using var combo = ImRaii.Combo
        (
            "###TrainsSelectCombo",
            selected == null ?
                $"({Lang.Get("None")})" :
                $"{selected.Name}",
            ImGuiComboFlags.HeightLarge
        );
        if (!combo) return changed;

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.InputTextWithHint("###TrainSearch", $"{Lang.Get("PleaseSearch")}……", ref trainSearchInput, 128);

        ImGui.SetNextItemWidth(-2 * ImGui.GetFrameHeightWithSpacing());
        var create = ImGui.InputTextWithHint
        (
            "###AddNewTrainList",
            Lang.Get("AutoMarksFinder-NewTrainListTip"),
            ref newListNameInput,
            256,
            ImGuiInputTextFlags.EnterReturnsTrue
        );

        ImGui.SameLine();
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(newListNameInput)))
            create |= ImGuiOm.ButtonIcon("###CreateTrain", FontAwesomeIcon.Plus, Lang.Get("Add"));

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

        if (create &&
            !string.IsNullOrWhiteSpace(newListNameInput))
        {
            var activeTrain = SelectedTrain;
            var resultTrain = scanResultTrainIndex >= 0 && scanResultTrainIndex < config.Trains.Count ?
                                  config.Trains[scanResultTrainIndex] :
                                  null;
            var train = new NotoriousMonsterList(newListNameInput.Trim());
            config.Trains.Insert(0, train);
            selectedTrainIndex   = config.Trains.FindIndex(candidate => ReferenceEquals(candidate, activeTrain));
            scanResultTrainIndex = config.Trains.FindIndex(candidate => ReferenceEquals(candidate, resultTrain));
            selectedIndex        = 0;
            selected             = train;
            newListNameInput     = string.Empty;
            trainSearchInput     = string.Empty;
            changed              = true;
            config.Save(this);
        }

        ImGui.Separator();

        using var table = ImRaii.Table
        (
            "###Routes",
            5,
            ImGuiTableFlags.RowBg         |
            ImGuiTableFlags.BordersInnerV |
            ImGuiTableFlags.ScrollY       |
            ImGuiTableFlags.Resizable,
            ScaledVector2(0, 300)
        );
        if (!table)
            return changed;

        ImGui.TableSetupColumn(Lang.Get("Name"),                         ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn(Lang.Get("Zone"),                         ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn(Lang.Get("Count"),                        ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn(Lang.Get("AutoMarksFinder-CreationTime"), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("###Actions",                             ImGuiTableColumnFlags.WidthFixed, 3 * ImGui.GetFrameHeightWithSpacing());
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        for (var i = 0; i < config.Trains.Count; i++)
        {
            var train = config.Trains[i];
            if (!train.Name.Contains(trainSearchInput, StringComparison.OrdinalIgnoreCase))
                continue;

            train.GeneratedDateTime ??= train.GeneratedTime.ToUTCDateTimeFromUnixSeconds().ToLocalTime();

            using var rowID = ImRaii.PushId(i);

            ImGui.TableNextRow();

            ImGui.TableNextColumn();

            using (ImRaii.PushColor(ImGuiCol.Text, KnownColor.LawnGreen.ToUInt(), ReferenceEquals(selected, train)))
            {
                if (ImGui.Selectable(train.Name))
                {
                    selectedIndex = i;
                    changed       = true;
                    selected      = train;

                    ImGui.CloseCurrentPopup();
                }
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{train.MonstersFound.Select(monster => monster.ZoneID).Distinct().Count()}");

            if (ImGui.IsItemHovered())
            {
                using var tooltip = ImRaii.Tooltip();
                foreach (var area in train.MonstersFound.GroupBy(monster => (monster.ZoneID, monster.Instance)))
                    ImGui.TextUnformatted($"{FormatZoneInfo(LuminaWrapper.GetZonePlaceName(area.Key.ZoneID), area.Key.Instance)} ({area.Count()})");
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{train.MonstersFound.Count(monster => monster.IsAlive)}/{train.MonstersFound.Count}");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{train.GeneratedDateTime:g}");

            ImGui.TableNextColumn();

            if (ImGuiOm.ButtonIcon("###RenameTrain", FontAwesomeIcon.Edit, Lang.Get("Rename")))
            {
                renameTrainInput = train.Name;
                ImGui.OpenPopup("###RenameTrainPopup");
            }

            using (var renamePopup = ImRaii.Popup("###RenameTrainPopup"))
            {
                if (renamePopup)
                {
                    ImGui.SetNextItemWidth(260 * GlobalUIScale);
                    var rename = ImGui.InputText("###TrainName", ref renameTrainInput, 128, ImGuiInputTextFlags.EnterReturnsTrue);
                    ImGui.SameLine();
                    using (ImRaii.Disabled(string.IsNullOrWhiteSpace(renameTrainInput)))
                        rename |= ImGui.Button(Lang.Get("Rename"));

                    if (rename &&
                        !string.IsNullOrWhiteSpace(renameTrainInput))
                    {
                        train.Name = renameTrainInput.Trim();
                        config.Save(this);
                        ImGui.CloseCurrentPopup();
                    }
                }
            }

            ImGui.SameLine();
            if (ImGuiOm.ButtonIcon("导出", FontAwesomeIcon.FileExport, Lang.Get("Export")))
                ExportToClipboard(train);

            ImGui.SameLine();
            if (ImGuiOm.ButtonIcon("###DeleteTrain", FontAwesomeIcon.TrashAlt, Lang.Get("Delete")))
                ImGui.OpenPopup("###DeleteTrainPopup");

            using var deletePopup = ImRaii.Popup("###DeleteTrainPopup");

            if (deletePopup)
            {
                ImGui.TextUnformatted(Lang.Get("AutoMarksFinder-DeleteConfirm"));

                if (ImGui.Button(Lang.Get("Confirm")))
                {
                    var activeTrain = SelectedTrain;
                    var resultTrain = scanResultTrainIndex >= 0 && scanResultTrainIndex < config.Trains.Count ?
                                          config.Trains[scanResultTrainIndex] :
                                          null;
                    config.Trains.RemoveAt(i);
                    selectedTrainIndex   = config.Trains.FindIndex(candidate => ReferenceEquals(candidate, activeTrain));
                    scanResultTrainIndex = config.Trains.FindIndex(candidate => ReferenceEquals(candidate, resultTrain));
                    selectedIndex        = config.Trains.FindIndex(candidate => ReferenceEquals(candidate, selected));
                    aliveMonitor?.RemoveTrain(train);

                    if (ReferenceEquals(activeTrain, train))
                    {
                        StopAutoBroadcast();
                        smallCardMobIndex = 0;
                    }

                    config.Save(this);
                    aliveMonitor?.RefreshTracking();
                    ImGui.CloseCurrentPopup();
                    return true;
                }
            }
        }

        if (changed)
            aliveMonitor?.RefreshTracking();

        return changed;
    }

    #region 常量

    private const string MONSTER_DRAG_PAYLOAD = "AutoMarksFinderMonster";

    #endregion
}
