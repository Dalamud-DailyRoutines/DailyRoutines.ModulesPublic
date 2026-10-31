using System.Numerics;
using System.Runtime.InteropServices;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.AetheryteRecord;
using OmenTools.Interop.Game;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.KamiToolKit.Addons.SelectYesno;
using AgentReceiveEventDelegate = FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent.AgentReceiveEventDelegate;
using AgentMap = FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent.AgentMap;

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

    private MemoryPatch       aetheryteTopPatch = null!;
    private SelectYesnoAddon? teleportConfirmation;

    protected override void Init()
    {
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
    }

    protected override void Uninit()
    {
        teleportConfirmation?.Dispose();
        teleportConfirmation = null;
    }

    private uint BuildMapMarkersDetour
    (
        AgentMap*        agent,
        ExtendArrayData* array,
        bool             omitAetherytes,
        bool             onlyAetherytes
    )
    {
        Span<int> aethernetIndices = stackalloc int[agent->MapMarkerCount];
        var       count            = 0;

        for (var index = 0; index < agent->MapMarkerCount; index++)
        {
            ref var marker = ref agent->MapMarkers[index];
            if (marker.DataType != 4)
                continue;

            aethernetIndices[count++] = index;
            marker.DataType           = 3;
        }

        try
        {
            return BuildMapMarkersHook.Original(agent, array, omitAetherytes, onlyAetherytes);
        }
        finally
        {
            for (var index = 0; index < count; index++)
                agent->MapMarkers[aethernetIndices[index]].DataType = 4;
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
        if (eventKind            == 0 &&
            valueCount           >= 2 &&
            values[0].GetInt()   == 7 &&
            values[1].UInt >> 24 == 2)
        {
            var mapAgent = (AgentMap*)agent;
            var index    = (int)(values[1].UInt & 0xFFFFFF);

            if (index < mapAgent->MapMarkerCount)
            {
                var marker = mapAgent->MapMarkers[index];
                var record = marker.DataType switch
                {
                    3 => AetheryteRecordManager.Instance().AllRecords.FirstOrDefault
                    (x => x is { Group: < 252, IsHouse: false, IsAetheryte: true } &&
                          x.RowID == marker.DataKey
                    ),
                    4 => AetheryteRecordManager.Instance().AllRecords.FirstOrDefault
                    (x => x is { Group: < 252, IsHouse: false, IsAetheryte: false } &&
                          LuminaGetter.TryGetRow<Aetheryte>(x.RowID, out var data) &&
                          data.AethernetName.RowId == marker.DataKey
                    ),
                    _ => null
                };

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

    private void OpenTeleportConfirmation(AetheryteRecord record, AtkUnitBase* addon)
    {
        record.Update();
        var isSameGroup = AetheryteRecordManager.Instance().GetNearestAetheryte(GameState.TerritoryType, Vector3.Zero)
                              is { } currentRecord &&
                          currentRecord.Group == record.Group;
        var cost = isSameGroup ?
                       0 :
                       record.Cost;

        var macroString = LuminaGetter.GetRowOrDefault<Addon>(3217).Text.ToMacroString()
                                      .Replace(@"<kilo(lnum2,\,)>", "<string(lstr2)>");
        if (!record.IsAetheryte)
            macroString = macroString.Replace("Aetheryte,lnum1,8", "Aetheryte,lnum1,9");

        teleportConfirmation?.Dispose();
        teleportConfirmation = SelectYesnoAddon.Open
        (
            new()
            {
                Prompt = ISeStringEvaluator.Instance().EvaluateMacroString
                (
                    macroString,
                    [record.RowID, cost.ToChineseString()]
                ),
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
}
