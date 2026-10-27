using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using NumericInput = FFXIVClientStructs.Expansion.FFXIV.Component.GUI.AtkComponentNumericInput;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe class AutoNumericInputMax : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoNumericInputMaxTitle"),
        Description = Lang.Get("AutoNumericInputMaxDescription"),
        Category    = ModuleCategory.Interface
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private Hook<AtkComponentNumericInput.Delegates.InnerSetValue> InnerSetValueHook;
    private Hook<NumericInput.Delegates.ReceiveNumericEvent>       ReceiveNumericEventHook;
    private Hook<NumericInput.Delegates.SetMaximum>                SetMaximumHook;

    private int  inputEventDepth;
    private bool isSettingMaximum;
    private bool isInitializingAddon;
    private bool maximumChanged;

    protected override void Init()
    {
        InnerSetValueHook ??= IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(AtkComponentNumericInput.MemberFunctionPointers),
            nameof(AtkComponentNumericInput.InnerSetValue),
            (AtkComponentNumericInput.Delegates.InnerSetValue)InnerSetValueDetour
        );
        ReceiveNumericEventHook ??= IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(NumericInput.MemberFunctionPointers),
            nameof(NumericInput.ReceiveNumericEvent),
            (NumericInput.Delegates.ReceiveNumericEvent)ReceiveNumericEventDetour
        );
        SetMaximumHook ??= IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(NumericInput.MemberFunctionPointers),
            nameof(NumericInput.SetMaximum),
            (NumericInput.Delegates.SetMaximum)SetMaximumDetour
        );

        InnerSetValueHook.Enable();
        ReceiveNumericEventHook.Enable();
        SetMaximumHook.Enable();
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup, OnAddonSetup);
    }

    protected override void Uninit() =>
        IAddonLifecycle.Instance().UnregisterListener(OnAddonSetup);

    private void ReceiveNumericEventDetour
    (
        NumericInput* component,
        AtkEventType  eventType,
        int           eventParam,
        AtkEvent*     atkEvent,
        AtkEventData* eventData
    )
    {
        inputEventDepth++;

        try
        {
            ReceiveNumericEventHook.Original(component, eventType, eventParam, atkEvent, eventData);
        }
        finally
        {
            inputEventDepth--;
        }
    }

    private void SetMaximumDetour
    (
        NumericInput* component,
        int           maximum,
        bool          triggerCallback,
        bool          playSoundEffect
    )
    {
        var previous = maximumChanged;
        maximumChanged = component->Data.Max != maximum;

        try
        {
            SetMaximumHook.Original(component, maximum, triggerCallback, playSoundEffect);
        }
        finally
        {
            maximumChanged = previous;
        }
    }

    private void InnerSetValueDetour
    (
        AtkComponentNumericInput* component,
        int                       value,
        bool                      triggerCallback,
        bool                      playSoundEffect
    )
    {
        var maximum = component->Data.Max;

        if (isSettingMaximum               ||
            inputEventDepth > 0            ||
            component->IsActive            ||
            maximum                <= 1    ||
            maximum                >= 9999 ||
            component->AtkTextNode == null ||
            component->OwnerNode   == null ||
            component->AtkResNode  == null ||
            !component->AtkResNode->NodeFlags.HasFlag(NodeFlags.Visible))
        {
            InnerSetValueHook.Original(component, value, triggerCallback, playSoundEffect);
            return;
        }

        if (!isInitializingAddon &&
            !maximumChanged      &&
            value == component->Value)
        {
            InnerSetValueHook.Original(component, value, triggerCallback, playSoundEffect);
            return;
        }

        var addon = RaptureAtkUnitManager.Instance()->GetAddonByNode((AtkResNode*)component->OwnerNode);

        if (addon == null                               ||
            (!addon->IsVisible && !isInitializingAddon) ||
            BlacklistAddons.Contains(addon->NameString))
        {
            InnerSetValueHook.Original(component, value, triggerCallback, playSoundEffect);
            return;
        }

        isSettingMaximum = true;

        try
        {
            InnerSetValueHook.Original(component, maximum, true, false);
        }
        finally
        {
            isSettingMaximum = false;
        }
    }

    private void OnAddonSetup
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        var addon = args.Addon.ToStruct();
        if (BlacklistAddons.Contains(addon->NameString))
            return;

        var previous = isInitializingAddon;
        isInitializingAddon = true;

        try
        {
            InitializeInputs(&addon->UldManager);
        }
        finally
        {
            isInitializingAddon = previous;
        }
    }

    private static void InitializeInputs
    (
        AtkUldManager* manager
    )
    {
        for (var i = 0; i < manager->NodeListCount; i++)
        {
            var node = manager->NodeList[i];
            if (node                == null ||
                node->GetNodeType() < NodeType.Component)
                continue;

            var component = ((AtkComponentNode*)node)->Component;
            if (component == null)
                continue;

            if (component->GetComponentType() == ComponentType.NumericInput)
            {
                var numericInput = (AtkComponentNumericInput*)component;
                numericInput->InnerSetValue(numericInput->Value, false, false);
            }
            else
                InitializeInputs(&component->UldManager);
        }
    }

    #region 常量

    private static readonly FrozenSet<string> BlacklistAddons =
    [
        "RetainerSell",
        "ItemSearch",
        "LookingForGroupSearch",
        "LookingForGroupCondition",
        "CountDownSettingDialog",
        "ConfigSystem",
        "ConfigCharacter"
    ];

    #endregion
}
