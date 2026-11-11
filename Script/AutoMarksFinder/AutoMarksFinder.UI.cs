using System.Numerics;
using DailyRoutines.Common.Extensions;
using DailyRoutines.Extensions;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Utility;
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

internal partial class AutoMarksFinder
{
    private readonly ImGuiMapRenderer scanMapRenderer = new()
    {
        EnableResizeGrip     = false,
        EnableDefaultMarkers = true
    };

    private ScanZoneCombo?       scanZoneCombo;
    private bool                 confirmClearScanResults;
    private MonsterScanner?      scanCoverageSource;
    private int                  scanCoverageVersion;
    private IDalamudTextureWrap? scanCoverageTexture;

    private Task?                    spawnPointUpdateTask;
    private CancellationTokenSource? spawnPointUpdateCancellation;

    private AetheryteCombo? startAetheryteCombo;

    private string trainSearchInput = string.Empty;
    private string newListNameInput = string.Empty;
    private string renameTrainInput = string.Empty;

    protected override void ConfigUI()
    {
        using (ImRaii.Heading1(Lang.Get("PreCondition")))
            ImGui.TextWrapped
            (
                Lang.Get
                (
                    "IPC-InstalledAndEnabledPlugin",
                    new Dictionary<string, object>
                    {
                        ["pluginName"] = "vnavmesh CN"
                    }
                )
            );

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

        DrawTrainsSelectCombo(300f * GlobalUIScale, selection);

        if (movement.IsBusy &&
            scanner?.IsRunning != true)
        {
            ImGui.SameLine();
            if (ImGuiOm.ButtonIcon("###StopMovement", FontAwesomeIcon.Stop, Lang.Get("Stop")))
                movement.Stop();
        }

        ImGui.Spacing();

        if (SelectedTrain == null) return;

        var       source = SelectedTrain.MonstersFound;
        using var table  = ImRaii.Table("##TrainTable", 4, ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable | ImGuiTableFlags.Hideable);
        if (!table) return;

        ImGui.TableSetupColumn(Lang.Get("Serial"),                           ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn(Lang.Get("Name"),                             ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn(Lang.Get("Position"),                         ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn(Lang.Get("AutoMarksFinder-NearestAetheryte"), ImGuiTableColumnFlags.WidthStretch);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);

        ImGui.TableNextColumn();

        if (ImGuiOm.ButtonSelectable($"{Lang.Get("AutoMarksFinder-IntendedStartingPoint")}"))
        {
            var zones = SelectedTrain.MonstersFound.Select(monster => monster.AetheryteZoneID).ToHashSet();
            startAetheryteCombo = new
            (
                "AutoMarksFinder.StartAetheryte",
                LuminaGetter.Get<Aetheryte>().Where(aetheryte => aetheryte.IsAetheryte && zones.Contains(aetheryte.Territory.RowId))
            );

            ImGui.OpenPopup("StartAetherytePopup");
        }

        using (var popup = ImRaii.Popup("StartAetherytePopup"))
        {
            if (popup)
            {
                startAetheryteCombo.SelectedID = SelectedTrain.StartAetheryteID;
                ImGui.SetNextItemWidth(260f * GlobalUIScale);

                if (startAetheryteCombo.DrawRadio())
                {
                    SelectedTrain.StartAetheryteID = startAetheryteCombo.SelectedID;
                    config.Save(this);
                }

                using (ImRaii.Disabled(SelectedTrain.StartAetheryteID == 0))
                {
                    if (ImGui.Button(Lang.Get("AutoMarksFinder-SortAccordingToStartingPoint")))
                    {
                        SelectedTrain.Reorder();
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
                        SelectedTrain.RemoveAt(i);
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
                            sourceIndex < SelectedTrain.MonstersFound.Count)
                        {
                            SelectedTrain.Move(sourceIndex, i);
                            config.Save(this);
                            return;
                        }
                    }
                }
            }

            ImGui.TableNextColumn();
            DrawMonsterStatusToggle(SelectedTrain, info);

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
                    info.SetState(isAlive);
                    if (!autoBroadcast.IsRunning &&
                        ReferenceEquals(train, SelectedTrain))
                        selection.FollowAliveState(info);
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

        if (ImGuiOm.ButtonIcon("传送到以太之光", FontAwesomeIcon.Plane, Lang.Get("AutoMarksFinder-TeleportToMobsZone")))
        {
            movement.Travel(info.ZoneID, info.Instance, info.NearestAetheryteRecord);
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
        if (scanner is not { IsRunning: true, ScanPoints.Count: > 0 } ||
            !LuminaGetter.TryGetRow<TerritoryType>(scanner.ZoneID, out var zone))
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

            for (var index = 0; index < scanner.ScanPoints.Count; index++)
            {
                var point   = scanner.ScanPoints[index];
                var pending = scanner.PendingPoints.Contains(point);
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

            if (scanner.FoundOnes.TryGetValue(scanner.ZoneID, out var found))
            {
                foreach (var monster in found.Where(monster => monster.Instance == scanner.Instance))
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

    private void DrawScanMapCoverage(ImGuiMapRenderer renderer, ImDrawListPtr drawList)
    {
        if (scanner == null)
            return;

        scanner.UpdateCoverageImage();

        if (!ReferenceEquals(scanCoverageSource, scanner)  ||
            scanCoverageVersion != scanner.CoverageVersion ||
            scanCoverageTexture == null)
        {
            var texture = ITextureProvider.Instance().CreateFromRaw
            (
                RawImageSpecification.Bgra32(MonsterScanner.COVERAGE_SIZE, MonsterScanner.COVERAGE_SIZE),
                scanner.CoveragePixels
            );
            scanCoverageTexture?.Dispose();
            scanCoverageTexture = texture;
            scanCoverageSource  = scanner;
            scanCoverageVersion = scanner.CoverageVersion;
        }

        var bounds = scanner.CoverageBounds;
        drawList.AddImage
        (
            scanCoverageTexture.Handle,
            renderer.WorldToScreen(new(bounds.Left, 0, bounds.Top)),
            renderer.WorldToScreen(new(bounds.Right, 0, bounds.Bottom))
        );

        for (var index = 1; index < scanner.ActivePath.Count; index++)
            drawList.AddLine
            (
                renderer.WorldToScreen(scanner.ActivePath[index - 1]),
                renderer.WorldToScreen(scanner.ActivePath[index]),
                ImGui.ColorConvertFloat4ToU32(KnownColor.LightSkyBlue.ToVector4()),
                2 * GlobalUIScale
            );
    }

    private void DrawScanMapForeground(ImGuiMapRenderer renderer, ImDrawListPtr drawList)
    {
        if (scanner == null)
            return;

        if (scanner.NavigationTarget is { } target)
        {
            var targetPosition = renderer.WorldToScreen(target);
            drawList.AddCircle(targetPosition, 10 * GlobalUIScale, ImGui.ColorConvertFloat4ToU32(KnownColor.Red.ToVector4()), 0, 2 * GlobalUIScale);
        }

        if (GameState.TerritoryType          != scanner.ZoneID   ||
            InstancesManager.CurrentInstance != scanner.Instance ||
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

        using (ImRaii.Disabled(relay.IsBusy))
        {
            if (ImGuiOm.ButtonIcon
                (
                    $"Relay{info}Position",
                    FontAwesomeIcon.Microphone,
                    Lang.Get("AutoMarksFinder-RelayPosition")
                ))
                relay.Send(RelayType.Position, SelectedTrain, info);
        }

        ImGui.SameLine();

        using (ImRaii.Disabled(scanner?.IsRunning == true || !GameState.IsLoggedIn))
        {
            if (movement.IsBusy)
            {
                ImGui.SameLine();
                if (ImGuiOm.ButtonIcon("###StopMovement", FontAwesomeIcon.Stop, Lang.Get("Stop")))
                    movement.Stop();
            }
            else
            {
                if (ImGuiOm.ButtonIcon($"Navigate{info}", FontAwesomeIcon.Route, Lang.Get("AutoMarksFinder-NavigateToMonster")))
                    movement.NavigateToMonster(info);
            }
        }

        ImGui.SameLine();

        var isItemHovered = false;
        var isItemClicked = false;

        if (includeZone)
        {
            ImGui.TextUnformatted(FormatZoneInfo(info.GetZoneName(), info.Instance));
            if (ImGui.IsItemHovered())
                isItemHovered = true;
            if (ImGui.IsItemClicked())
                isItemClicked = true;

            ImGui.SameLine();
        }

        ImGui.TextUnformatted($"[{info.Position.X:F1}, {info.Position.Y:F1}, {info.Position.Z:F1}]");
        if (ImGui.IsItemHovered())
            isItemHovered = true;
        if (ImGui.IsItemClicked())
            isItemClicked = true;

        if (isItemHovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (isItemClicked)
            info.OpenMap();
    }

    private void DrawAetheryteColumn
    (
        NotoriousMonsterFound? info
    )
    {
        if (info == null) return;
        using var id = ImRaii.PushId($"{info}");

        using (ImRaii.Disabled(relay.IsBusy))
        {
            if (ImGuiOm.ButtonIcon
                (
                    $"Relay{info}Nearest",
                    FontAwesomeIcon.Microphone,
                    Lang.Get("AutoMarksFinder-RelayAetheryte")
                ))
                relay.Send(RelayType.WaitAetheryte, SelectedTrain, info);
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(scanner?.IsRunning == true || movement.IsBusy))
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

            using (ImRaii.Disabled(spawnPointUpdateTask is { IsCompleted: false } || scanner?.IsRunning == true))
            {
                if (ImGui.SmallButton(Lang.Get("AutoMarksFinder-UpdateSpawnPoints")))
                    spawnPointUpdateTask = UpdateSpawnPoints(spawnPointUpdateCancellation.Token);
            }
            
            ImGui.SameLine();
            if (ImGui.SmallButton(Lang.Get("AutoMarksFinder-ContributeSpawnPoints")))
                Util.OpenLink(SPAWN_POINTES_CONTRIBUTE_URL);
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
                        Lang.Get("AutoMarksFinder-UpdateSpawnPointsRet", new Dictionary<string, object>
                        {
                            ["count"] = parsed.Sum(pair => pair.Value.Count)
                        }),
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
                NotifyHelper.Instance().NotificationWarning
                (
                    Lang.Get("AutoMarksFinder-UpdateSpawnPointsFailed"),
                    Lang.Get("AutoMarksFinderTitle")
                );
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
                   movement.IsBusy                                ||
                   autoBroadcast.IsRunning
               ))
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Play, Lang.Get("Start")))
            {
                scanner?.Dispose();
                scanner = new(this);
                scanCoverageTexture?.Dispose();
                scanCoverageTexture = null;
                scanCoverageSource  = null;
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

            if (ImGui.Button(Lang.Get("Confirm")))
            {
                scanner?.Dispose();
                scanner = null;
                scanCoverageTexture?.Dispose();
                scanCoverageTexture = null;
                scanCoverageSource  = null;
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
            DrawTrainsSelectCombo(300f * GlobalUIScale, scanResultSelection);
            ImGui.SameLine();

            using (ImRaii.Disabled(scanResultSelection.Train == null))
            {
                var overwrite = ImGui.Button(Lang.Get("Overwrite"));

                ImGui.SameLine();
                var append = ImGui.Button(Lang.Get("Append"));

                if (overwrite || append)
                {
                    var train = scanResultSelection.Train;
                    train.Merge(scanner.FoundOnes.SelectMany(pair => pair.Value), overwrite);
                    config.Save(this);

                    NotifyHelper.Instance().NotificationInfo
                    (
                        Lang.Get("SavedSuccessfully"),
                        Lang.Get("AutoMarksFinderTitle")
                    );
                }
            }
        }

        foreach (var info in scanner.FoundOnes)
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

    private void DrawTrainsSelectCombo
    (
        float         width,
        HuntSelection targetSelection
    )
    {
        var selected = targetSelection.Train;

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
        if (!combo)
            return;

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
                    monster.Observe(false, true);

                config.Trains.Add(imported);
                config.Save(this);
            }
        }

        if (create &&
            !string.IsNullOrWhiteSpace(newListNameInput))
        {
            var train = new NotoriousMonsterList { Name = newListNameInput.Trim() };
            config.Trains.Insert(0, train);
            targetSelection.Select(train);
            selected         = train;
            newListNameInput = string.Empty;
            trainSearchInput = string.Empty;
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
            return;

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

            using var rowID = ImRaii.PushId(i);

            ImGui.TableNextRow();

            ImGui.TableNextColumn();

            using (ImRaii.PushColor(ImGuiCol.Text, KnownColor.LawnGreen.ToUInt(), ReferenceEquals(selected, train)))
            {
                if (ImGui.Selectable(train.Name))
                {
                    targetSelection.Select(train);
                    selected = train;

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
                    config.Trains.RemoveAt(i);
                    if (ReferenceEquals(selection.Train, train))
                        selection.Select(null);

                    if (ReferenceEquals(scanResultSelection.Train, train))
                        scanResultSelection.Select(null);

                    config.Save(this);
                    aliveMonitor?.RefreshTracking();
                    ImGui.CloseCurrentPopup();
                    return;
                }
            }
        }

    }

    #region 常量

    private const string MONSTER_DRAG_PAYLOAD = "AutoMarksFinderMonster";
    
    private const string SPAWN_POINTS_URL =
        "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/AutoMarksFinder/SpawnPoints.json";

    private const string SPAWN_POINTES_CONTRIBUTE_URL = 
        "https://github.com/Dalamud-DailyRoutines/DailyRoutines/tree/main/Resources/Modules/AutoMarksFinder";

    #endregion
}
