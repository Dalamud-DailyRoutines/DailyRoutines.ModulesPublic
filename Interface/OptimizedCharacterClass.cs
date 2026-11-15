using System.Globalization;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
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

    private readonly Dictionary<uint, IAddonEventHandle[]> events    = [];
    private readonly Dictionary<uint, IAddonEventHandle[]> eventsPVP = [];

    protected override void Init()
    {
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostRequestedUpdate, "CharacterClass", OnAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize,         "CharacterClass", OnAddon);
        if (CharacterClass->IsAddonAndNodesReady())
            OnAddon(AddonEvent.PostRequestedUpdate, null);

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostRequestedUpdate, "PvPCharacter", OnAddonPVP);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize,         "PvPCharacter", OnAddonPVP);
        if (PvPCharacter->IsAddonAndNodesReady())
            OnAddonPVP(AddonEvent.PostRequestedUpdate, null);
    }

    protected override void Uninit()
    {
        IAddonLifecycle.Instance().UnregisterListener(OnAddon, OnAddonPVP);

        ClearEvents(events);
        ClearEvents(eventsPVP);
    }

    private static void AddCollisionEvent
    (
        AtkUnitBase*                          addon,
        AtkComponentBase*                     component,
        uint                                  classJobID,
        Dictionary<uint, IAddonEventHandle[]> registeredEvents
    )
    {
        if (registeredEvents.ContainsKey(classJobID))
            return;
        if (component == null)
            return;
        if (!LuminaGetter.TryGetRow(classJobID, out ClassJob classJob))
            return;

        var colNode = component->UldManager.SearchSimpleNodeByType<AtkCollisionNode>(NodeType.Collision);
        if (colNode == null) return;

        colNode->AtkEventManager.ClearEvents();

        using var rented = new RentedSeStringBuilder();
        var tooltip = rented.AppendIcon((uint)classJob.ToBitmapFontIcon())
                            .Append(CultureInfo.InvariantCulture, $" {classJob.Name}")
                            .ToReadOnlySeString();

        var eventManager = IAddonEventManager.Instance();
        var clickEvent = eventManager.AddEvent
        (
            (nint)addon,
            (nint)colNode,
            AddonEventType.MouseClick,
            (_, _) =>
            {
                LocalPlayerState.SwitchGearset(classJobID);
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
                eventManager.SetCursor(AddonCursorType.Clickable);
                UIGlobals.PlaySoundEffect(0);
                AtkStage.Instance()->TooltipManager.ShowTooltip
                (
                    ownerAddon->Id,
                    (AtkResNode*)data.NodeTargetPointer,
                    tooltip
                );
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
                eventManager.ResetCursor();
                AtkStage.Instance()->TooltipManager.HideTooltip(ownerAddon->Id);
            }
        );
        registeredEvents.Add(classJobID, [clickEvent, cursorOverEvent, cursorOutEvent]);
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
                ClearEvents(events);
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
                ClearEvents(eventsPVP);
                break;
        }
    }

    private static void ClearEvents(Dictionary<uint, IAddonEventHandle[]> registeredEvents)
    {
        var eventManager = IAddonEventManager.Instance();

        foreach (var classEvents in registeredEvents.Values)
        {
            foreach (var atkEvent in classEvents)
                eventManager.RemoveEvent(atkEvent);
        }

        registeredEvents.Clear();
    }

    #region 常量

    private const int CLASS_JOB_ICON_ARRAY_OFFSET = 25;
    private const int CLASS_JOB_ICON_ID_OFFSET    = 62100;

    #endregion
}
