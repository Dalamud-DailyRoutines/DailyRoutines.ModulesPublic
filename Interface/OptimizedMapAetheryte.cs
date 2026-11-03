using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OmenTools.Info.Game.AetheryteRecord;
using OmenTools.Interop.Game;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.KamiToolKit.Addons.SelectYesno;
using AgentReceiveEventDelegate = FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent.AgentReceiveEventDelegate;
using AgentMap = FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent.AgentMap;
using AddonAreaMap = FFXIVClientStructs.Expansion.FFXIV.Client.UI.Addon.AddonAreaMap;
using AtkCursor = FFXIVClientStructs.Expansion.FFXIV.Component.GUI.AtkCursor;
using CursorType = FFXIVClientStructs.FFXIV.Component.GUI.AtkCursor.CursorType;
using MapMarkerBase = FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent.MapMarkerBase;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedMapAetheryte : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("OptimizedMapAetheryteTitle"),
        Description = Lang.Get("OptimizedMapAetheryteDescription"),
        Category    = ModuleCategory.Interface
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private Hook<AgentMap.Delegates.BuildMapMarkers> BuildMapMarkersHook;

    private Hook<AgentReceiveEventDelegate> AgentMapReceiveEventHook;

    private Hook<AgentMap.Delegates.CreateTooltip> AgentMapCreateTooltipHook;

    private MemoryPatch aetheryteTopPatch = null!;

    private Config config = null!;

    private SelectYesnoAddon? teleportConfirmation;

    private bool isHoveringAetheryte;

    protected override void Init()
    {
        config = LoadConfig<Config>() ?? new();

        aetheryteTopPatch = new
        (
            "80 BB 10 5B 00 00 00 74 1B 83 F9 03 75 16 48 83 FD 14",
            [0xEB, 0x07, 0x90, 0x90, 0x90, 0x90, 0x90]
        );
        aetheryteTopPatch.Enable();

        BuildMapMarkersHook = IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(AgentMap.MemberFunctionPointers),
            nameof(AgentMap.BuildMapMarkers),
            (AgentMap.Delegates.BuildMapMarkers)BuildMapMarkersDetour
        );
        BuildMapMarkersHook.Enable();

        AgentMapReceiveEventHook = AgentMap.Instance()->VirtualTable->HookVFuncFromName
        (
            "ReceiveEvent",
            (AgentReceiveEventDelegate)AgentMapReceiveEventDetour
        );
        AgentMapReceiveEventHook.Enable();

        AgentMapCreateTooltipHook = IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(AgentMap.MemberFunctionPointers),
            nameof(AgentMap.CreateTooltip),
            (AgentMap.Delegates.CreateTooltip)AgentMapCreateTooltipDetour
        );
        AgentMapCreateTooltipHook.Enable();

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreReceiveEvent,  "AreaMap", OnAreaMap);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostReceiveEvent, "AreaMap", OnAreaMap);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreRefresh,       "AreaMap", OnAreaMap);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreHide,          "AreaMap", OnAreaMap);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreClose,         "AreaMap", OnAreaMap);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize,      "AreaMap", OnAreaMap);
    }

    protected override void Uninit()
    {
        IAddonLifecycle.Instance().UnregisterListener(OnAreaMap);
        ResetAetheryteCursor();
        UpdateExtraMapMarkers(AgentMap.Instance(), false);

        teleportConfirmation?.Dispose();
        teleportConfirmation = null;
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox
            (
                Lang.Get("OptimizedMapAetheryte-NotDisplayInParty"),
                ref config.NoDisplayWhenInParty
            ))
            config.Save(this);
        ImGuiOm.TextDisabledWrapped(Lang.Get("OptimizedMapAetheryte-NotDisplayInParty-Help"));
    }

    private void OnAreaMap(AddonEvent type, AddonArgs args)
    {
        if (ShouldSkip())
            return;

        if (args is not AddonReceiveEventArgs receiveEventArgs                    ||
            (AtkEventType)receiveEventArgs.AtkEventType != AtkEventType.MouseMove ||
            (ushort)receiveEventArgs.EventParam         != 20)
        {
            ResetAetheryteCursor();
            return;
        }

        if (type != AddonEvent.PostReceiveEvent)
            return;

        var addon = (AddonAreaMap*)args.Addon.Address;
        var stage = AtkStage.Instance();

        if (!((AtkUnitBase*)addon)->IsAddonAndNodesReady() ||
            addon->NumBlockingAddons != 0                  ||
            addon->IsUsingController                       ||
            addon->IsDraggingMap                           ||
            addon->IsResizingMap                           ||
            stage->AtkCollisionManager->IntersectingAddon != (AtkUnitBase*)addon)
        {
            ResetAetheryteCursor();
            return;
        }

        var marker = (AddonAreaMap.MapMarker*)addon->AreaMap.GetMarker((uint)addon->HoveredMarkerIndex);

        if (marker                       == null ||
            marker->InteractionFlags     == 0    ||
            marker->TooltipContext >> 24 != 2)
        {
            ResetAetheryteCursor();
            return;
        }

        var agent = AgentMap.Instance();
        var index = (int)(marker->TooltipContext & 0xFFFFFF);

        if (index >= agent->MapMarkerCount)
        {
            ResetAetheryteCursor();
            return;
        }

        var record = FindRecord(agent->MapMarkers[index]);

        if (record == null ||
            !record.IsUnlocked())
        {
            ResetAetheryteCursor();
            return;
        }

        var cursor = (AtkCursor*)&stage->AtkCursor;

        if (cursor->Type != CursorType.Clickable)
        {
            cursor->IsTypeLocked = false;
            cursor->SetCursorType(CursorType.Clickable, true);
        }

        cursor->IsTypeLocked = true;
        isHoveringAetheryte  = true;
    }

    private void ResetAetheryteCursor()
    {
        if (!isHoveringAetheryte)
            return;

        isHoveringAetheryte = false;

        var stage  = AtkStage.Instance();
        var cursor = (AtkCursor*)&stage->AtkCursor;
        cursor->IsTypeLocked = false;
        cursor->SetCursorType(CursorType.Arrow, true);
        stage->RaptureAtkUnitManager->UpdateCursor();
    }

    private uint BuildMapMarkersDetour
    (
        AgentMap*        agent,
        ExtendArrayData* array,
        bool             omitAetherytes,
        bool             onlyAetherytes
    )
    {
        UpdateExtraMapMarkers(agent, !ShouldSkip());

        if (ShouldSkip())
            return BuildMapMarkersHook.Original(agent, array, omitAetherytes, onlyAetherytes);

        Span<ushort> markerTypes = stackalloc ushort[agent->MapMarkerCount];

        for (var index = 0; index < agent->MapMarkerCount; index++)
        {
            ref var marker = ref agent->MapMarkers[index];
            markerTypes[index] = marker.DataType;

            if (marker.DataType is 4 or EXTRA_MARKER_TYPE)
                marker.DataType = 3;
        }

        try
        {
            return BuildMapMarkersHook.Original(agent, array, omitAetherytes, onlyAetherytes);
        }
        finally
        {
            for (var index = 0; index < markerTypes.Length; index++)
                agent->MapMarkers[index].DataType = markerTypes[index];
        }
    }

    private static void UpdateExtraMapMarkers(AgentMap* agent, bool addExtraMarkers)
    {
        var count = 0;

        for (var index = 0; index < agent->MapMarkerCount; index++)
        {
            var marker = agent->MapMarkers[index];
            if (marker.DataType == EXTRA_MARKER_TYPE)
                continue;

            var markerBase = (MapMarkerBase*)&marker.MapMarker;
            markerBase->TooltipContext = (markerBase->TooltipContext & 0xFF000000) | (uint)count;
            agent->MapMarkers[count++] = marker;
        }

        agent->MapMarkerCount = (byte)count;

        if (!addExtraMarkers)
            return;

        foreach (var record in AetheryteRecordManager.Instance().AllRecords)
        {
            if (record.EventTransport is not { } transport ||
                !record.IsUnlocked())
                continue;

            var position = Vector3.Zero;

            if (transport.SourceZone == agent->SelectedTerritoryId)
            {
                if (!LuminaGetter.TryGetRow<TerritoryType>(transport.SourceZone, out var territory) ||
                    territory.Map.RowId != agent->SelectedMapId)
                    continue;

                position   =  transport.EventPosition;
                position.X += agent->SelectedOffsetX;
                position.Z += agent->SelectedOffsetY;
            }
            else if (record.ZoneID != agent->SelectedTerritoryId)
                continue;

            if (!agent->AddMapMarker(position, AETHERNET_ICON_ID))
                return;

            var marker = agent->MapMarkers[agent->MapMarkerCount - 1];
            ((MapMarkerBase*)&marker.MapMarker)->TooltipContext = MAP_MARKER_CONTEXT | (uint)(agent->MapMarkerCount - 1);
            marker.MapMarker.IconFlags                          = EXTRA_MARKER_ICON_FLAGS;
            marker.DataType                                     = EXTRA_MARKER_TYPE;
            marker.DataKey                                      = (ushort)record.ZoneID;
            agent->MapMarkers[agent->MapMarkerCount - 1]        = marker;
        }
    }

    private AtkValue* AgentMapReceiveEventDetour
    (
        AgentInterface* agent,
        AtkValue*       returnValues,
        AtkValue*       values,
        uint            valueCount,
        ulong           eventKind
    )
    {
        if (!ShouldSkip()             &&
            eventKind            == 0 &&
            valueCount           >= 2 &&
            values[0].GetInt()   == 7 &&
            values[1].UInt >> 24 == 2)
        {
            var mapAgent = (AgentMap*)agent;
            var index    = (int)(values[1].UInt & 0xFFFFFF);

            if (index < mapAgent->MapMarkerCount)
            {
                var marker = mapAgent->MapMarkers[index];
                var record = FindRecord(marker);

                if (record != null)
                {
                    if (AreaMap->IsAddonAndNodesReady() &&
                        AreaMap->NumBlockingAddons == 0)
                    {
                        OpenTeleportConfirmation(record, AreaMap);

                        returnValues->SetBool(false);
                        return returnValues;
                    }
                }
            }
        }

        return AgentMapReceiveEventHook.Original(agent, returnValues, values, valueCount, eventKind);
    }

    private bool AgentMapCreateTooltipDetour(AgentMap* agent, Utf8String* tooltipString, uint tooltipContext)
    {
        var index = (int)(tooltipContext & 0xFFFFFF);
        var isExtraMarker = tooltipContext >> 24              == 2                    &&
                            index                             < agent->MapMarkerCount &&
                            agent->MapMarkers[index].DataType == EXTRA_MARKER_TYPE;
        var result = isExtraMarker || AgentMapCreateTooltipHook.Original(agent, tooltipString, tooltipContext);

        if (ShouldSkip())
            return !isExtraMarker && result;

        if (!result ||
            tooltipContext >> 24 != 2)
            return result;

        if (index >= agent->MapMarkerCount)
            return result;

        var record = FindRecord(agent->MapMarkers[index]);
        if (record == null ||
            !record.IsUnlocked())
            return result;

        tooltipString->SetString(GetTeleportText(record, 3216));
        return result;
    }

    private static AetheryteRecord? FindRecord(MapMarkerInfo marker) => marker.DataType switch
    {
        EXTRA_MARKER_TYPE => AetheryteRecordManager.Instance().AllRecords.FirstOrDefault
        (x => x.Group  == EXTRA_RECORD_GROUP &&
              x.ZoneID == marker.DataKey
        ),
        3 => AetheryteRecordManager.Instance().AllRecords.FirstOrDefault
        (x => x is { Group: < 252, IsHouse: false, IsAetheryte: true } &&
              x.RowID == marker.DataKey
        ),
        4 => AetheryteRecordManager.Instance().AllRecords.FirstOrDefault
        (x => x is { Group: < 252, IsHouse: false, IsAetheryte: false } &&
              LuminaGetter.TryGetRow<Aetheryte>(x.RowID, out var data)  &&
              data.AethernetName.RowId == marker.DataKey
        ),
        _ => null
    };

    private static uint GetTeleportCost(AetheryteRecord record)
    {
        record.Update();
        var isSameGroup = AetheryteRecordManager.Instance().GetNearestAetheryte(GameState.TerritoryType, Vector3.Zero)
                              is { } currentRecord &&
                          currentRecord.Group == record.Group;
        return isSameGroup ?
                   0 :
                   record.Cost;
    }

    private static uint GetTeleportIconIndex(AetheryteRecord record)
    {
        if (!record.IsAetheryte)
            return 0;

        var playerState = PlayerState.Instance();
        if (playerState == null)
            return 0;

        if (playerState->FreeAetheryteIds[1] == record.RowID ||
            playerState->FreeAetheryteIds[2] == record.RowID)
            return 7;

        if (playerState->FreeAetheryteIds[0] == record.RowID)
            return playerState->IsPlayerStateFlagSet(PlayerStateFlag.IsLoginSecurityToken) ?
                       2u :
                       3u;

        return playerState->FavouriteAetherytes.Contains((ushort)record.RowID) ?
                   1u :
                   0u;
    }

    private static ReadOnlySeString GetTeleportText(AetheryteRecord record, uint addonID)
    {
        var cost = GetTeleportCost(record);

        var macroString = LuminaGetter.GetRowOrDefault<Addon>(addonID).Text.ToMacroString()
                                      .Replace(@"<kilo(lnum2,\,)>", "<string(lstr2)>");
        var iconIndex = GetTeleportIconIndex(record);
        if (record.Group == EXTRA_RECORD_GROUP)
            macroString = macroString.Replace("<sheet(PlaceName,<sheet(Aetheryte,lnum1,8)>,0)>", "<string(lstr4)>");
        else if (!record.IsAetheryte)
            macroString = macroString.Replace("Aetheryte,lnum1,8", "Aetheryte,lnum1,9");

        return ISeStringEvaluator.Instance().EvaluateMacroString
        (
            macroString,
            [record.RowID, cost.ToChineseString(), iconIndex, record.Name]
        );
    }

    private void OpenTeleportConfirmation(AetheryteRecord record, AtkUnitBase* addon)
    {
        teleportConfirmation?.Dispose();
        teleportConfirmation = SelectYesnoAddon.Open
        (
            new()
            {
                Prompt = GetTeleportText(record, 3217),
                Callback = (_, result) =>
                {
                    teleportConfirmation = null;

                    if (result != SelectYesnoAddonResult.Yes)
                        return;

                    if (record.TeleportTo())
                    {
                        AgentMap.Instance()->Hide();
                        IAddonEventManager.Instance().ResetCursor();
                    }
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

    private bool ShouldSkip() =>
        config.NoDisplayWhenInParty &&
        LocalPlayerState.IsInLocalParty;

    private class Config : ModuleConfig
    {
        public bool NoDisplayWhenInParty = true;
    }

    #region 常量

    private const ushort EXTRA_MARKER_TYPE       = ushort.MaxValue;
    private const byte   EXTRA_RECORD_GROUP      = 252;
    private const uint   AETHERNET_ICON_ID       = 60430;
    private const uint   MAP_MARKER_CONTEXT      = 2U << 24;
    private const ushort EXTRA_MARKER_ICON_FLAGS = 0x4002;

    #endregion
}
