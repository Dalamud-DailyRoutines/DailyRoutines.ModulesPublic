using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;
using Lumina.Text.ReadOnly;

namespace DailyRoutines.ModulesPublic;

internal unsafe partial class FastInstanceZoneChange
{
    private AddonDRFastInstanceZoneChange? instanceZoneChangeAddon;

    private bool CanChangeInstance =>
        TaskHelper is { IsBusy: false } &&
        !ICondition.Instance().IsBetweenAreas;

    private static bool IsAddonDisplayable() =>
        InstancesManager.IsInstancedArea                   &&
        ValidUses.Contains(GameState.TerritoryIntendedUse) &&
        InstancesManager.Instance().GetInstancesCount() > 1;

    private void ToggleAddon()
    {
        if (instanceZoneChangeAddon is not null)
        {
            CloseAddon();
            return;
        }

        if (!IsAddonDisplayable())
            return;

        instanceZoneChangeAddon = new(this)
        {
            InternalName = "DRFastInstanceZoneChange",
            Title        = Lang.GetSe("FastInstanceZoneChangeTitle")
        };
        instanceZoneChangeAddon.Open();
    }

    private void CloseAddon()
    {
        var addon = instanceZoneChangeAddon;
        instanceZoneChangeAddon = null;
        addon?.Dispose();
    }

    private class AddonDRFastInstanceZoneChange
    (
        FastInstanceZoneChange module
    ) : NativeAddon
    {
        private readonly Dictionary<uint, TwoLineTextButtonNode> instanceButtons = [];

        private VerticalListNode listNode = null!;
        private bool             isClosing;

        protected override unsafe void OnSetup
        (
            AtkUnitBase*   addon,
            Span<AtkValue> atkValues
        )
        {
            listNode = new()
            {
                Position    = ContentStartPosition,
                Width       = BUTTON_WIDTH,
                FitContents = true,
                FitWidth    = true,
                ItemSpacing = BUTTON_SPACING
            };
            listNode.AttachNode(this);

            ICondition.Instance().ConditionChange += OnConditionChanged;
            module.TaskHelper.EnterBusyState      += UpdateButtonEnabledState;
            module.TaskHelper.LeaveBusyState      += UpdateButtonEnabledState;

            Rebuild();
        }

        protected override void OnHide
        (
            AtkUnitBase* addon
        )
        {
            UnsubscribeEvents();

            if (module.instanceZoneChangeAddon == this)
                module.instanceZoneChangeAddon = null;
        }

        protected override void OnFinalize
        (
            AtkUnitBase* addon
        )
        {
            UnsubscribeEvents();

            instanceButtons.Clear();
            listNode = null!;
        }

        public void Rebuild()
        {
            instanceButtons.Clear();
            listNode.Clear();

            var instanceCount = (uint)InstancesManager.Instance().GetInstancesCount();

            for (uint index = 1; index <= instanceCount; index++)
            {
                if (index == InstancesManager.CurrentInstance)
                    continue;

                var targetInstance = index;

                var button = new TwoLineTextButtonNode
                {
                    Size = new(BUTTON_WIDTH, BUTTON_HEIGHT),
                    String = Lang.GetSe
                    (
                        "FastInstanceZoneChange-ButtonTitle",
                        new Dictionary<string, object>
                        {
                            ["instanceText"] = targetInstance.ToSEHexCount()
                        }
                    ),
                    Hint = Lang.GetSe
                    (
                        "FastInstanceZoneChange-ButtonKeyHint",
                        new Dictionary<string, object>
                        {
                            ["keyboardHint"] = targetInstance.ToSESmallCount()
                        }
                    ),
                    OnClick = () => ChangeInstance(targetInstance)
                };

                instanceButtons.Add(targetInstance, button);
                listNode.AddNode(button);
            }

            UpdateButtonEnabledState();

            SetWindowSize(ContentStartPosition + listNode.Size + (2 * ContentPadding));

            var screenSize = new Vector2(AtkStage.Instance()->ScreenSize.Width, AtkStage.Instance()->ScreenSize.Height);
            SetWindowPosition((screenSize - Size) / 2f);
        }

        protected override void OnUpdate
        (
            AtkUnitBase* addon
        )
        {
            if (isClosing || !IsOpen)
                return;

            var keyCode = InputIDManager.ToStruct()->KeyboardInputs.KeyHeldKeycode;

            var instance = keyCode switch
            {
                >= DIGIT_KEY_CODE_FIRST and <= DIGIT_KEY_CODE_LAST               => (uint)(keyCode - DIGIT_KEY_CODE_FIRST)        + 1,
                >= NUMPAD_DIGIT_KEY_CODE_FIRST and <= NUMPAD_DIGIT_KEY_CODE_LAST => (uint)(keyCode - NUMPAD_DIGIT_KEY_CODE_FIRST) + 1,
                _                                                                => 0u
            };

            if (instance is 0                          ||
                !instanceButtons.ContainsKey(instance) ||
                !IKeyState.Instance()[(VirtualKey)keyCode])
                return;

            ChangeInstance(instance);
        }

        private void UnsubscribeEvents()
        {
            ICondition.Instance().ConditionChange -= OnConditionChanged;

            if (module.TaskHelper is not { } taskHelper)
                return;

            taskHelper.EnterBusyState -= UpdateButtonEnabledState;
            taskHelper.LeaveBusyState -= UpdateButtonEnabledState;
        }

        private void OnConditionChanged
        (
            ConditionFlag flag,
            bool          value
        )
        {
            if (flag is not (ConditionFlag.BetweenAreas or ConditionFlag.BetweenAreas51))
                return;

            UpdateButtonEnabledState();
        }

        private void UpdateButtonEnabledState()
        {
            var isEnabled = module.CanChangeInstance;

            foreach (var button in instanceButtons.Values)
                button.IsEnabled = isEnabled;
        }

        private void ChangeInstance
        (
            uint instance
        )
        {
            if (!module.CanChangeInstance)
                return;

            isClosing = true;

            ChatManager.Instance().SendMessage($"/pdr insc {instance}");
            module.CloseAddon();
        }

        #region 常量

        private const float BUTTON_WIDTH   = 280f;
        private const float BUTTON_HEIGHT  = 50f;
        private const float BUTTON_SPACING = 4f;

        private const byte DIGIT_KEY_CODE_FIRST        = 49;
        private const byte DIGIT_KEY_CODE_LAST         = 57;
        private const byte NUMPAD_DIGIT_KEY_CODE_FIRST = 97;
        private const byte NUMPAD_DIGIT_KEY_CODE_LAST  = 105;

        #endregion
    }
    
