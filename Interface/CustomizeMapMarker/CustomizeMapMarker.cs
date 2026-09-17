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
        Category    = ModuleCategory.Interface,
        PreviewImageURL =
        [
            "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/CustomizeMapMarker/preview-1.png"
        ]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private Config config = null!;

    private readonly Dictionary<Guid, MarkerRecord> markerIndex = [];

    private MarkerDetailsAddon? markerDetailsAddon;
    private MarkerListAddon?    markerListAddon;
    
    private AddonController<AddonAreaMap>? areaMapController;
    private MapOverlayController?          mapOverlayController;

    private HorizontalListNode? mapButtonContainer;
    private CircleButtonNode?   mapAddButton;
    private CircleButtonNode?   mapListButton;

    private bool isPlacingMarker;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();
        NormalizeConfig();

        markerDetailsAddon = new(this)
        {
            InternalName = "DRCustomizeMapMarkerDetails",
            Title        = Lang.Get("CustomizeMapMarker-DetailsTitle"),
            Size         = new(420, 640)
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
            OnSetup    = OnAreaMapSetup,
            OnUpdate   = OnAreaMapUpdate,
            OnFinalize = OnAreaMapFinalize
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
        
        OnAreaMapFinalize(null);

        mapOverlayController?.Dispose();
        mapOverlayController = null;

        markerDetailsAddon?.Dispose();
        markerDetailsAddon = null;

        markerListAddon?.Dispose();
        markerListAddon = null;

        markerIndex.Clear();
    }

    private void OnCommand
    (
        string command,
        string arguments
    ) =>
        markerListAddon?.Open();
    
    private void OnAreaMapSetup
    (
        AddonAreaMap* addon
    )
    {
        mapButtonContainer = new()
        {
            Position    = new(250, 30),
            Size        = new(70, 28),
            ItemSpacing = 4,
            Alignment   = HorizontalListAnchor.Right
        };

        mapButtonContainer.AddNode
        (
            mapAddButton = new()
            {
                Icon        = CircleButtonIcon.Add,
                TextTooltip = Lang.Get("CustomizeMapMarker-AddMarker"),
                Size        = new(28),
                OnClick     = TogglePlacementMode
            }
        );
        
        mapButtonContainer.AddNode
        (
            mapListButton = new()
            {
                Icon        = CircleButtonIcon.Document,
                TextTooltip = Lang.Get("CustomizeMapMarker-OpenList"),
                Size        = new(28),
                OnClick     = () => markerListAddon?.Toggle()
            }
        );
        
        mapButtonContainer.RecalculateLayout();

        mapButtonContainer.AttachNode(addon->LocationContainerNode);
    }
    
    private void OnAreaMapUpdate
    (
        AddonAreaMap* addon
    )
    {
        mapButtonContainer?.X = addon->RootNode->GetWidth()  - 24;
        mapButtonContainer?.Y = addon->RootNode->GetHeight() - 52;
    }

    private void OnAreaMapFinalize
    (
        AddonAreaMap* addon
    )
    {
        isPlacingMarker = false;

        mapButtonContainer?.Dispose();
        mapButtonContainer = null;
        
        mapAddButton  = null;
        mapListButton = null;
    }

    private void TogglePlacementMode()
    {
        isPlacingMarker = !isPlacingMarker;
        UpdatePlacementButton();

        if (isPlacingMarker)
            NotifyHelper.ToastQuest(Lang.Get("CustomizeMapMarker-PlacementHint"));
    }

    private void UpdatePlacementButton()
    {
        if (mapAddButton is not null)
        {
            mapAddButton.Icon = isPlacingMarker ?
                                    CircleButtonIcon.CrossSmall :
                                    CircleButtonIcon.Add;
            mapAddButton.TextTooltip = isPlacingMarker ?
                                           Lang.Get("CustomizeMapMarker-CancelPlacement") :
                                           Lang.Get("CustomizeMapMarker-AddMarker");
        }
    }

    private void AddMarkerAtMapPosition
    (
        uint    mapID,
        Vector2 overlayPosition
    )
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
        markerIndex.Add(marker.ID, marker);
        SaveAndRefresh();
        markerDetailsAddon?.OpenMarker(marker.ID);
    }

    private MarkerRecord? FindMarker
    (
        Guid markerID
    ) =>
        markerIndex.GetValueOrDefault(markerID);

    private void DeleteMarker
    (
        Guid markerID
    )
    {
        if (!markerIndex.Remove(markerID, out var marker)) return;

        config.Markers.Remove(marker);
        SaveAndRefresh();
    }

    private static void SetGameFlag
    (
        MarkerRecord marker
    ) =>
        AgentMap.Instance()->SetMapFlagAndOpen
        (
            marker.MapID,
            marker.TexturePosition.ToVector3(0)
        );

    private void SaveAndRefresh()
    {
        config.Save(this);
        RebuildMapMarkers();
        markerListAddon?.RebuildList();
        markerDetailsAddon?.RefreshMarker();
    }

    private static string FormatMarkerLocation
    (
        MarkerRecord marker
    )
    {
        if (!LuminaGetter.TryGetRow<Map>(marker.MapID, out var map))
            return $"Map {marker.MapID}";

        var mapPosition = PositionHelper.WorldToMap(marker.TexturePosition, map);
        return $"{GetMapName(map, marker.MapID)}  X: {mapPosition.X:F1}  Y: {mapPosition.Y:F1}";
    }

    private static string FormatMapName
    (
        uint mapID
    )
    {
        if (!LuminaGetter.TryGetRow<Map>(mapID, out var map))
            return $"Map {mapID}";

        return GetMapName(map, mapID);
    }

    private static string GetMapName
    (
        Map  map,
        uint mapID
    )
    {
        var mapName = map.PlaceName.ValueNullable?.Name.ToString();
        return string.IsNullOrWhiteSpace(mapName) ?
                   $"Map {mapID}" :
                   mapName;
    }

    private static void ExportMarkers
    (
        IEnumerable<MarkerRecord> markers
    )
    {
        try
        {
            var package = new MarkerPackage
            {
                Markers = [.. markers.Select(marker => marker.Clone())]
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

                var normalizedMarker = importedMarker.Clone();
                if (normalizedMarker.ID == Guid.Empty)
                    normalizedMarker.ID = Guid.NewGuid();

                if (markerIndex.TryGetValue(normalizedMarker.ID, out var existingMarker))
                    CopyMarker(normalizedMarker, existingMarker);
                else
                {
                    config.Markers.Add(normalizedMarker);
                    markerIndex.Add(normalizedMarker.ID, normalizedMarker);
                }

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

    private static void CopyMarker
    (
        MarkerRecord source,
        MarkerRecord destination
    )
    {
        destination.TerritoryID     = source.TerritoryID;
        destination.MapID           = source.MapID;
        destination.TexturePosition = source.TexturePosition;
        destination.Name            = source.Name;
        destination.Group           = source.Group;
        destination.Description     = source.Description;
        destination.IconID          = source.IconID;
        destination.Scale           = source.Scale;
        destination.AutoSetFlag     = source.AutoSetFlag;
        destination.ExtraCommands   = source.ExtraCommands;
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
                    IconId         = marker.IconID,
                    Size           = new(32),
                    MarkerScale    = marker.Scale,
                    TextTooltip    = $"{marker.Name} [{marker.Group}]\n{marker.Description}".Trim(),
                    OnClick        = () => HandleMarkerClick(markerID),
                    OnRightClick   = () => markerDetailsAddon?.OpenMarker(markerID)
                }
            );
        }
    }

    private void HandleMarkerClick
    (
        Guid markerID
    )
    {
        if (FindMarker(markerID) is not { } marker) return;

        if (marker.AutoSetFlag)
            SetGameFlag(marker);

        if (!string.IsNullOrWhiteSpace(marker.ExtraCommands))
            ChatManager.Instance().ExecuteMacro(marker.ExtraCommands);
    }

    private void NormalizeConfig()
    {
        config.Markers ??= [];
        markerIndex.Clear();

        foreach (var marker in config.Markers)
        {
            while (marker.ID == Guid.Empty || !markerIndex.TryAdd(marker.ID, marker))
                marker.ID = Guid.NewGuid();

            if (string.IsNullOrWhiteSpace(marker.Name))
                marker.Name = Lang.Get("CustomizeMapMarker-Untitled");

            if (string.IsNullOrWhiteSpace(marker.Group))
                marker.Group = Lang.Get("CustomizeMapMarker-DefaultGroup");

            if (marker.IconID is 0 or > int.MaxValue)
                marker.IconID = DEFAULT_ICON_ID;

            if (!float.IsFinite(marker.Scale) || marker.Scale <= 0)
                marker.Scale = 1f;

            marker.ExtraCommands ??= string.Empty;

        }
    }

    #region 常量

    private const string COMMAND          = "mapmarker";
    private const uint   DEFAULT_ICON_ID  = 60561;
    private const int    MAX_IMPORT_COUNT = 5000;

    #endregion
}
