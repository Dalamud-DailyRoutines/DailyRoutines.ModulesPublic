using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;

namespace DailyRoutines.ModulesPublic;

public unsafe class CharacterClassSwitcher : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "CharacterClassSwitcherTitle",
        Description = "CharacterClassSwitcherDescription",
        Category    = ModuleCategories.UIOperation,
        Author = ["Middo"]
    };

    private static readonly Dictionary<uint, uint> classJobComponentMap = new()
    {
        { 08, 19 }, // PLD
        { 10, 21 }, // WAR
        { 12, 32 }, // DRK
        { 14, 37 }, // GNB

        { 20, 24 }, // WHM
        { 22, 28 }, // SCH
        { 24, 33 }, // AST
        { 26, 40 }, // SGE

        { 32, 20 }, // MNK
        { 34, 22 }, // DRG
        { 36, 30 }, // NIN
        { 38, 34 }, // SAM
        { 40, 39 }, // RPR
        { 42, 41 }, // VPR

        { 48, 23 }, // BRD
        { 50, 31 }, // MCH
        { 52, 38 }, // DNC

        { 58, 25 }, // BLM
        { 60, 27 }, // SMN
        { 62, 35 }, // RDM
        { 64, 42 }, // PCT
        { 66, 36 }, // BLU

        { 71, 08 }, // CRP
        { 72, 09 }, // BSM
        { 73, 10 }, // ARM
        { 74, 11 }, // GSM
        { 75, 12 }, // LTW
        { 76, 13 }, // WVR
        { 77, 14 }, // ALC
        { 78, 15 }, // CUL

        { 84, 16 }, // MIN
        { 86, 17 }, // BTN
        { 88, 18 }, // FSH
    };
    private static readonly List<IAddonEventHandle> EventHandles = [];

    protected override void Init()
    {
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "CharacterClass", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "CharacterClass", OnAddon);
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        OnAddon(AddonEvent.PreFinalize, null);
    }

    private static byte? GetGearsetForClassJob(ClassJob cj)
    {
        byte? gearsetId = null;

        var gearsetModule = RaptureGearsetModule.Instance();

        if (gearsetModule == null)
            return null;

        for (var i = 0; i < gearsetModule->NumGearsets; i++)
        {
            var gearset = gearsetModule->GetGearset(i);

            if (gearset == null) continue;
            if (!gearset->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists)) continue;
            if (gearset->Id != i) continue;

            if (gearset->ClassJob == cj.RowId) return gearset->Id;
            if (gearsetId == null && cj.ClassJobParent.RowId != 0 && gearset->ClassJob == cj.ClassJobParent.RowId) // 获得基础职业id
                gearsetId = gearset->Id;
        }

        return gearsetId;
    }

    private static void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                if (CharacterClass != null)
                {
                    foreach (var (nodeId, cjId) in classJobComponentMap)
                    {
                        var componentNode = CharacterClass->GetComponentNodeById(nodeId);
                        if (componentNode == null) continue;
                        
                        switch (componentNode->Type)
                        {
                            case (NodeType)1001:
                                {
                                    // 生产职业（点击会打开分解技能列表）
                                    var colNode = (AtkCollisionNode*)componentNode->Component->UldManager.SearchNodeById(12);

                                    if (colNode == null || colNode->Type != NodeType.Collision) continue;

                                    if (colNode != null)
                                    {
                                        var evt = colNode->AtkEventManager.Event;
                                        while (evt != null)
                                        {
                                            if (evt->State.EventType is AtkEventType.MouseClick or AtkEventType.InputReceived)
                                                colNode->RemoveEvent(evt->State.EventType, evt->Param, evt->Listener, false);        

                                            evt = evt->NextEvent;
                                        }
                                    }

                                    if(DService.AddonEvent.AddEvent((nint)CharacterClass, (nint)colNode, AddonEventType.MouseClick, OnMouseClick) is { } clickHandler)
                                        EventHandles.Add(clickHandler);

                                    break;
                                }
                            case (NodeType)1003:
                                {
                                    // 其他职业
                                    var colNode = (AtkCollisionNode*)componentNode->Component->UldManager.SearchNodeById(8);

                                    if (colNode == null || colNode->Type != NodeType.Collision) continue;

                                    if(DService.AddonEvent.AddEvent((nint)CharacterClass, (nint)colNode, AddonEventType.MouseClick, OnMouseClick) is { } clickHandler)
                                        EventHandles.Add(clickHandler);

                                    break;
                                }
                        }
                    }
                }
                break;
            case AddonEvent.PreFinalize:
                foreach (var handle in EventHandles)
                    DService.AddonEvent.RemoveEvent(handle);

                EventHandles.Clear();
                break;
        }
    }

    private static void OnMouseClick(AddonEventType atkEventType, AddonEventData data)
    {
        var triggedComponentNodeId = ((AtkCollisionNode*)data.NodeTargetPointer)->ParentNode->NodeId;

        try
        {
            if (atkEventType is AddonEventType.MouseClick)
            {
                var cjId = classJobComponentMap[triggedComponentNodeId];
                if (!LuminaGetter.Get<ClassJob>().TryGetRow(cjId, out var classJob)) return;

                var gearsetId = GetGearsetForClassJob(classJob);

                if (gearsetId != null)
                    ChatHelper.SendMessage($"/gearset change {gearsetId.Value + 1}");
                else
                    Chat($"{GetLoc("CharacterClassSwitcher-GearsetNotFound")}{classJob.Name.ExtractText()}");
            }
        }
        catch (Exception ex)
        {
            DService.Log.Error(ex.ToString());
        }
    }

}
