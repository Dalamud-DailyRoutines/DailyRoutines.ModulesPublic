using System.Globalization;
using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Timelines;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic;

internal unsafe class OptimizedCharacterClass : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("OptimizedCharacterClassTitle"),
        Description = Lang.Get("OptimizedCharacterClassDescription"),
        Category    = ModuleCategory.Interface,
        Author      = ["Middo"]
    };

    public override string ModuleName => nameof(OptimizedCharacterClass);

    public override ModuleBehaviour Behaviour { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private Config config = null!;

    private readonly Dictionary<uint, (IAddonEventHandle[] Events, TimelineNode<ResNode> Highlight)> events    = [];
    private readonly Dictionary<uint, (IAddonEventHandle[] Events, TimelineNode<ResNode> Highlight)> eventsPVP = [];

    private ushort hoveredAddonID;
    private uint   hoveredNodeID;
    private uint   hoveredClassJobID;

    protected override void Init()
    {
        config = LoadConfig<Config>() ?? new();
        
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostRequestedUpdate, "CharacterClass", OnAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize,         "CharacterClass", OnAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreReceiveEvent,     "CharacterClass", OnAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostReceiveEvent,    "CharacterClass", OnAddon);
        if (CharacterClass->IsAddonAndNodesReady())
            OnAddon(AddonEvent.PostRequestedUpdate, null);

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostRequestedUpdate, "PvPCharacter", OnAddonPVP);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize,         "PvPCharacter", OnAddonPVP);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostReceiveEvent,    "PvPCharacter", OnAddonPVP);
        if (PvPCharacter->IsAddonAndNodesReady())
            OnAddonPVP(AddonEvent.PostRequestedUpdate, null);
    }

    protected override void Uninit()
    {
        IAddonLifecycle.Instance().UnregisterListener(OnAddon, OnAddonPVP);

        ClearEvents(CharacterClass, events);
        ClearEvents(PvPCharacter,   eventsPVP);
    }

    private void OnAddon
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        switch (type)
        {
            case AddonEvent.PostRequestedUpdate:
                var addon = (AddonCharacterClass*)CharacterClass;
                if (!addon->AtkUnitBase.IsAddonAndNodesReady())
                    return;
                if (events.Count == addon->ClassComponents.Length)
                    return;

                var numberArray = AtkStage.Instance()->GetNumberArrayData(NumberArrayType.Character);
                if (numberArray == null) return;

                for (var index = 0; index < addon->ClassComponents.Length; index++)
                {
                    var iconID = numberArray->IntArray[CLASS_JOB_ICON_ARRAY_OFFSET + index];
                    if (iconID < CLASS_JOB_ICON_ID_OFFSET)
                        continue;

                    AddCollisionEvent
                    (
                        (AtkUnitBase*)addon,
                        addon->ClassComponents[index].Value,
                        (uint)(iconID - CLASS_JOB_ICON_ID_OFFSET),
                        events
                    );
                }

                break;

            case AddonEvent.PreFinalize:
                ClearEvents(CharacterClass, events);
                break;

            case AddonEvent.PostReceiveEvent:
                if (args is AddonReceiveEventArgs { AtkEventType: AddonEventType.MouseOver })
                    ShowTooltip(CharacterClass);
                break;

            case AddonEvent.PreReceiveEvent:
                if (args is AddonReceiveEventArgs { AtkEventType: AddonEventType.ButtonClick } receiveArgs)
                {
                    var target = (AtkResNode*)((AtkEvent*)receiveArgs.AtkEvent)->Target;
                    if (target != null && events.ContainsKey(target->NodeId))
                        receiveArgs.AtkEventType = AddonEventType.MouseClick;
                }

                break;
        }
    }

    private void OnAddonPVP
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        switch (type)
        {
            case AddonEvent.PostRequestedUpdate:
                var addon = (AddonPvPCharacter*)PvPCharacter;
                if (!addon->AtkUnitBase.IsAddonAndNodesReady())
                    return;
                if (eventsPVP.Count == addon->ClassEntries.Length)
                    return;

                foreach (ref var entry in addon->ClassEntries)
                {
                    var iconNode = entry.Icon;
                    if (iconNode == null)
                        continue;

                    var partsList = iconNode->PartsList;
                    if (partsList        == null ||
                        iconNode->PartId >= partsList->PartCount)
                        continue;

                    var asset = partsList->Parts[iconNode->PartId].UldAsset;
                    if (asset == null)
                        continue;

                    var texture = &asset->AtkTexture;
                    if (texture->TextureType != TextureType.Resource ||
                        texture->Resource    == null)
                        continue;

                    var iconID = texture->Resource->IconId;
                    if (iconID < CLASS_JOB_ICON_ID_OFFSET)
                        continue;

                    AddCollisionEvent((AtkUnitBase*)addon, entry.Base, iconID - CLASS_JOB_ICON_ID_OFFSET, eventsPVP);
                }

                break;
            case AddonEvent.PreFinalize:
                ClearEvents(PvPCharacter, eventsPVP);
                break;
            case AddonEvent.PostReceiveEvent:
                if (args is AddonReceiveEventArgs { AtkEventType: AddonEventType.MouseOver })
                    ShowTooltip(PvPCharacter);
                break;
        }
    }

    private void AddCollisionEvent
    (
        AtkUnitBase*                                                                    addon,
        AtkComponentBase*                                                               component,
        uint                                                                            classJobID,
        Dictionary<uint, (IAddonEventHandle[] Events, TimelineNode<ResNode> Highlight)> registeredEvents
    )
    {
        if (component == null)
            return;
        var nodeID = component->OwnerNode->NodeId;
        if (registeredEvents.ContainsKey(nodeID))
            return;
        if (!LuminaGetter.TryGetRow(classJobID, out ClassJob _))
            return;

        var colNode = component->UldManager.SearchSimpleNodeByType<AtkCollisionNode>(NodeType.Collision);
        if (colNode == null)
            return;

        var highlight = AddHighlightTimeline((AtkResNode*)component->OwnerNode, classJobID is >= 8 and <= 15);
        addon->UldManager.UpdateDrawNodeList();
        addon->UpdateCollisionNodeList(false);

        var eventManager = IAddonEventManager.Instance();
        var clickEvent = eventManager.AddEvent
        (
            (nint)addon,
            (nint)colNode,
            AddonEventType.MouseClick,
            (_, addonEventData) =>
            {
                if (!TryFindClassJobGearset(hoveredClassJobID, out var gearsets))
                    return;
                
                if (addonEventData.IsRightClick)
                {
                    if (gearsets.Count > 1)
                    {
                        List<ContextMenuEntry> subMenus = [];

                        foreach (var gearset in gearsets)
                        {
                            subMenus.Add
                            (
                                new ContextMenuEntryInfo
                                (
                                    nameof(OptimizedCharacterClass),
                                    _ => new()
                                    {
                                        Name = Lang.Get
                                        (
                                            "OptimizedCharacterClass-SetSetForClassJob-Sub",
                                            new Dictionary<string, object>
                                            {
                                                ["index"] = gearset.Id + 1,
                                                ["name"]  = gearset.NameString
                                            }
                                        ),
                                        Priority = gearset.Id,
                                        OnClicked = _ =>
                                        {
                                            config.ClassJobGearsets.TryAdd(LocalPlayerState.ContentID, []);
                                            config.ClassJobGearsets[LocalPlayerState.ContentID][classJobID] = gearset.Id;
                                            config.Save(this);

                                            NotifyHelper.Instance().Chat
                                            (
                                                Lang.Get
                                                (
                                                    "OptimizedCharacterClass-SetSetForClassJob-Message",
                                                    new Dictionary<string, object>
                                                    {
                                                        ["index"]       = gearset.Id + 1,
                                                        ["jobName"]     = LuminaWrapper.GetJobName(classJobID),
                                                        ["gearsetName"] = gearset.NameString
                                                    }
                                                )
                                            );
                                        }
                                    }
                                )
                            );
                        }
                    
                        ContextMenuManager.Instance().Open
                        (
                            new()
                            {
                                Addon = addon
                            },
                            [
                                new ContextMenuEntryInfo
                                (
                                    nameof(OptimizedCharacterClass),
                                    _ => new ContextMenuItem
                                    {
                                        Name = Lang.Get
                                        (
                                            "OptimizedCharacterClass-SetSetForClassJob"
                                        ),
                                        Submenu = new()
                                        {
                                            Title   = Lang.Get("OptimizedCharacterClass-SetSetForClassJob"),
                                            Entries = subMenus
                                        }
                                    }
                                )
                            ]
                        );
                    }
                    
                    return;
                }
                
                var toUseGearset = gearsets[0];
                
                if (config.ClassJobGearsets.TryGetValue(LocalPlayerState.ContentID, out var classJobGearsets) &&
                    classJobGearsets.TryGetValue(hoveredClassJobID, out var classJobGearset)                  &&
                    gearsets.FirstOrDefault(x => x.Id == classJobGearset) is var configGearset                &&
                    configGearset.Id == classJobGearset)
                    toUseGearset = configGearset;
                
                LocalPlayerState.SwitchGearset(toUseGearset.Id);
                UIGlobals.PlaySoundEffect(1);
            }
        );
        var cursorOverEvent = eventManager.AddEvent
        (
            (nint)addon,
            (nint)colNode,
            AddonEventType.MouseOver,
            (_, data) =>
            {
                var ownerAddon = (AtkUnitBase*)data.AddonPointer;
                hoveredAddonID    = ownerAddon->Id;
                hoveredNodeID     = nodeID;
                hoveredClassJobID = classJobID;
                highlight.Timeline.PlayAnimation(HIGHLIGHT_ON_LABEL);
                eventManager.SetCursor(AddonCursorType.Clickable);
                UIGlobals.PlaySoundEffect(0);
                ShowTooltip(ownerAddon);
            }
        );
        var cursorOutEvent = eventManager.AddEvent
        (
            (nint)addon,
            (nint)colNode,
            AddonEventType.MouseOut,
            (_, data) =>
            {
                var ownerAddon = (AtkUnitBase*)data.AddonPointer;
                hoveredAddonID    = 0;
                hoveredNodeID     = 0;
                hoveredClassJobID = 0;
                highlight.Timeline.PlayAnimation(HIGHLIGHT_OFF_LABEL);
                eventManager.ResetCursor();
                AtkStage.Instance()->TooltipManager.HideTooltip(ownerAddon->Id);
            }
        );
        registeredEvents.Add(nodeID, ([clickEvent, cursorOverEvent, cursorOutEvent], highlight));
    }

    private void ShowTooltip(AtkUnitBase* addon)
    {
        if (addon->Id != hoveredAddonID)
            return;
        if (!LuminaGetter.TryGetRow(hoveredClassJobID, out ClassJob classJob))
            return;

        using var rented = new RentedSeStringBuilder();
        
        if (!LocalPlayerState.IsClassJobUnlocked(hoveredClassJobID))
        {
            rented.AppendIcon((uint)classJob.ToBitmapFontIcon())
                  .Append(CultureInfo.InvariantCulture, $"{classJob.Name}");
        }
        else if (!TryFindClassJobGearset(hoveredClassJobID, out var gearsets))
        {
            rented.AppendIcon(BitmapFontIcon.Warning)
                  .Append
                  (
                      Lang.Get
                      (
                          "OptimizedCharacterClass-NoGearset",
                          new Dictionary<string, object>
                          {
                              ["jobName"] = LuminaWrapper.GetJobName(hoveredClassJobID)
                          }
                      )
                  );
        }
        else
        {
            rented.AppendIcon((uint)classJob.ToBitmapFontIcon())
                  .Append(CultureInfo.InvariantCulture, $"{classJob.Name}");

            if (gearsets.Count > 1)
            {
                var toUseGearset = gearsets[0];
                
                if (config.ClassJobGearsets.TryGetValue(LocalPlayerState.ContentID, out var classJobGearsets) &&
                    classJobGearsets.TryGetValue(hoveredClassJobID, out var classJobGearset)                  &&
                    gearsets.FirstOrDefault(x => x.Id == classJobGearset) is var configGearset                &&
                    configGearset.Id == classJobGearset)
                    toUseGearset = configGearset;
                
                rented.AppendNewLine()
                      .Append
                      (
                          Lang.Get
                          (
                              "OptimizedCharacterClass-Gearset",
                              new Dictionary<string, object>
                              {
                                  ["index"] = toUseGearset.Id + 1,
                                  ["name"]  = toUseGearset.NameString
                              }
                          )
                      );
            }
        }

        var component = addon->GetComponentNodeById(hoveredNodeID)->Component;
        AtkStage.Instance()->TooltipManager.ShowTooltip
        (
            addon->Id,
            (AtkResNode*)component->UldManager.SearchSimpleNodeByType<AtkCollisionNode>(NodeType.Collision),
            rented.ToReadOnlySeString()
        );
    }

    private void ClearEvents
    (
        AtkUnitBase*                                                                    addon,
        Dictionary<uint, (IAddonEventHandle[] Events, TimelineNode<ResNode> Highlight)> registeredEvents
    )
    {
        var eventManager = IAddonEventManager.Instance();

        foreach (var (nodeID, (classEvents, highlight)) in registeredEvents)
        {
            foreach (var atkEvent in classEvents)
                eventManager.RemoveEvent(atkEvent);

            if (addon->IsAddonAndNodesReady())
                MoveNativeNode(addon->GetNodeById(nodeID), highlight.ResNode, false);

            highlight.Dispose();
        }

        if (addon != null && addon->Id == hoveredAddonID)
        {
            eventManager.ResetCursor();
            AtkStage.Instance()->TooltipManager.HideTooltip(addon->Id);
            hoveredAddonID    = 0;
            hoveredNodeID     = 0;
            hoveredClassJobID = 0;
        }

        registeredEvents.Clear();
    }

    private static TimelineNode<ResNode> AddHighlightTimeline(AtkResNode* node, bool isCrafter)
    {
        var addColor = isCrafter ?
                           new(16f) :
                           new Vector3(32f);
        
        var highlight = new TimelineNode<ResNode>
        {
            Size = new Vector2(node->Width, node->Height),
            LabelsetTimeline = new TimelineBuilder()
                               .BeginFrameSet(1, 53)
                               .AddLabelPair(1,  10, HIGHLIGHT_IDLE_LABEL)
                               .AddLabelPair(11, 17, HIGHLIGHT_ON_LABEL)
                               .AddLabelPair(47, 53, HIGHLIGHT_OFF_LABEL)
                               .EndFrameSet()
                               .Build(),
            ContentTimeline = new TimelineBuilder()
                              .AddFrameSetWithFrame(1, 10, 1, addColor: Vector3.Zero, multiplyColor: new(100f))
                              .BeginFrameSet(11, 17)
                              .AddFrame(11, addColor: Vector3.Zero, multiplyColor: new(100f))
                              .AddFrame(13, addColor: addColor,     multiplyColor: new(100f))
                              .EndFrameSet()
                              .BeginFrameSet(47, 53)
                              .AddFrame(47, addColor: addColor,     multiplyColor: new(100f))
                              .AddFrame(53, addColor: Vector3.Zero, multiplyColor: new(100f))
                              .EndFrameSet()
                              .Build()
        };

        highlight.AttachNode(node, NodePosition.BeforeTarget);
        MoveNativeNode(node, highlight.ContentNode.ResNode, true);
        highlight.Timeline.PlayAnimation(AtkTimelineJumpBehavior.Initialize, HIGHLIGHT_IDLE_LABEL);
        return highlight;
    }

    private static void MoveNativeNode(AtkResNode* node, AtkResNode* target, bool asChild)
    {
        if (node->NextSiblingNode != null)
            node->NextSiblingNode->PrevSiblingNode = node->PrevSiblingNode;
        else
            node->ParentNode->ChildNode = node->PrevSiblingNode;

        if (node->PrevSiblingNode != null)
            node->PrevSiblingNode->NextSiblingNode = node->NextSiblingNode;

        node->ParentNode->ChildCount--;
        node->PrevSiblingNode = null;
        node->NextSiblingNode = null;

        if (asChild)
        {
            node->ParentNode      = target;
            node->PrevSiblingNode = target->ChildNode;
            if (target->ChildNode != null)
                target->ChildNode->NextSiblingNode = node;

            target->ChildNode = node;
        }
        else
        {
            node->ParentNode      = target->ParentNode;
            node->NextSiblingNode = target->NextSiblingNode;
            node->PrevSiblingNode = target;
            if (target->NextSiblingNode != null)
                target->NextSiblingNode->PrevSiblingNode = node;
            else
                target->ParentNode->ChildNode = node;

            target->NextSiblingNode = node;
        }

        node->ParentNode->ChildCount++;
        node->IsDirty = true;
    }
    
    public static unsafe bool TryFindClassJobGearset
    (
        uint                                        classJobID,
        out List<RaptureGearsetModule.GearsetEntry> gearsets
    )
    {
        gearsets   = [];

        var gearsetModule = RaptureGearsetModule.Instance();

        for (var i = 0; i < 100; i++)
        {
            var gearset = gearsetModule->GetGearset(i);
            if (gearset == null                                                          ||
                !gearset->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists)         ||
                gearset->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.MainHandMissing) ||
                gearset->Id       != i                                                   ||
                gearset->ClassJob != classJobID)
                continue;

            gearsets.Add(*gearset);
        }
        
        return gearsets.Count > 0;
    }

    private class Config : ModuleConfig
    {
        // CID - ClassJob - GearSet
        public Dictionary<ulong, Dictionary<uint, int>> ClassJobGearsets = [];
    }
    
    #region 常量

    private const int CLASS_JOB_ICON_ARRAY_OFFSET = 25;
    private const int CLASS_JOB_ICON_ID_OFFSET    = 62100;

    private const ushort HIGHLIGHT_IDLE_LABEL = 1;
    private const ushort HIGHLIGHT_ON_LABEL   = 2;
    private const ushort HIGHLIGHT_OFF_LABEL  = 4;

    #endregion
}
