using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ListRenderer = FFXIVClientStructs.Expansion.FFXIV.Component.GUI.AtkComponentListItemRenderer;
using NumericInput = FFXIVClientStructs.Expansion.FFXIV.Component.GUI.AtkComponentNumericInput;
using TreeList = FFXIVClientStructs.Expansion.FFXIV.Component.GUI.AtkComponentTreeList;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoNumericInputMax : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoNumericInputMaxTitle"),
        Description = Lang.Get("AutoNumericInputMaxDescription"),
        Category    = ModuleCategory.Interface,
        ModulesPair = ["AutoIncreaseBuyAmountLimit"]
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private Hook<AtkComponentNumericInput.Delegates.InnerSetValue> InnerSetValueHook;
    private Hook<NumericInput.Delegates.ReceiveNumericEvent>       ReceiveNumericEventHook;
    private Hook<ListRenderer.Delegates.Populate>                  PopulateListItemHook;
    private Hook<TreeList.Delegates.PopulateItem>                  PopulateTreeItemHook;

    private readonly Dictionary<ushort, InputState> inputStates    = [];
    private readonly Stack<HashSet<InputState>>     inputScopePool = [];

    private HashSet<InputState>? pendingInputs;

    private int  inputEventDepth;
    private bool isSettingMaximum;
    private bool isInitializingAddon;

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
        PopulateListItemHook ??= IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(ListRenderer.MemberFunctionPointers),
            nameof(ListRenderer.Populate),
            (ListRenderer.Delegates.Populate)PopulateListItemDetour
        );
        PopulateTreeItemHook ??= IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(TreeList.MemberFunctionPointers),
            nameof(TreeList.PopulateItem),
            (TreeList.Delegates.PopulateItem)PopulateTreeItemDetour
        );

        InnerSetValueHook.Enable();
        ReceiveNumericEventHook.Enable();
        PopulateListItemHook.Enable();
        PopulateTreeItemHook.Enable();
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreSetup,    OnAddonReset);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize, OnAddonReset);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup,   OnAddonSetup);
    }

    protected override void Uninit()
    {
        IAddonLifecycle.Instance().UnregisterListener(OnAddonSetup);
        IAddonLifecycle.Instance().UnregisterListener(OnAddonReset);
        inputStates.Clear();
        inputScopePool.Clear();
        pendingInputs = null;
    }

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

    private void PopulateListItemDetour
    (
        ListRenderer* renderer,
        void*         itemData,
        ushort        itemIndex,
        bool          skipPopulation
    )
    {
        if (isSettingMaximum ||
            skipPopulation)
        {
            PopulateListItemHook.Original(renderer, itemData, itemIndex, skipPopulation);
            return;
        }

        var previous = pendingInputs;
        if (!inputScopePool.TryPop(out var current))
            current = [];

        pendingInputs = current;

        try
        {
            PopulateListItemHook.Original(renderer, itemData, itemIndex, skipPopulation);
            pendingInputs = null;

            if (current.Count > 0)
                InitializeInputs(&renderer->UldManager, current);
        }
        finally
        {
            pendingInputs = previous;
            current.Clear();
            inputScopePool.Push(current);
        }
    }

    private byte PopulateTreeItemDetour
    (
        TreeList*                     list,
        uint                          itemIndex,
        AtkComponentTreeListItem*     item,
        AtkComponentListItemRenderer* renderer,
        bool                          isHighlighted,
        bool                          isDragging
    )
    {
        if (isSettingMaximum)
            return PopulateTreeItemHook.Original(list, itemIndex, item, renderer, isHighlighted, isDragging);

        var previous = pendingInputs;
        if (!inputScopePool.TryPop(out var current))
            current = [];

        pendingInputs = current;

        try
        {
            var result = PopulateTreeItemHook.Original(list, itemIndex, item, renderer, isHighlighted, isDragging);
            pendingInputs = null;

            if (current.Count > 0)
                InitializeInputs(&renderer->UldManager, current);

            return result;
        }
        finally
        {
            pendingInputs = previous;
            current.Clear();
            inputScopePool.Push(current);
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
            component->AtkTextNode == null ||
            component->OwnerNode   == null ||
            component->AtkResNode  == null)
        {
            InnerSetValueHook.Original(component, value, triggerCallback, playSoundEffect);
            return;
        }

        var addon = RaptureAtkUnitManager.Instance()->GetAddonByNode((AtkResNode*)component->OwnerNode);

        if (addon == null ||
            BlacklistAddons.Contains(addon->NameString))
        {
            InnerSetValueHook.Original(component, value, triggerCallback, playSoundEffect);
            return;
        }

        if (!inputStates.TryGetValue(addon->Id, out var root))
            inputStates[addon->Id] = root = new();

        var state = GetInputState((AtkResNode*)component->OwnerNode, addon->RootNode, root);

        if (pendingInputs != null)
        {
            InnerSetValueHook.Original(component, value, triggerCallback, playSoundEffect);

            if (state.Initialized                 &&
                state.Value   == component->Value &&
                state.Maximum == component->Data.Max)
                pendingInputs.Remove(state);
            else
                pendingInputs.Add(state);

            return;
        }

        if (inputEventDepth > 0                                                   ||
            (((NumericInput*)component)->IsEditingDigits && !isInitializingAddon) ||
            maximum < 1                                                           ||
            maximum >= 9999                                                       ||
            (!addon->IsVisible && !isInitializingAddon)                           ||
            !component->OwnerNode->IsVisible()                                    ||
            (state.Initialized && state.Value == value && state.Maximum == maximum))
        {
            var previousValue = component->Value;
            InnerSetValueHook.Original(component, value, triggerCallback, playSoundEffect);
            state.Value       =  component->Value;
            state.Maximum     =  component->Data.Max;
            state.Initialized |= inputEventDepth > 0 && previousValue != component->Value;
            return;
        }

        isSettingMaximum = true;

        try
        {
            InnerSetValueHook.Original(component, maximum, false, false);
            state.Value       = component->Value;
            state.Maximum     = component->Data.Max;
            state.Initialized = true;

            var update = new AtkEventDispatcher.Event
            {
                State = new AtkEventState
                {
                    EventType = AtkEventType.ValueUpdate
                },
                EventData = new AtkEventData
                {
                    ValueData = new AtkEventData.AtkValueData
                    {
                        NewValue  = component->Value,
                        LastValue = value
                    }
                }
            };
            component->OwnerNode->DispatchEvent(&update);
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

    private void OnAddonReset
    (
        AddonEvent type,
        AddonArgs  args
    ) =>
        inputStates.Remove(args.Addon.ToStruct()->Id);

    private void InitializeInputs
    (
        AtkUldManager*      manager,
        HashSet<InputState> inputs = null
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

                if (inputs != null)
                {
                    var addon = RaptureAtkUnitManager.Instance()->GetAddonByNode((AtkResNode*)numericInput->OwnerNode);
                    if (addon == null                                     ||
                        !inputStates.TryGetValue(addon->Id, out var root) ||
                        !inputs.Contains(GetInputState((AtkResNode*)numericInput->OwnerNode, addon->RootNode, root)))
                        continue;
                }

                numericInput->InnerSetValue(numericInput->Value, false, false);
            }
            else
                InitializeInputs(&component->UldManager, inputs);
        }
    }

    private static InputState GetInputState
    (
        AtkResNode* node,
        AtkResNode* root,
        InputState  state
    )
    {
        if (node == null ||
            node == root)
            return state;

        var parent = GetInputState(node->ParentNode, root, state);
        if (node->GetNodeType() < NodeType.Component)
            return parent;

        var component = ((AtkComponentNode*)node)->Component;
        if (component == null)
            return parent;

        var key = (node->NodeId, -1);
        if (component->GetComponentType() == ComponentType.ListItemRenderer)
            key = (0, ((AtkComponentListItemRenderer*)component)->ListItemIndex);

        if (!parent.Children.TryGetValue(key, out var child))
            parent.Children[key] = child = new();

        return child;
    }

    private sealed class InputState
    {
        public readonly Dictionary<(uint NodeID, int ItemIndex), InputState> Children = [];

        public int  Value;
        public int  Maximum;
        public bool Initialized;
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
