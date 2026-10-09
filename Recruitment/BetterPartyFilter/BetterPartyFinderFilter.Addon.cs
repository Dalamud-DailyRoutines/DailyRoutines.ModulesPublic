using System.Numerics;
using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.Info;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;
using OmenTools.Interop.Game.Lumina;
using OmenTools.KamiToolKit.Addons;
using OmenTools.KamiToolKit.Nodes;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.BetterPartyFilter;

public partial class BetterPartyFinderFilter
{
    private TextButtonNode?               buttonNode;
    private BetterPartyFinderFilterAddon? addon;

    private unsafe void OnAddon
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        switch (type)
        {
            case AddonEvent.PostDraw:
                if (LookingForGroup == null || buttonNode != null) return;

                buttonNode = new()
                {
                    Size     = new(154, 32),
                    String   = Lang.Get("Filter"),
                    Position = new(736, 72),
                    OnClick  = () => addon.Toggle()
                };

                buttonNode.LabelNode.AutoAdjustTextSize();
                buttonNode.AttachNode(LookingForGroup->RootNode);

                for (var i = 24U; i < 29; i++)
                {
                    var button = LookingForGroup->GetNodeById(i);
                    if (button == null)
                        continue;

                    button->ToggleVisibility(false);
                }

                break;

            case AddonEvent.PreFinalize:
                buttonNode = null;
                break;
        }
    }

    private unsafe class BetterPartyFinderFilterAddon
    (
        BetterPartyFinderFilter module
    ) : AttachedAddon("LookingForGroup", AddonEvent.PostRefresh, AddonEvent.PostReceiveEvent)
    {
        protected override bool AutoOpenAddon => false;

        private readonly List<RegexRow> regexRows = [];

        private TabBarNode tabBar1 = null!;
        private TabBarNode tabBar2 = null!;

        private VerticalListNode generalPanel     = null!;
        private VerticalListNode highEndPanel     = null!;
        private VerticalListNode descriptionPanel = null!;

        private RadioButtonGroupNode orderRadioGroup          = null!;
        private RadioButtonNode      ascRadioButton           = null!;
        private RadioButtonNode      desRadioButton           = null!;
        private CheckboxNode         blacklistedCheckbox      = null!;
        private CheckboxNode         lockedCheckbox           = null!;
        private VerticalListNode     notifyLayout             = null!;
        private CheckboxNode         notifyCheckbox           = null!;
        private NumericInputNode     notifyIntervalInput      = null!;
        private CheckboxNode         noNotifyWhenZeroCheckbox = null!;

        private RadioButtonGroupNode modeRadioGroup        = null!;
        private RadioButtonNode      autoModeRadioButton   = null!;
        private RadioButtonNode      manualModeRadioButton = null!;
        private VerticalListNode     numLayout             = null!;

        private RadioButtonGroupNode listModeRadioGroup   = null!;
        private RadioButtonNode      blacklistRadioButton = null!;
        private RadioButtonNode      whitelistRadioButton = null!;
        private VerticalListNode     listContainer        = null!;
        private PaginationNode       paginationBar        = null!;

        private int  currentPageIndex;
        private int  currentActiveTab;
        private bool isPanelReady;

        protected override void OnHostAddon
        (
            AddonEvent type,
            AddonArgs? args
        )
        {
            switch (type)
            {
                case AddonEvent.PostRefresh:
                case AddonEvent.PostReceiveEvent
                    when args is AddonReceiveEventArgs { AtkEventType: var eventType } eventArgs &&
                         (((AtkEventType)eventType == AtkEventType.ListItemClick && eventArgs.EventParam == 1) ||
                          ((AtkEventType)eventType == AtkEventType.ButtonClick   && eventArgs.EventParam == 7)):

                    if (IsRequestedOpen && currentActiveTab == 0)
                        RefreshGeneralPanel();

                    break;
            }
        }

        protected override void OnShow
        (
            AtkUnitBase* addon
        ) =>
            RefreshGeneralPanel();

        protected override void OnSetup
        (
            AtkUnitBase*   addon,
            Span<AtkValue> atkValues
        )
        {
            isPanelReady     = false;
            currentPageIndex = 0;
            currentActiveTab = 0;

            SetupTabBars();
            SetupGeneralPanel();
            SetupHighEndPanel();
            SetupDescriptionPanel();

            isPanelReady = true;
            SwitchTab(0);
            module.TaskHelper.Enqueue(() => ClearTabBarSelection(tabBar2));
        }

        protected override void OnFinalize
        (
            AtkUnitBase* addon
        )
        {
            isPanelReady = false;
            base.OnFinalize(addon);
        }

        private static void ClearTabBarSelection
        (
            TabBarNode bar
        )
        {
            foreach (var button in bar.TabButtons)
            {
                button.IsChecked  = false;
                button.IsSelected = false;
            }
        }

        private void RefreshGeneralPanel()
        {
            if (!isPanelReady) return;

            var flags = FlagStatusModule.Instance();
            orderRadioGroup.SelectedButton = (flags->UIFlags[4] & 2) == 0 ?
                                                 ascRadioButton :
                                                 desRadioButton;

            blacklistedCheckbox.IsChecked      = flags->UIFlags[12]                                                   == 1;
            lockedCheckbox.IsChecked           = flags->UIFlags[7]                                                    == 0;
            notifyCheckbox.IsChecked           = IGameConfig.Instance().UiConfig.GetUInt("PartyFinderNewArrivalDisp") == 1;
            noNotifyWhenZeroCheckbox.IsChecked = flags->UIFlags[6]                                                    == 1;

            var notifyInterval = (int)flags->UIFlags[5];
            if (notifyIntervalInput.Value != notifyInterval)
                notifyIntervalInput.Value = notifyInterval;

            if (notifyLayout.IsVisible != notifyCheckbox.IsChecked)
            {
                notifyLayout.IsVisible = notifyCheckbox.IsChecked;
                notifyLayout.RecalculateLayout();
                RecalculatePanel(generalPanel);
            }
        }

        private void RefreshDisplaySettings
        (
            bool? displayBlacklisted = null,
            bool? displayLocked      = null,
            bool? notifyRecruitment  = null,
            uint? notifyInterval     = null,
            bool? noNotifyWhenZero   = null
        )
        {
            var flags                     = FlagStatusModule.Instance();
            var notifyCurrentlyEnabled    = IGameConfig.Instance().UiConfig.GetUInt("PartyFinderNewArrivalDisp") == 1;
            var showBlacklisted           = displayBlacklisted ?? flags->UIFlags[12] == 1;
            var showLocked                = displayLocked      ?? flags->UIFlags[7]  == 0;
            var notifyEnabled             = notifyRecruitment  ?? notifyCurrentlyEnabled;
            var interval                  = notifyInterval     ?? flags->UIFlags[5];
            var suppressEmptyNotification = noNotifyWhenZero   ?? flags->UIFlags[6] == 1;

            if (showBlacklisted           == (flags->UIFlags[12] == 1) &&
                showLocked                == (flags->UIFlags[7]  == 0) &&
                notifyEnabled             == notifyCurrentlyEnabled    &&
                interval                  == flags->UIFlags[5]         &&
                suppressEmptyNotification == (flags->UIFlags[6] == 1))
                return;

            var notificationFlags = interval;
            if (suppressEmptyNotification)
                notificationFlags |= 0x10000;

            var displayFlags = notifyEnabled ?
                                   1U :
                                   0U;
            if (!showLocked)
                displayFlags |= 0x10000;
            if (!showBlacklisted)
                displayFlags |= 0x20000;

            AgentId.LookingForGroup.SendEvent(13, 0, notificationFlags, displayFlags);
            RefreshGeneralPanel();
        }

        private void SetupTabBars()
        {
            // 1. TabBar1
            tabBar1 = new TabBarNode
            {
                Position = ContentStartPosition,
                Size     = ContentSize with { Y = 28f }
            };

            tabBar1.AddTab(Lang.Get("General"),                                      () => SwitchTab(0));
            tabBar1.AddTab(LuminaWrapper.GetAddonText(10822),                        () => SwitchTab(1));
            tabBar1.AddTab(Lang.Get("BetterPartyFinderFilter-Category-Description"), () => SwitchTab(2));

            tabBar1.AttachNode(this);

            // 2. TabBar2
            tabBar2 = new TabBarNode
            {
                Position = ContentStartPosition + new Vector2(0f, 28f),
                Size     = ContentSize with { Y = 28f }
            };

            tabBar2.AddTab(LuminaWrapper.GetAddonText(11070),                         () => OnActionTabClicked(3));
            tabBar2.AddTab(Lang.Get("Search"),                                        () => OnActionTabClicked(4));
            tabBar2.AddTab(Lang.Get("BetterPartyFinderFilter-Category-SearchByName"), () => OnActionTabClicked(5));

            tabBar2.AttachNode(this);

            UpdateTabButtons(tabBar1);
            UpdateTabButtons(tabBar2);

            return;

            void UpdateTabButtons
            (
                TabBarNode tab
            )
            {
                foreach (var btn in tab.TabButtons)
                {
                    btn.TextTooltip         =  btn.String;
                    btn.LabelNode.TextFlags |= TextFlags.Ellipsis;
                }
            }
        }

        private void SetupGeneralPanel()
        {
            // 一般面板 (General)
            generalPanel = new VerticalListNode
            {
                ItemSpacing      = 8f,
                FirstItemSpacing = 16f,
                FitContents      = true,
                Alignment        = VerticalListAlignment.Right,
                Size             = ContentSize
            };

            var displayLabel = new LabelTextNode
            {
                String    = LuminaWrapper.GetAddonText(11127),
                Width     = ContentSize.X,
                TextColor = ColorHelper.GetColor(2)
            };
            generalPanel.AddNode(displayLabel);
            generalPanel.AddDummy();

            var displayLayout = new VerticalListNode
            {
                FitContents = true,
                Size        = ContentSize with { X = ContentSize.X - ROW_INDENT }
            };

            var orderRow = new HorizontalListNode
            {
                Size = ContentSize with { X = ContentSize.X - ROW_INDENT, Y = 24f }
            };

            orderRadioGroup = new RadioButtonGroupNode
            {
                FitToContentHeight         = false,
                Height                     = 28f,
                SelectFirstButtonByDefault = false
            };
            orderRadioGroup.LayoutOrientation = LayoutOrientation.Horizontal;
            orderRadioGroup.ItemSpacing       = ORDER_ROW_SPACING;

            ascRadioButton = orderRadioGroup.AddButton
            (
                LuminaWrapper.GetAddonText(10127),
                () => AgentId.LookingForGroup.SendEvent(1, 24, 0, 0)
            );

            desRadioButton = orderRadioGroup.AddButton
            (
                LuminaWrapper.GetAddonText(10128),
                () => AgentId.LookingForGroup.SendEvent(1, 24, 1, 0)
            );

            orderRow.AddNode(orderRadioGroup);
            displayLayout.AddNode(orderRow);

            var filterSameDescCheckbox = new CheckboxNode
            {
                Size      = ContentSize with { Y = 24f },
                IsChecked = module.config.FilterSameDescription,
                String    = Lang.Get("BetterPartyFinderFilter-FilterDuplicate"),
                OnClick = isChecked =>
                {
                    module.config.FilterSameDescription = isChecked;
                    module.config.Save(module);
                }
            };
            displayLayout.AddNode(filterSameDescCheckbox);

            blacklistedCheckbox = new CheckboxNode
            {
                Size    = ContentSize with { Y = 24f },
                String  = LuminaWrapper.GetAddonText(11124),
                OnClick = isChecked => RefreshDisplaySettings(isChecked)
            };
            displayLayout.AddNode(blacklistedCheckbox);

            lockedCheckbox = new CheckboxNode
            {
                Size    = ContentSize with { Y = 24f },
                String  = LuminaWrapper.GetAddonText(11128),
                OnClick = isChecked => RefreshDisplaySettings(displayLocked: isChecked)
            };
            displayLayout.AddNode(lockedCheckbox);

            generalPanel.AddNode(displayLayout);

            var notifyLabel = new LabelTextNode
            {
                String    = LuminaWrapper.GetAddonText(11116),
                Width     = ContentSize.X,
                TextColor = ColorHelper.GetColor(2)
            };

            generalPanel.AddDummy(16f);
            generalPanel.AddNode(notifyLabel);

            var notifyLabelLayout = new VerticalListNode
            {
                FitContents      = true,
                Alignment        = VerticalListAlignment.Right,
                FirstItemSpacing = 8f,
                Size             = ContentSize with { X = ContentSize.X - ROW_INDENT }
            };

            notifyCheckbox = new CheckboxNode
            {
                String  = LuminaWrapper.GetAddonText(11119),
                Size    = ContentSize with { X = ContentSize.X - ROW_INDENT, Y = 24f },
                OnClick = isChecked => RefreshDisplaySettings(notifyRecruitment: isChecked)
            };
            notifyLabelLayout.AddNode(notifyCheckbox);

            notifyLayout = new VerticalListNode
            {
                FitContents = true,
                IsVisible   = IGameConfig.Instance().UiConfig.GetUInt("PartyFinderNewArrivalDisp") == 1,
                Size        = ContentSize with { X = ContentSize.X - (ROW_INDENT * 2f) }
            };

            var notifyIntervalLabel = new LabelTextNode
            {
                String    = LuminaWrapper.GetAddonText(11117),
                Height    = 20f,
                Position  = new(0, 3),
                TextColor = ColorHelper.GetColor(31)
            };

            notifyIntervalInput = new NumericInputNode
            {
                Size          = new(220f, 24f),
                Min           = 1,
                Max           = 10,
                Value         = (int)FlagStatusModule.Instance()->UIFlags[5],
                OnValueUpdate = val => RefreshDisplaySettings(notifyInterval: (uint)val)
            };

            notifyLayout.AddNode(notifyIntervalLabel);
            notifyLayout.AddNode(notifyIntervalInput);

            noNotifyWhenZeroCheckbox = new CheckboxNode
            {
                Size    = ContentSize with { Y = 24f },
                String  = LuminaWrapper.GetAddonText(11118),
                OnClick = isChecked => RefreshDisplaySettings(noNotifyWhenZero: isChecked)
            };

            notifyLayout.AddDummy(12f);
            notifyLayout.AddNode(noNotifyWhenZeroCheckbox);

            notifyLabelLayout.AddNode(notifyLayout);

            var notifyInfoLabel = new LabelTextNode
            {
                TextFlags = TextFlags.AutoAdjustNodeSize,
                String    = LuminaWrapper.GetAddonText(11171),
                Position  = new(0, 3),
                FontSize  = 12,
                Width     = ContentSize.X - ROW_INDENT
            };
            AtkColors.Hint.ApplyTo(notifyInfoLabel);

            notifyLabelLayout.AddDummy(12f);
            notifyLabelLayout.AddNode(notifyInfoLabel);
            notifyLabelLayout.AddDummy(12f);

            generalPanel.AddNode(notifyLabelLayout);

            generalPanel.AttachNode(this);
        }

        private void SetupHighEndPanel()
        {
            // 高难度面板 (High-End)
            highEndPanel = new VerticalListNode
            {
                ItemSpacing      = 4f,
                FirstItemSpacing = 8f,
                FitContents      = true,
                Alignment        = VerticalListAlignment.Right,
                Size             = ContentSize
            };

            var highEndFilterSameJobCheckbox = new CheckboxNode
            {
                IsChecked   = module.config.HighEndFilterSameJob,
                String      = Lang.Get("BetterPartyFinderFilter-HighEndFilter-SameJob"),
                TextTooltip = Lang.Get("BetterPartyFinderFilter-HighEndFilter-SameJob-Help"),
                Size        = ContentSize with { Y = 24f },
                OnClick = isChecked =>
                {
                    module.config.HighEndFilterSameJob = isChecked;
                    module.config.Save(module);
                }
            };
            highEndPanel.AddNode(highEndFilterSameJobCheckbox);

            var highEndFilterRoleCountCheckbox = new CheckboxNode
            {
                IsChecked   = module.config.HighEndFilterRoleCount,
                String      = Lang.Get("BetterPartyFinderFilter-HighEndFilter-RoleCount"),
                TextTooltip = Lang.Get("BetterPartyFinderFilter-HighEndFilter-RoleCount-Help"),
                Size        = ContentSize with { Y = 24f },
                OnClick = isChecked =>
                {
                    module.config.HighEndFilterRoleCount = isChecked;
                    module.config.Save(module);
                    UpdateHighEndContainerVisibility();
                }
            };
            highEndPanel.AddNode(highEndFilterRoleCountCheckbox);

            var filterRoleCountLayout = new VerticalListNode
            {
                FitContents = true,
                Size        = ContentSize with { X = ContentSize.X - ROW_INDENT },
                ItemSpacing = 4f
            };

            modeRadioGroup = new RadioButtonGroupNode
            {
                FitToContentHeight         = false,
                Height                     = 28f,
                IsVisible                  = module.config.HighEndFilterRoleCount,
                SelectFirstButtonByDefault = false
            };
            modeRadioGroup.LayoutOrientation = LayoutOrientation.Horizontal;
            modeRadioGroup.ItemSpacing       = ORDER_ROW_SPACING;

            autoModeRadioButton = modeRadioGroup.AddButton
            (
                Lang.Get("AutoMode"),
                () => module.manualMode = false
            );
            autoModeRadioButton.TextTooltip = Lang.Get("BetterPartyFinderFilter-HighEndFilter-RoleCount-AutoMode-Help");

            manualModeRadioButton = modeRadioGroup.AddButton
            (
                Lang.Get("ManualMode"),
                () => module.manualMode = true
            );
            manualModeRadioButton.TextTooltip = Lang.Get("BetterPartyFinderFilter-HighEndFilter-RoleCount-ManualMode-Help");

            modeRadioGroup.SelectedButton = module.manualMode ?
                                                manualModeRadioButton :
                                                autoModeRadioButton;

            filterRoleCountLayout.AddNode(modeRadioGroup);

            numLayout = new VerticalListNode
            {
                IsVisible   = module.config.HighEndFilterRoleCount,
                ItemSpacing = 4f,
                FitContents = true,
                Size        = ContentSize with { X = ContentSize.X - ROW_INDENT }
            };

            numLayout.AddNode
            (
                CreateRoleCountNumericInput
                (
                    1082,
                    module.config.FilterJobTypeCountData.Tank,
                    val =>
                    {
                        module.config.FilterJobTypeCountData.Tank = val;
                        module.config.Save(module);
                    }
                )
            );
            numLayout.AddNode
            (
                CreateRoleCountNumericInput
                (
                    11300,
                    module.config.FilterJobTypeCountData.PureHealer,
                    val =>
                    {
                        module.config.FilterJobTypeCountData.PureHealer = val;
                        module.config.Save(module);
                    }
                )
            );
            numLayout.AddNode
            (
                CreateRoleCountNumericInput
                (
                    11301,
                    module.config.FilterJobTypeCountData.ShieldHealer,
                    val =>
                    {
                        module.config.FilterJobTypeCountData.ShieldHealer = val;
                        module.config.Save(module);
                    }
                )
            );
            numLayout.AddNode
            (
                CreateRoleCountNumericInput
                (
                    1084,
                    module.config.FilterJobTypeCountData.Melee,
                    val =>
                    {
                        module.config.FilterJobTypeCountData.Melee = val;
                        module.config.Save(module);
                    }
                )
            );
            numLayout.AddNode
            (
                CreateRoleCountNumericInput
                (
                    1085,
                    module.config.FilterJobTypeCountData.PhysicalRanged,
                    val =>
                    {
                        module.config.FilterJobTypeCountData.PhysicalRanged = val;
                        module.config.Save(module);
                    }
                )
            );
            numLayout.AddNode
            (
                CreateRoleCountNumericInput
                (
                    1086,
                    module.config.FilterJobTypeCountData.MagicalRanged,
                    val =>
                    {
                        module.config.FilterJobTypeCountData.MagicalRanged = val;
                        module.config.Save(module);
                    }
                )
            );

            filterRoleCountLayout.AddNode(numLayout);

            highEndPanel.AddNode(filterRoleCountLayout);

            highEndPanel.AttachNode(this);
        }

        private unsafe void SetupDescriptionPanel()
        {
            regexRows.Clear();

            // 招募描述面板 (Description)
            descriptionPanel = new VerticalListNode
            {
                IsVisible        = false,
                ItemSpacing      = 8f,
                FirstItemSpacing = 16f,
                FitContents      = true,
                Size             = ContentSize
            };

            var modeLabel = new LabelTextNode
            {
                String = Lang.Get("Mode")
            };
            AtkColors.Text.ApplyTo(modeLabel);
            descriptionPanel.AddNode(modeLabel);

            descriptionPanel.AddDummy();

            listModeRadioGroup = new RadioButtonGroupNode
            {
                FitToContentHeight         = false,
                Height                     = 28f,
                SelectFirstButtonByDefault = false
            };
            listModeRadioGroup.LayoutOrientation = LayoutOrientation.Horizontal;
            listModeRadioGroup.ItemSpacing       = ORDER_ROW_SPACING;

            blacklistRadioButton = listModeRadioGroup.AddButton
            (
                Lang.Get("Blacklist"),
                () =>
                {
                    module.config.IsWhiteList = false;
                    module.config.Save(module);
                }
            );
            blacklistRadioButton.TextTooltip = Lang.Get("BetterPartyFinderFilter-Description-Blacklist-Help");

            whitelistRadioButton = listModeRadioGroup.AddButton
            (
                Lang.Get("Whitelist"),
                () =>
                {
                    module.config.IsWhiteList = true;
                    module.config.Save(module);
                }
            );
            whitelistRadioButton.TextTooltip = Lang.Get("BetterPartyFinderFilter-Description-Whitelist-Help");

            listModeRadioGroup.SelectedButton = module.config.IsWhiteList ?
                                                    whitelistRadioButton :
                                                    blacklistRadioButton;

            var workModeRow = new HorizontalListNode
            {
                Size = ContentSize with { Y = 24f }
            };
            workModeRow.AddDummy(16f);
            workModeRow.AddNode(listModeRadioGroup);

            descriptionPanel.AddNode(workModeRow);

            var addPresetBtn = new TextButtonNode
            {
                Size        = ContentSize with { Y = 32 },
                String      = Lang.Get("BetterPartyFinderFilter-Description-Add"),
                TextureType = ButtonTextureType.ButtonB,
                OnClick = () =>
                {
                    module.config.BlackList.Add(new(true, string.Empty));
                    module.config.Save(module);
                    var totalPages = Math.Max(1, (int)Math.Ceiling(module.config.BlackList.Count / 10.0));
                    currentPageIndex = totalPages - 1;
                    RebuildRegexList();
                }
            };
            descriptionPanel.AddNode(addPresetBtn);

            listContainer = new VerticalListNode
            {
                ItemSpacing = 4f,
                FitContents = true,
                Size        = ContentSize
            };
            descriptionPanel.AddNode(listContainer);

            const float ROW_CHECKBOX_HEIGHT = 28f;
            const float ROW_ITEM_SPACING    = 4f;

            for (var i = 0; i < 10; i++)
            {
                var row = new HorizontalListNode
                {
                    IsVisible = false,
                    Size      = ContentSize with { Y = 32f }
                };

                var checkbox = new CheckboxNode
                {
                    Size   = new(ROW_CHECKBOX_HEIGHT, ROW_CHECKBOX_HEIGHT),
                    String = string.Empty
                };

                var textInput = new TextInputNode
                {
                    Size              = new(ContentSize.X - checkbox.Width - ROW_ITEM_SPACING, 32f),
                    PlaceholderString = Lang.Get("Regex")
                };

                row.AddNode(checkbox);
                row.AddDummy(ROW_ITEM_SPACING);
                row.AddNode(textInput);

                listContainer.AddNode(row);

                var regexRow = new RegexRow
                {
                    Row       = row,
                    Checkbox  = checkbox,
                    TextInput = textInput
                };

                checkbox.AddEvent
                (
                    AtkEventType.MouseClick,
                    (_, _, _, _, atkEventData) =>
                    {
                        if (atkEventData->IsRightClick)
                            ShowContextMenu(regexRow);
                    }
                );

                regexRows.Add(regexRow);
            }

            paginationBar = new PaginationNode
            {
                IsDisplayIndicatorText = true,
                OnPreviousPage = () =>
                {
                    currentPageIndex--;
                    RebuildRegexList();
                },
                OnNextPage = () =>
                {
                    currentPageIndex++;
                    RebuildRegexList();
                }
            };

            var pagingLayout = new HorizontalFlexNode
            {
                Size           = ContentSize with { Y = 28f },
                AlignmentFlags = FlexFlags.CenterHorizontally
            };
            pagingLayout.AddNode(paginationBar);

            descriptionPanel.AddNode(pagingLayout);

            descriptionPanel.AttachNode(this);
        }

        private HorizontalListNode CreateRoleCountNumericInput
        (
            uint        addonTextID,
            int         initialVal,
            Action<int> onValueUpdate
        )
        {
            const float ICON_SIZE   = 28f;
            const float INPUT_WIDTH = 100f;

            var row = new HorizontalListNode
            {
                Size = new(ContentSize.X - ROW_INDENT, 28f)
            };

            var icon = addonTextID switch
            {
                // 防护职业
                1082 => new SimpleNineGridNode
                {
                    TextureCoordinates = new(0, 80),
                    TextureSize        = new(28),
                    Size               = new(ICON_SIZE),
                    TexturePath        = "ui/uld/img04/LFG_hr1.tex"
                },
                // 纯粹治疗职业
                11300 => new SimpleNineGridNode
                {
                    TextureCoordinates = new(0, 56),
                    TextureSize        = new(28),
                    Size               = new(ICON_SIZE),
                    TexturePath        = "ui/uld/LFGSelectRole_hr1.tex"
                },
                // 护盾治疗职业
                11301 => new SimpleNineGridNode
                {
                    TextureCoordinates = new(28, 56),
                    TextureSize        = new(28),
                    Size               = new(ICON_SIZE),
                    TexturePath        = "ui/uld/LFGSelectRole_hr1.tex"
                },
                // 近战职业
                1084 => new SimpleNineGridNode
                {
                    TextureCoordinates = new(0),
                    TextureSize        = new(28),
                    Size               = new(ICON_SIZE),
                    TexturePath        = "ui/uld/LFGSelectRole_hr1.tex"
                },
                // 远程物理职业
                1085 => new SimpleNineGridNode
                {
                    TextureCoordinates = new(28, 0),
                    TextureSize        = new(28),
                    Size               = new(ICON_SIZE),
                    TexturePath        = "ui/uld/LFGSelectRole_hr1.tex"
                },
                // 远程魔法职业
                1086 => new SimpleNineGridNode
                {
                    TextureCoordinates = new(56, 0),
                    TextureSize        = new(28),
                    Size               = new(ICON_SIZE),
                    TexturePath        = "ui/uld/LFGSelectRole_hr1.tex"
                },
                _ => null
            };
            ArgumentNullException.ThrowIfNull((object?)icon);

            row.AddNode(icon);
            row.AddDummy(ORDER_ROW_SPACING);

            var label = new LabelTextNode
            {
                String   = LuminaWrapper.GetAddonText(addonTextID),
                Size     = new(row.Width - ICON_SIZE - INPUT_WIDTH - (ORDER_ROW_SPACING * 2f), 20f),
                Position = new(0, 3)
            };

            var numInput = new NumericInputNode
            {
                Size          = new(INPUT_WIDTH, 24f),
                Min           = -1,
                Max           = 8,
                Value         = initialVal,
                OnValueUpdate = onValueUpdate
            };

            row.AddNode(label);
            row.AddDummy(ORDER_ROW_SPACING);
            row.AddNode(numInput);
            return row;
        }

        private void RecalculatePanel
        (
            LayoutListNode panel
        )
        {
            if (panel is not { IsVisible: true }) return;

            panel.RecalculateLayout();

            SetWindowSize(Size.X, ContentStartPosition.Y + tabBar1.Height + tabBar2.Height + panel.Height + 24f);
            panel.Position   = ContentStartPosition + new Vector2(0f, 62f);
            tabBar1.Position = ContentStartPosition;
            tabBar1.Width    = ContentSize.X;
            tabBar2.Position = ContentStartPosition + new Vector2(0f, 28f);
            tabBar2.Width    = ContentSize.X;
        }

        private void SwitchTab
        (
            int tabIndex
        )
        {
            currentActiveTab = tabIndex;

            switch (tabIndex)
            {
                case 0:
                    tabBar1.SelectTab(Lang.Get("General"));
                    break;
                case 1:
                    tabBar1.SelectTab(LuminaWrapper.GetAddonText(10822));
                    break;
                case 2:
                    tabBar1.SelectTab(Lang.Get("BetterPartyFinderFilter-Category-Description"));
                    break;
            }

            ClearTabBarSelection(tabBar2);

            generalPanel.IsVisible     = tabIndex == 0;
            highEndPanel.IsVisible     = tabIndex == 1;
            descriptionPanel.IsVisible = tabIndex == 2;

            switch (tabIndex)
            {
                case 0:
                    RefreshGeneralPanel();
                    RecalculatePanel(generalPanel);
                    break;
                case 1:
                    UpdateHighEndContainerVisibility();
                    break;
                case 2:
                    RebuildRegexList();
                    break;
            }
        }

        private void OnActionTabClicked
        (
            int actionIndex
        )
        {
            switch (actionIndex)
            {
                case 3:
                    if (LFGFilterSettings != null)
                        LFGFilterSettings->Close(true);
                    else
                        AgentId.LookingForGroup.SendEvent(1, 25);
                    break;
                case 4:
                    if (LookingForGroupSearch != null)
                        LookingForGroupSearch->Close(true);
                    else
                        AgentId.LookingForGroup.SendEvent(1, 15);
                    break;
                case 5:
                    if (LookingForGroupNameSearch != null)
                        LookingForGroupNameSearch->Close(true);
                    else
                    {
                        AgentId.LookingForGroup.SendEvent(1,  19);
                        AgentId.LookingForGroup.SendEvent(10, 16);
                    }

                    break;
            }

            SwitchTab(currentActiveTab);
            module.TaskHelper.Enqueue(() => ClearTabBarSelection(tabBar2));
        }

        private void UpdateHighEndContainerVisibility()
        {
            var enabled = module.config.HighEndFilterRoleCount;
            modeRadioGroup.IsVisible = enabled;
            numLayout.IsVisible      = enabled;

            numLayout.RecalculateLayout();
            RecalculatePanel(highEndPanel);
        }

        private void RebuildRegexList()
        {
            var totalItems = module.config.BlackList.Count;
            var totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / 10.0));

            if (currentPageIndex >= totalPages)
                currentPageIndex = totalPages - 1;
            if (currentPageIndex < 0)
                currentPageIndex = 0;

            var items = module.config.BlackList.Skip(currentPageIndex * 10).Take(10).ToList();

            for (var i = 0; i < 10; i++)
            {
                var regexRow = regexRows[i];

                if (i < items.Count)
                {
                    var localItem   = items[i];
                    var globalIndex = (currentPageIndex * 10) + i;

                    regexRow.Index              = globalIndex;
                    regexRow.Row.IsVisible      = true;
                    regexRow.Checkbox.IsChecked = localItem.Key;
                    regexRow.Checkbox.OnClick = isChecked =>
                    {
                        module.config.BlackList[globalIndex] = new(isChecked, localItem.Value);
                        module.config.Save(module);
                    };

                    regexRow.TextInput.String = localItem.Value;
                    regexRow.TextInput.OnInputComplete = text =>
                    {
                        module.HandleRegexUpdate(globalIndex, module.config.BlackList[globalIndex].Key, text.ToString());
                    };
                }
                else
                    regexRow.Row.IsVisible = false;
            }

            paginationBar.PreviousPageButtonNode.IsEnabled = currentPageIndex > 0;
            paginationBar.NextPageButtonNode.IsEnabled     = currentPageIndex < totalPages - 1;
            paginationBar.IndicatorTextNode.String         = $"{currentPageIndex + 1} / {totalPages}";

            listContainer.RecalculateLayout();
            RecalculatePanel(descriptionPanel);
        }

        private void ShowContextMenu
        (
            RegexRow regexRow
        )
        {
            List<ContextMenuItem> menus =
            [
                new()
                {
                    Name = Lang.Get("BetterPartyFinderFilter-Description-Delete"),
                    OnClicked = _ =>
                    {
                        module.config.BlackList.RemoveAt(regexRow.Index);
                        module.config.Save(module);

                        var newTotalPages = Math.Max(1, (int)Math.Ceiling(module.config.BlackList.Count / 10.0));
                        if (currentPageIndex >= newTotalPages)
                            currentPageIndex = newTotalPages - 1;

                        RebuildRegexList();
                    }
                }
            ];

            ContextMenuManager.Instance().Open
            (
                new ContextMenuOpenedArgs(),
                [
                    new ContextMenuEntryInfo
                    (
                        nameof(ContextMenuManager),
                        _ => menus,
                        omitPrefix: true
                    )
                ]
            );
        }

        private class RegexRow
        {
            public HorizontalListNode Row       { get; init; } = null!;
            public CheckboxNode       Checkbox  { get; init; } = null!;
            public TextInputNode      TextInput { get; init; } = null!;
            public int                Index     { get; set; }
        }

        #region 常量

        private const float ORDER_ROW_SPACING = 10f;
        private const float ROW_INDENT        = 20f;

        #endregion
    }
}