    private class TwoLineTextButtonNode : TextButtonNode
    {
        private readonly TextNode hintNode;

        public TwoLineTextButtonNode()
        {
            LabelNode.FontSize = TITLE_FONT_SIZE;

            hintNode = new()
            {
                AlignmentType = AlignmentType.Center,
                FontSize      = HINT_FONT_SIZE,
                TextColorID   = HINT_TEXT_COLOR_ID
            };
            hintNode.AttachNode(this);
        }

        public ReadOnlySeString Hint
        {
            set => hintNode.String = value;
        }

        protected override void OnSizeChanged()
        {
            base.OnSizeChanged();

            LabelNode.Size = new(Width - (HORIZONTAL_PADDING * 2), TITLE_HEIGHT);

            hintNode.Position = new(HORIZONTAL_PADDING, HINT_TOP);
            hintNode.Size     = new(Width - (HORIZONTAL_PADDING * 2), HINT_HEIGHT);
        }

        #region 常量

        private const float HORIZONTAL_PADDING = 16f;
        private const float TITLE_HEIGHT       = 22f;
        private const float HINT_TOP           = 24f;
        private const float HINT_HEIGHT        = 16f;
        private const uint  TITLE_FONT_SIZE    = 16;
        private const uint  HINT_FONT_SIZE     = 12;
        private const uint  HINT_TEXT_COLOR_ID = 3;

        #endregion
    }
}
