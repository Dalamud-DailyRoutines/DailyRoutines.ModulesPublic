using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using KamiToolKit.Controllers;
using KamiToolKit.MapOverlay;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.KamiToolKit.Addons.SelectYesno;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe class ClickableAethernet : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("ClickableAethernetTitle"),
        Description = Lang.Get("ClickableAethernetDescription"),
        Category    = ModuleCategory.Interface
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private AddonController<AddonAreaMap>? areaMapController;
    private MapOverlayController?          mapOverlayController;

    private DRSelectYesno? drSelectYesno;

    private uint lastMapID;

    protected override void Init()
    {
        mapOverlayController = new();
        mapOverlayController.Enable();

        areaMapController = new()
        {
            AddonName = "AreaMap",
            OnUpdate  = OnAddonUpdate
        };
        areaMapController.Enable();

        IClientState.Instance().TerritoryChanged += OnZoneChanged;
    }

    protected override void Uninit()
    {
        IClientState.Instance().TerritoryChanged -= OnZoneChanged;
        
        areaMapController?.Dispose();
        areaMapController = null;

        mapOverlayController?.RemoveAllMarkers();
        mapOverlayController?.Dispose();
        mapOverlayController = null;

        drSelectYesno?.Dispose();
        drSelectYesno = null;
    }
    
    private void OnZoneChanged
    (
        uint zone
    ) =>
        lastMapID = 0;

    private void OnAddonUpdate
    (
        AddonAreaMap* addon
    )
    {
        var agent = AgentMap.Instance();

        var selectedMapID = GameState.IsLoggedIn && agent != null && agent->IsAgentActive() ?
                                agent->SelectedMapId :
                                0;

        if (selectedMapID == lastMapID)
            return;

        lastMapID = selectedMapID;
        mapOverlayController.RemoveAllMarkers();

        if (selectedMapID != 0)
        {
            var mapRow = LuminaGetter.GetRowOrDefault<Map>(agent->SelectedMapId);

            var markers = mapRow.GetMapMarkers();

            var macroString =
                LuminaGetter.GetRowOrDefault<Addon>(3217).Text.ToMacroString()
                            .Replace
                            (
                                "Aetheryte,lnum1,8",
                                "Aetheryte,lnum1,9"
                            )
                            .Replace
                            (
                                @"<kilo(lnum2,\,)>",
                                "<string(lstr2)>"
                            );

            foreach (var marker in markers)
            {
                // 城内以太之晶
                if (marker.DataType != 4)
                    continue;

                if (!LuminaGetter.TryGetRow<PlaceName>(marker.DataKey.RowId, out var placeNameRow))
                    continue;

                if (AetheryteRecordManager.Instance().AllRecords.FirstOrDefault(x => x.GetData().AethernetName.RowId == placeNameRow.RowId)
                    is not { } aetheryteRecord)
                    continue;

                var isSameGroup = AetheryteRecordManager.Instance().GetNearestAetheryte(GameState.TerritoryType, Vector3.Zero) is { } record &&
                                  record.Group == aetheryteRecord.Group;

                var cost = isSameGroup ?
                               0 :
                               aetheryteRecord.Cost;

                mapOverlayController.AddMarker
                (
                    new MapMarkerNode
                    {
                        MapId       = agent->SelectedMapId,
                        Position    = PositionHelper.TextureToWorld(marker.GetPosition(), mapRow),
                        IconId      = marker.Icon,
                        Size        = new(32),
                        MarkerScale = 1f,
                        TextTooltip = $"{aetheryteRecord.Name}\n({cost}\ue049)",
                        OnClick = () =>
                        {
                            if (addon->NumBlockingAddons != 0)
                                return;

                            if (drSelectYesno != null &&
                                !AddonHelper.TryGetPtrByName("DRSelectYesno", out _))
                            {
                                try
                                {
                                    drSelectYesno?.Dispose();
                                    drSelectYesno = null;
                                }
                                catch
                                {
                                    // 谁敢猜这个时候会发生什么
                                }
                            }

                            if (drSelectYesno != null)
                                return;

                            drSelectYesno = DRSelectYesno.Open
                            (
                                new()
                                {
                                    Prompt = ISeStringEvaluator.Instance().EvaluateMacroString
                                    (
                                        macroString,
                                        [
                                            aetheryteRecord.RowID,
                                            cost.ToChineseString()
                                        ]
                                    ),
                                    Callback = (_, result) =>
                                    {
                                        drSelectYesno = null;

                                        if (result != DRSelectYesnoResult.Yes)
                                            return;

                                        aetheryteRecord.TeleportTo();
                                    },
                                    Position = new
                                    (
                                        addon->RootNode->GetNodeState().Center,
                                        AddonPositionAlignment.TopCenter
                                    ),
                                    BlockedParentID = addon->Id,
                                    ParentID        = addon->Id
                                }
                            );
                        }
                    }
                );
            }
        }
    }
}
