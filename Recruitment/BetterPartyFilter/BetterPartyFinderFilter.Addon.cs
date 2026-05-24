using System.Numerics;
using System.Reflection;
using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.KamiToolKit.Addons;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Premade.Node;
using KamiToolKit.Premade.Node.Simple;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic.BetterPartyFilter;

public partial class BetterPartyFinderFilter
{
    private TextButtonNode?         buttonNode;
    private bool                    isNeedToOpenAddon;
    private BetterPartyFinderFilterAddon? addon;

    private unsafe void OnAddon(AddonEvent type, AddonArgs args)
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
                    OnClick = () =>
                    {
                        isNeedToOpenAddon ^= true;
                        if (!isNeedToOpenAddon)
                            addon.Close();
                    }
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
                buttonNode?.Dispose();
                buttonNode = null;

                isNeedToOpenAddon = false;
                break;
        }
    }

    private unsafe class BetterPartyFinderFilterAddon
    (
        BetterPartyFinderFilter module
    ) : AttachedAddon("LookingForGroup")
    {
        private class RegexRow
        {
            public HorizontalListNode Row          { get; set; } = null!;
            public CheckboxNode       Checkbox     { get; set; } = null!;
            public TextInputNode      TextInput    { get; set; } = null!;
            public TextButtonNode     DeleteButton { get; set; } = null!;
        }

        private readonly List<RegexRow> regexRows = [];

        private TabBarNode tabBar1 = null!;
        private TabBarNode tabBar2 = null!;

        private static readonly FieldInfo RadioButtonsField =
            typeof(TabBarNode).GetField("radioButtons", BindingFlags.NonPublic | BindingFlags.Instance);

        private static void ClearTabBarSelection(TabBarNode bar)
        {
            if (RadioButtonsField.GetValue(bar) is List<TabBarRadioButtonNode> buttons)
            {
                foreach (var btn in buttons)
                {
                    btn.IsChecked  = false;
                    btn.IsSelected = false;
                }
            }
        }

        private VerticalListNode generalPanel     = null!;
        private VerticalListNode highEndPanel     = null!;
        private VerticalListNode descriptionPanel = null!;

        private CheckboxNode     ascCheckbox              = null!;
        private CheckboxNode     desCheckbox              = null!;
        private CheckboxNode     blacklistedCheckbox      = null!;
        private CheckboxNode     lockedCheckbox           = null!;
        private VerticalListNode notifyLayout             = null!;
        private CheckboxNode     notifyCheckbox           = null!;
        private NumericInputNode notifyIntervalInput      = null!;
        private CheckboxNode     noNotifyWhenZeroCheckbox = null!;
        
        private CheckboxNode       autoModeCheckbox      = null!;
        private CheckboxNode       manualModeCheckbox    = null!;
        private HorizontalListNode modeRow               = null!;
        private VerticalListNode   numLayout             = null!;

        private CheckboxNode       blacklistCheckbox = null!;
        private CheckboxNode       whitelistCheckbox = null!;
        private VerticalListNode   listContainer     = null!;
        private TextButtonNode     prevPageBtn       = null!;
        private TextButtonNode     nextPageBtn       = null!;
        private TextNode           pageLabel         = null!;

        private int currentPageIndex;
        private int currentActiveTab;

        protected override AttachedAddonPosition AttachPosition =>
            AttachedAddonPosition.LeftTop;

        protected override bool CanOpenAddon => module.isNeedToOpenAddon;

        protected override bool CanCloseHostAddon(AtkUnitBase* hostAddon) =>
            module.isNeedToOpenAddon;

        protected override void OnDraw(AtkUnitBase* addon)
        {
            if (!HostAddon->IsAddonAndNodesReady()) return;

            if (generalPanel.IsVisible)
            {
                ascCheckbox.IsChecked              = FlagStatusModule.Instance()->UIFlags[4]  == 1;
                desCheckbox.IsChecked              = FlagStatusModule.Instance()->UIFlags[4]  == 3;
                blacklistedCheckbox.IsChecked      = FlagStatusModule.Instance()->UIFlags[12] == 1;
                lockedCheckbox.IsChecked           = FlagStatusModule.Instance()->UIFlags[7]  == 0;
                notifyCheckbox.IsChecked           = NotifyNewRecruitment                     == 1;
                notifyIntervalInput.Value          = (int)FlagStatusModule.Instance()->UIFlags[5];
                noNotifyWhenZeroCheckbox.IsChecked = FlagStatusModule.Instance()->UIFlags[6] == 1;

                var isNotifyEnabled = notifyCheckbox.IsChecked;
                if (notifyLayout.IsVisible != isNotifyEnabled)
                {
                    notifyLayout.IsVisible = isNotifyEnabled;
                    notifyLayout.RecalculateLayout();
                    RecalculatePanel(generalPanel);
                }
            }
        }

        protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
        {
            currentPageIndex = 0;
            currentActiveTab = 0;

            if (WindowNode is WindowNode windowNode)
                windowNode.CloseButtonNode.IsVisible = false;

            SetupTabBars();
            SetupGeneralPanel();
            SetupHighEndPanel();
            SetupDescriptionPanel();

            SwitchTab(0);
            module.TaskHelper.Enqueue(() => ClearTabBarSelection(tabBar2));
        }

        private void SetupTabBars()
        {
            // 1. TabBar1
            tabBar1 = new TabBarNode
            {
                Position  = ContentStartPosition,
                Size      = ContentSize with { Y = 28f },
                IsVisible = true
            };

            tabBar1.AddTab(Lang.Get("General"),                                () => SwitchTab(0));
            tabBar1.AddTab(LuminaWrapper.GetAddonText(10822),                  () => SwitchTab(1));
            tabBar1.AddTab(Lang.Get("BetterPartyFinderFilter-Category-Description"), () => SwitchTab(2));

            tabBar1.AttachNode(this);

            // 2. TabBar2
            tabBar2 = new TabBarNode
            {
                Position  = ContentStartPosition + new Vector2(0f, 28f),
                Size      = ContentSize with { Y = 28f },
                IsVisible = true
            };

            tabBar2.AddTab(LuminaWrapper.GetAddonText(11070),                   () => OnActionTabClicked(3));
            tabBar2.AddTab(Lang.Get("Search"),                                  () => OnActionTabClicked(4));
            tabBar2.AddTab(Lang.Get("BetterPartyFinderFilter-Category-SearchByName"), () => OnActionTabClicked(5));

            tabBar2.AttachNode(this);
        }

        private void SetupGeneralPanel()
        {
            // 一般面板 (General)
            generalPanel = new VerticalListNode
            {
                IsVisible        = true,
                ItemSpacing      = 8f,
                FirstItemSpacing = 16f,
                FitContents      = true,
                FitWidth         = true,
                Size             = ContentSize
            };
            
            var displayLabel = new LabelTextNode
            {
                String    = LuminaWrapper.GetAddonText(11127),
                TextColor = ColorHelper.GetColor(2)
            };
            generalPanel.AddNode(displayLabel);
            generalPanel.AddDummy();

            var displayLayout = new VerticalListNode
            {
                FitContents = true,
                FitWidth    = true,
                Position    = new(20, 0)
            };

            var filterSameDescCheckbox = new CheckboxNode
            {
                Size      = new(280f, 24f),
                IsVisible = true,
                IsChecked = module.config.FilterSameDescription,
                String    = Lang.Get("BetterPartyFinderFilter-FilterDuplicate"),
                OnClick = isChecked =>
                {
                    module.config.FilterSameDescription = isChecked;
                    module.config.Save(module);
                }
            };
            displayLayout.AddNode(filterSameDescCheckbox);

            var orderRow = new HorizontalListNode
            {
                IsVisible = true,
                Size      = new(280f, 24f)
            };

            ascCheckbox = new CheckboxNode
            {
                Size      = new(135f, 24f),
                IsVisible = true,
                String    = LuminaWrapper.GetAddonText(10127),
                OnClick = isChecked =>
                {
                    if (isChecked)
                    {
                        AgentId.LookingForGroup.SendEvent(1, 24, 0, 0);
                        desCheckbox.IsChecked = false;
                    }
                    else
                        ascCheckbox.IsChecked = true;
                }
            };
            orderRow.AddNode(ascCheckbox);
            orderRow.AddDummy(10f);

            desCheckbox = new CheckboxNode
            {
                Size      = new(135f, 24f),
                IsVisible = true,
                String    = LuminaWrapper.GetAddonText(10128),
                OnClick = isChecked =>
                {
                    if (isChecked)
                    {
                        AgentId.LookingForGroup.SendEvent(1, 24, 1, 0);
                        ascCheckbox.IsChecked = false;
                    }
                    else
                        desCheckbox.IsChecked = true;
                }
            };
            orderRow.AddNode(desCheckbox);
            displayLayout.AddNode(orderRow);

            blacklistedCheckbox = new CheckboxNode
            {
                Size      = new(280f, 24f),
                IsVisible = true,
                String    = LuminaWrapper.GetAddonText(11124),
                OnClick = isChecked =>
                {
                    var currentLocked = FlagStatusModule.Instance()->UIFlags[7] == 0;
                    RefreshDisplaySettings(isChecked, currentLocked);
                }
            };
            displayLayout.AddNode(blacklistedCheckbox);

            lockedCheckbox = new CheckboxNode
            {
                Size      = new(280f, 24f),
                IsVisible = true,
                String    = LuminaWrapper.GetAddonText(11128),
                OnClick = isChecked =>
                {
                    var currentBlacklisted = FlagStatusModule.Instance()->UIFlags[12] == 1;
                    RefreshDisplaySettings(currentBlacklisted, isChecked);
                }
            };
            displayLayout.AddNode(lockedCheckbox);
            
            generalPanel.AddNode(displayLayout);

            var notifyLabel = new LabelTextNode
            {
                String    = LuminaWrapper.GetAddonText(11116),
                TextColor = ColorHelper.GetColor(2)
            };
            
            generalPanel.AddDummy(16f);
            generalPanel.AddNode(notifyLabel);

            var notifyLabelLayout = new VerticalListNode
            {
                Position         = new(20, 0),
                FitContents      = true,
                FitWidth         = true,
                FirstItemSpacing = 8f
            };
            
            notifyCheckbox = new CheckboxNode
            {
                Size      = new(280f, 24f),
                IsVisible = true,
                String    = LuminaWrapper.GetAddonText(11119),
                OnClick = isChecked =>
                {
                    RefreshDisplaySettings(notifyRecruitment: isChecked);
                    
                    var enabled = NotifyNewRecruitment == 1;
                    notifyLayout.IsVisible = enabled;

                    notifyLayout.RecalculateLayout();
                    RecalculatePanel(generalPanel);
                }
            };
            notifyLabelLayout.AddNode(notifyCheckbox);

            notifyLayout = new VerticalListNode
            {
                Position    = new(20, 0),
                FitContents = true,
                IsVisible   = NotifyNewRecruitment == 1,
            };

            var notifyIntervalLabel = new LabelTextNode
            {
                String    = LuminaWrapper.GetAddonText(11117),
                Size      = new(198f, 20f),
                Position  = new(0, 3),
                TextColor = ColorHelper.GetColor(31)
            };

            notifyIntervalInput = new NumericInputNode
            {
                Size     = new(220f, 24f),
                Position = new(20, 0),
                Min      = 1,
                Max      = 10,
                Value    = (int)FlagStatusModule.Instance()->UIFlags[5],
                OnValueUpdate = val => RefreshDisplaySettings(notifyInterval: (uint)val)
            };

            notifyLayout.AddNode(notifyIntervalLabel);
            notifyLayout.AddNode(notifyIntervalInput);

            noNotifyWhenZeroCheckbox = new CheckboxNode
            {
                Size      = new(260f, 24f),
                IsVisible = true,
                String    = LuminaWrapper.GetAddonText(11118),
                OnClick = isChecked =>
                {
                    RefreshDisplaySettings(noNotifyWhenZero: isChecked);
                }
            };
            
            notifyLayout.AddDummy(12f);
            notifyLayout.AddNode(noNotifyWhenZeroCheckbox);
            
            notifyLabelLayout.AddNode(notifyLayout);

            var notifyInfoLabel = new LabelTextNode
            {
                TextFlags = TextFlags.AutoAdjustNodeSize,
                String    = LuminaWrapper.GetAddonText(11171),
                Position  = new(0, 3),
                TextColor = ColorHelper.GetColor(3),
                FontSize  = 12
            };
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
                IsVisible   = false,
                ItemSpacing = 4f,
                FitContents = true,
                FitWidth    = true,
                Size        = ContentSize
            };

            var highEndFilterSameJobCheckbox = new CheckboxNode
            {
                Size      = new(280f, 24f),
                IsVisible = true,
                IsChecked = module.config.HighEndFilterSameJob,
                String    = Lang.Get("BetterPartyFinderFilter-HighEndFilter-SameJob"),
                OnClick = isChecked =>
                {
                    module.config.HighEndFilterSameJob = isChecked;
                    module.config.Save(module);
                }
            };
            highEndPanel.AddNode(highEndFilterSameJobCheckbox);

            var highEndFilterRoleCountCheckbox = new CheckboxNode
            {
                Size        = new(280f, 24f),
                IsVisible   = true,
                IsChecked   = module.config.HighEndFilterRoleCount,
                String      = Lang.Get("BetterPartyFinderFilter-HighEndFilter-RoleCount"),
                TextTooltip = Lang.Get("BetterPartyFinderFilter-HighEndFilter-RoleCount-Help"),
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
                Position    = new(20, 0),
                FitContents = true,
                FitWidth    = true
            };

            autoModeCheckbox = new CheckboxNode
            {
                Size        = new(135f, 24f),
                IsVisible   = module.config.HighEndFilterRoleCount,
                IsChecked   = !module.manualMode,
                String      = Lang.Get("AutoMode"),
                TextTooltip = Lang.Get("BetterPartyFinderFilter-HighEndFilter-RoleCount-AutoMode-Help"),
                OnClick = isChecked =>
                {
                    if (isChecked)
                    {
                        module.manualMode            = false;
                        manualModeCheckbox.IsChecked = false;
                    }
                    else
                        autoModeCheckbox.IsChecked = true;
                }
            };

            manualModeCheckbox = new CheckboxNode
            {
                Size        = new(135f, 24f),
                IsVisible   = module.config.HighEndFilterRoleCount,
                IsChecked   = module.manualMode,
                String      = Lang.Get("ManualMode"),
                TextTooltip = Lang.Get("BetterPartyFinderFilter-HighEndFilter-RoleCount-ManualMode-Help"),
                OnClick = isChecked =>
                {
                    if (isChecked)
                    {
                        module.manualMode          = true;
                        autoModeCheckbox.IsChecked = false;
                    }
                    else
                        manualModeCheckbox.IsChecked = true;
                }
            };

            modeRow = new HorizontalListNode
            {
                IsVisible = module.config.HighEndFilterRoleCount,
                Size      = new(280f, 24f)
            };

            modeRow.AddNode(autoModeCheckbox);
            modeRow.AddDummy(10f);
            modeRow.AddNode(manualModeCheckbox);

            filterRoleCountLayout.AddNode(modeRow);

            numLayout = new VerticalListNode
            {
                IsVisible   = module.config.HighEndFilterRoleCount,
                ItemSpacing = 4f,
                FitContents = true,
                FitWidth    = true,
                Size        = ContentSize
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

            filterRoleCountLayout.AddDummy(4f);
            filterRoleCountLayout.AddNode(numLayout);

            highEndPanel.AddNode(filterRoleCountLayout);

            highEndPanel.AttachNode(this);
        }

        private void SetupDescriptionPanel()
        {
            // 招募描述面板 (Description)
            descriptionPanel = new VerticalListNode
            {
                IsVisible        = false,
                ItemSpacing      = 8f,
                FirstItemSpacing = 8f,
                FitContents      = true,
                FitWidth         = true,
                Size             = ContentSize
            };

            var modeLabel = new LabelTextNode
            {
                String = Lang.Get("Mode")
            };
            descriptionPanel.AddNode(modeLabel);

            blacklistCheckbox = new CheckboxNode
            {
                Size        = new(135f, 24f),
                IsVisible   = true,
                IsChecked   = !module.config.IsWhiteList,
                String      = Lang.Get("Blacklist"),
                TextTooltip = Lang.Get("BetterPartyFinderFilter-Description-Blacklist-Help"),
                OnClick = isChecked =>
                {
                    if (isChecked)
                    {
                        module.config.IsWhiteList = false;
                        module.config.Save(module);
                        whitelistCheckbox.IsChecked = false;
                    }
                    else
                        blacklistCheckbox.IsChecked = true;
                }
            };

            whitelistCheckbox = new CheckboxNode
            {
                Size        = new(135f, 24f),
                IsVisible   = true,
                IsChecked   = module.config.IsWhiteList,
                String      = Lang.Get("Whitelist"),
                TextTooltip = Lang.Get("BetterPartyFinderFilter-Description-Whitelist-Help"),
                OnClick = isChecked =>
                {
                    if (isChecked)
                    {
                        module.config.IsWhiteList = true;
                        module.config.Save(module);
                        blacklistCheckbox.IsChecked = false;
                    }
                    else
                        whitelistCheckbox.IsChecked = true;
                }
            };

            var workModeRow = new HorizontalListNode
            {
                IsVisible = true,
                Size      = new(280f, 24f),
                Position  = new(16, 0),
            };
            workModeRow.AddNode(blacklistCheckbox);
            workModeRow.AddDummy(10f);
            workModeRow.AddNode(whitelistCheckbox);

            descriptionPanel.AddNode(workModeRow);

            var addPresetBtn = new TextButtonNode
            {
                Size      = new(280f, 28f),
                IsVisible = true,
                String    = $"{Lang.Get("Add")} ({Lang.Get("Regex")})",
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
                IsVisible   = true,
                ItemSpacing = 4f,
                FitContents = true,
                FitWidth    = true,
                Size        = ContentSize
            };
            descriptionPanel.AddNode(listContainer);

            for (var i = 0; i < 10; i++)
            {
                var row = new HorizontalListNode
                {
                    IsVisible = false,
                    Size      = new(280f, 32f)
                };

                var checkbox = new CheckboxNode
                {
                    Size      = new(28f, 28),
                    IsVisible = true,
                    String    = string.Empty
                };

                var textInput = new TextInputNode
                {
                    Size              = new(290f, 32),
                    PlaceholderString = Lang.Get("Regex")
                };

                var deleteBtn = new TextButtonNode
                {
                    Size     = new(42f, 28f),
                    Position = new(0, 3),
                    String   = Lang.Get("Delete")
                };

                row.AddNode(checkbox);
                row.AddDummy(4f);
                row.AddNode(textInput);
                row.AddDummy(4f);
                row.AddNode(deleteBtn);

                listContainer.AddNode(row);

                regexRows.Add
                (
                    new()
                    {
                        Row          = row,
                        Checkbox     = checkbox,
                        TextInput    = textInput,
                        DeleteButton = deleteBtn
                    }
                );
            }

            var pagingLayout = new HorizontalFlexNode
            {
                IsVisible      = true,
                Size           = ContentSize with { Y = 28f },
                AlignmentFlags = FlexFlags.CenterHorizontally,
                Position       = new(0, 6)
            };

            prevPageBtn = new TextButtonNode
            {
                Size   = new(40f, 24f),
                String = "<",
                OnClick = () =>
                {
                    if (currentPageIndex > 0)
                    {
                        currentPageIndex--;
                        RebuildRegexList();
                    }
                }
            };

            pageLabel = new TextNode
            {
                TextFlags     = TextFlags.AutoAdjustNodeSize,
                String        = "1 / 1",
                AlignmentType = AlignmentType.Left,
                Position      = new(0, 3)
            };

            nextPageBtn = new TextButtonNode
            {
                Size   = new(40f, 24f),
                String = ">",
                OnClick = () =>
                {
                    var totalPages = Math.Max(1, (int)Math.Ceiling(module.config.BlackList.Count / 10.0));

                    if (currentPageIndex < totalPages - 1)
                    {
                        currentPageIndex++;
                        RebuildRegexList();
                    }
                }
            };

            pagingLayout.AddNode(prevPageBtn);
            pagingLayout.AddNode(pageLabel);
            pagingLayout.AddNode(nextPageBtn);

            descriptionPanel.AddNode(pagingLayout);

            descriptionPanel.AttachNode(this);
        }

        private static HorizontalListNode CreateRoleCountNumericInput(uint addonTextID, int initialVal, Action<int> onValueUpdate)
        {
            var row = new HorizontalListNode
            {
                IsVisible = true,
                Size      = new(310f, 28f)
            };

            var icon = addonTextID switch
            {
                // 防护职业
                1082 => new SimpleNineGridNode
                {
                    TextureCoordinates = new(0, 80),
                    TextureSize        = new(28),
                    Size               = new(28),
                    TexturePath        = "ui/uld/img04/LFG_hr1.tex"
                },
                // 纯粹治疗职业
                11300 => new SimpleNineGridNode
                {
                    TextureCoordinates = new(0, 56),
                    TextureSize        = new(28),
                    Size               = new(28),
                    TexturePath        = "ui/uld/LFGSelectRole_hr1.tex"
                },
                // 护盾治疗职业
                11301 => new SimpleNineGridNode
                {
                    TextureCoordinates = new(28, 56),
                    TextureSize        = new(28),
                    Size               = new(28),
                    TexturePath        = "ui/uld/LFGSelectRole_hr1.tex"
                },
                // 近战职业
                1084 => new SimpleNineGridNode
                {
                    TextureCoordinates = new(0),
                    TextureSize        = new(28),
                    Size               = new(28),
                    TexturePath        = "ui/uld/LFGSelectRole_hr1.tex"
                },
                // 远程物理职业
                1085 => new SimpleNineGridNode
                {
                    TextureCoordinates = new(28, 0),
                    TextureSize        = new(28),
                    Size               = new(28),
                    TexturePath        = "ui/uld/LFGSelectRole_hr1.tex"
                },
                // 远程魔法职业
                1086 => new SimpleNineGridNode
                {
                    TextureCoordinates = new(56, 0),
                    TextureSize        = new(28),
                    Size               = new(28),
                    TexturePath        = "ui/uld/LFGSelectRole_hr1.tex"
                },
                _ => null
            };
            ArgumentNullException.ThrowIfNull((object?)icon);

            row.AddNode(icon);
            row.AddDummy(10f);

            var label = new LabelTextNode
            {
                String   = LuminaWrapper.GetAddonText(addonTextID),
                Size     = new(208f, 20f),
                Position = new(0, 3)
            };

            var numInput = new NumericInputNode
            {
                Size          = new(100f, 24f),
                Min           = -1,
                Max           = 8,
                Value         = initialVal,
                OnValueUpdate = onValueUpdate
            };

            row.AddNode(label);
            row.AddDummy(10f);
            row.AddNode(numInput);
            return row;
        }

        private void RecalculatePanel(LayoutListNode panel)
        {
            if (panel is not { IsVisible: true }) return;

            panel.RecalculateLayout();

            SetWindowSize(400f, ContentStartPosition.Y + tabBar1.Height + tabBar2.Height + panel.Height + 24f);
            panel.Position   = ContentStartPosition + new Vector2(0f, 62f);
            tabBar1.Position = ContentStartPosition;
            tabBar2.Position = ContentStartPosition + new Vector2(0f, 28f);
        }

        private void SwitchTab(int tabIndex)
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

        private void OnActionTabClicked(int actionIndex)
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
            autoModeCheckbox.IsVisible   = enabled;
            manualModeCheckbox.IsVisible = enabled;
            modeRow.IsVisible            = enabled;
            numLayout.IsVisible          = enabled;

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

                    regexRow.DeleteButton.OnClick = () =>
                    {
                        module.config.BlackList.RemoveAt(globalIndex);
                        module.config.Save(module);
                        var newTotalPages = Math.Max(1, (int)Math.Ceiling(module.config.BlackList.Count / 10.0));
                        if (currentPageIndex >= newTotalPages)
                            currentPageIndex = newTotalPages - 1;
                        RebuildRegexList();
                    };
                }
                else
                    regexRow.Row.IsVisible = false;
            }

            prevPageBtn.IsEnabled = currentPageIndex > 0;
            nextPageBtn.IsEnabled = currentPageIndex < totalPages - 1;
            pageLabel.String      = $"{currentPageIndex + 1} / {totalPages}";

            listContainer.RecalculateLayout();
            RecalculatePanel(descriptionPanel);
        }

        protected override void OnAttachedAddonUpdate(AtkUnitBase* addon, AtkUnitBase* hostAddon)
        {
            if (tabBar1 != null)
            {
                tabBar1.Position = ContentStartPosition;
                tabBar1.Width    = ContentSize.X;
            }

            if (tabBar2 != null)
            {
                tabBar2.Position = ContentStartPosition + new Vector2(0f, 28f);
                tabBar2.Width    = ContentSize.X;
            }

            switch (currentActiveTab)
            {
                case 0 when generalPanel != null:
                    RecalculatePanel(generalPanel);
                    break;
                case 1 when highEndPanel != null:
                    RecalculatePanel(highEndPanel);
                    break;
                case 2 when descriptionPanel != null:
                    RecalculatePanel(descriptionPanel);
                    break;
            }
        }
    }
}
