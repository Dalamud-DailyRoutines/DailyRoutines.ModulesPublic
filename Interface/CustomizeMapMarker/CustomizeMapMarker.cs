using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using KamiToolKit.Controllers;
using KamiToolKit.Enums;
using KamiToolKit.MapOverlay;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using OmenTools.Dalamud;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Interface.CustomizeMapMarker;

public unsafe partial class CustomizeMapMarker : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("CustomizeMapMarkerTitle"),
        Description = Lang.Get("CustomizeMapMarkerDescription"),
        Category    = ModuleCategory.Interface
    };

    private Config config = null!;

    private AddonController<AddonAreaMap>? areaMapController;
    private MapOverlayController?          mapOverlayController;
    private MarkerDetailsAddon?            markerDetailsAddon;
    private MarkerListAddon?               markerListAddon;
    private HorizontalListNode?            mapButtonContainer;
    private CircleButtonNode?              mapAddButton;
    private bool                           isPlacingMarker;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();
        NormalizeConfig();

        markerDetailsAddon = new(this)
        {
            InternalName = "DRCustomizeMapMarkerDetails",
            Title        = Lang.Get("CustomizeMapMarker-DetailsTitle"),
            Size         = new(420, 410)
        };
        markerListAddon = new(this)
        {
            InternalName = "DRCustomizeMapMarkerList",
            Title        = Lang.Get("CustomizeMapMarker-ListTitle"),
            Size         = new(620, 520)
        };

        mapOverlayController = new() { OnMapClick = AddMarkerAtMapPosition };
        mapOverlayController.Enable();

        areaMapController = new()
        {
            AddonName  = "AreaMap",
            OnSetup    = AttachAreaMapButtons,
            OnFinalize = DetachAreaMapButtons
        };
        areaMapController.Enable();

        CommandManager.Instance().AddSubCommand
        (
            COMMAND,
            new(OnCommand) { HelpMessage = Lang.Get("CustomizeMapMarker-CommandHelp") }
        );

        RebuildMapMarkers();
    }

    protected override void Uninit()
    {
        CommandManager.Instance().RemoveSubCommand(COMMAND);

        areaMapController?.Dispose();
        areaMapController = null;

        mapOverlayController?.Dispose();
        mapOverlayController = null;

        markerDetailsAddon?.Dispose();
        markerDetailsAddon = null;

        markerListAddon?.Dispose();
        markerListAddon = null;
    }

    private void OnCommand(string command, string arguments) =>
        markerListAddon?.Open();

    private void AttachAreaMapButtons(AddonAreaMap* addon)
    {
        mapButtonContainer?.Dispose();

        mapButtonContainer = new()
        {
            Position    = new(250, 30),
            Size        = new(170, 28),
            ItemSpacing = 4,
            Alignment   = HorizontalListAnchor.Right
        };

        mapButtonContainer.AddNode
        (
            mapAddButton = new CircleButtonNode
            {
                Icon        = CircleButtonIcon.Add,
                TextTooltip = Lang.Get("CustomizeMapMarker-AddMarker"),
                Size        = new(28),
                OnClick     = TogglePlacementMode
            }
        );
        mapButtonContainer.AddNode
        (
            new CircleButtonNode
            {
                Icon        = CircleButtonIcon.Document,
                TextTooltip = Lang.Get("CustomizeMapMarker-OpenList"),
                Size        = new(28),
                OnClick     = () => markerListAddon?.Toggle()
            }
        );

        mapButtonContainer.AttachNode(addon->LocationContainerNode);
    }

    private void DetachAreaMapButtons(AddonAreaMap* addon)
    {
        isPlacingMarker = false;
        mapButtonContainer?.Dispose();
        mapButtonContainer = null;
        mapAddButton       = null;
    }

    private void TogglePlacementMode()
    {
        isPlacingMarker ^= true;
        UpdatePlacementButton();

        if (isPlacingMarker)
            NotifyHelper.ToastQuest(Lang.Get("CustomizeMapMarker-PlacementHint"));
    }

    private void UpdatePlacementButton()
    {
        if (mapAddButton is not null)
        {
            mapAddButton.Icon = isPlacingMarker ? CircleButtonIcon.CrossSmall : CircleButtonIcon.Add;
            mapAddButton.TextTooltip = isPlacingMarker ?
                                           Lang.Get("CustomizeMapMarker-CancelPlacement") :
                                           Lang.Get("CustomizeMapMarker-AddMarker");
        }
    }

    private void AddMarkerAtMapPosition(uint mapID, Vector2 overlayPosition)
    {
        if (!isPlacingMarker) return;

        isPlacingMarker = false;
        UpdatePlacementButton();

        if (!LuminaGetter.TryGetRow<Map>(mapID, out var map))
        {
            NotifyHelper.ToastError(Lang.Get("CustomizeMapMarker-InvalidMap"));
            return;
        }

        var mapPosition = PositionHelper.WorldToMap(overlayPosition, map);
        var marker = new MarkerRecord
        {
            TerritoryID     = map.TerritoryType.RowId,
            MapID           = mapID,
            TexturePosition = overlayPosition,
            Name            = Lang.Get("CustomizeMapMarker-DefaultName", mapPosition.X, mapPosition.Y)
        };

        config.Markers.Add(marker);
        SaveAndRefresh();
        markerDetailsAddon?.OpenMarker(marker.ID);
    }

    private void OpenMarkerDetails(Guid markerID) =>
        markerDetailsAddon?.OpenMarker(markerID);

    private MarkerRecord? FindMarker(Guid markerID) =>
        config.Markers.FirstOrDefault(x => x.ID == markerID);

    private void DeleteMarker(Guid markerID)
    {
        if (config.Markers.RemoveAll(x => x.ID == markerID) is > 0)
            SaveAndRefresh();
    }

    private static void SetGameFlag(MarkerRecord marker) =>
        AgentMap.Instance()->SetFlagMapMarker
        (
            marker.TerritoryID,
            marker.MapID,
            new(marker.TexturePosition.X, 0, marker.TexturePosition.Y)
        );

    private void SaveAndRefresh()
    {
        config.Save(this);
        RebuildMapMarkers();
        markerListAddon?.RebuildList();
        markerDetailsAddon?.RefreshMarker();
    }

    private static string FormatMarkerLocation(MarkerRecord marker)
    {
        if (!LuminaGetter.TryGetRow<Map>(marker.MapID, out var map))
            return $"Map {marker.MapID}";

        var mapPosition = PositionHelper.WorldToMap(marker.TexturePosition, map);
        var mapName     = map.PlaceName.ValueNullable?.Name.ToString();
        if (string.IsNullOrWhiteSpace(mapName))
            mapName = $"Map {marker.MapID}";

        return $"{mapName}  X: {mapPosition.X:F1}  Y: {mapPosition.Y:F1}";
    }

    private static string FormatMapName(uint mapID)
    {
        if (!LuminaGetter.TryGetRow<Map>(mapID, out var map))
            return $"Map {mapID}";

        var mapName = map.PlaceName.ValueNullable?.Name.ToString();
        return string.IsNullOrWhiteSpace(mapName) ? $"Map {mapID}" : mapName;
    }

    private static void ExportMarkers(IEnumerable<MarkerRecord> markers)
    {
        try
        {
            var package = new MarkerPackage
            {
                Markers = [.. markers.Select(CloneMarker)]
            };
            ImGui.SetClipboardText(package.ToJSONBase64());
            NotifyHelper.ToastQuest
            (
                Lang.Get("CustomizeMapMarker-Exported"),
                new()
                {
                    DisplayCheckmark = true
                }
            );
        }
        catch (Exception exception)
        {
            DLog.Error(Lang.Get("CustomizeMapMarker-ExportFailed"), exception);
            NotifyHelper.ToastError(Lang.Get("CustomizeMapMarker-ExportFailed"));
        }
    }

    private void ImportMarkers()
    {
        try
        {
            var package = ImGui.GetClipboardText().FromJSONBase64<MarkerPackage>();

            if (package is not { Version: 1 })
            {
                NotifyHelper.ToastError(Lang.Get("CustomizeMapMarker-ImportInvalid"));
                return;
            }

            var importedCount = 0;

            foreach (var importedMarker in package.Markers.Take(MAX_IMPORT_COUNT))
            {
                if (importedMarker.MapID is 0                         ||
                    importedMarker.IconID > int.MaxValue              ||
                    !float.IsFinite(importedMarker.TexturePosition.X) ||
                    !float.IsFinite(importedMarker.TexturePosition.Y) ||
                    !LuminaGetter.TryGetRow<Map>(importedMarker.MapID, out _))
                    continue;

                var normalizedMarker = CloneMarker(importedMarker);
                if (normalizedMarker.ID == Guid.Empty)
                    normalizedMarker.ID = Guid.NewGuid();

                if (config.Markers.FirstOrDefault(x => x.ID == normalizedMarker.ID) is { } existingMarker)
                    CopyMarker(normalizedMarker, existingMarker);
                else
                    config.Markers.Add(normalizedMarker);

                importedCount++;
            }

            NormalizeConfig();
            SaveAndRefresh();
            
            NotifyHelper.ToastQuest
            (
                Lang.Get("CustomizeMapMarker-Imported", importedCount),
                new()
                {
                    DisplayCheckmark = true
                }
            );
        }
        catch (Exception exception)
        {
            DLog.Error(Lang.Get("CustomizeMapMarker-ImportFailed"), exception);
            NotifyHelper.ToastError(Lang.Get("CustomizeMapMarker-ImportFailed"));
        }
    }

    private static MarkerRecord CloneMarker(MarkerRecord marker) => new()
    {
        ID              = marker.ID,
        TerritoryID     = marker.TerritoryID,
        MapID           = marker.MapID,
        TexturePosition = marker.TexturePosition,
        Name            = marker.Name,
        Group           = marker.Group,
        Description     = marker.Description,
        IconID          = marker.IconID,
        CreatedAt       = marker.CreatedAt
    };

    private static void CopyMarker(MarkerRecord source, MarkerRecord destination)
    {
        destination.TerritoryID     = source.TerritoryID;
        destination.MapID           = source.MapID;
        destination.TexturePosition = source.TexturePosition;
        destination.Name            = source.Name;
        destination.Group           = source.Group;
        destination.Description     = source.Description;
        destination.IconID          = source.IconID;
        destination.CreatedAt       = source.CreatedAt;
    }

    private void RebuildMapMarkers()
    {
        if (mapOverlayController is null) return;

        mapOverlayController.RemoveAllMarkers();

        foreach (var marker in config.Markers)
        {
            if (!LuminaGetter.TryGetRow<Map>(marker.MapID, out _)) continue;

            var markerID = marker.ID;
            mapOverlayController.AddMarker
            (
                new MapMarkerNode
                {
                    MapId          = marker.MapID,
                    Position       = marker.TexturePosition,
                    UseRawPosition = true,
                    IconId         = marker.IconID,
                    Size           = new(32),
                    TextTooltip    = $"{marker.Name} [{marker.Group}]\n{marker.Description}".Trim(),
                    OnClick        = () => OpenMarkerDetails(markerID)
                }
            );
        }
    }

    private void NormalizeConfig()
    {
        config.Markers ??= [];
        HashSet<Guid> markerIDs = [];

        foreach (var marker in config.Markers)
        {
            if (marker.ID == Guid.Empty || !markerIDs.Add(marker.ID))
            {
                marker.ID = Guid.NewGuid();
                markerIDs.Add(marker.ID);
            }

            if (string.IsNullOrWhiteSpace(marker.Name))
                marker.Name = Lang.Get("CustomizeMapMarker-Untitled");

            if (string.IsNullOrWhiteSpace(marker.Group))
                marker.Group = Lang.Get("CustomizeMapMarker-DefaultGroup");

            if (marker.IconID is 0 or > int.MaxValue)
                marker.IconID = DEFAULT_ICON_ID;
        }
    }

    #region 常量

    private const string COMMAND          = "mapmarker";
    private const uint   DEFAULT_ICON_ID  = 60561;
    private const int    MAX_IMPORT_COUNT = 5000;
    private const uint   FLAG_ICON_ID     = 60561;

    #endregion
}
