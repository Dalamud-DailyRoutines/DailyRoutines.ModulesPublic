using System.Numerics;
using DailyRoutines.Common.KamiToolKit.Addons;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Premade.Node.Simple;
using Lumina.Text.ReadOnly;
using OmenTools.OmenService;
using ContextMenu = KamiToolKit.ContextMenu.ContextMenu;

namespace DailyRoutines.ModulesPublic.AutoRecordPartyFinderSettings;

public unsafe partial class AutoRecordPartyFinderSettings
{
    private sealed class AutoRecordPartyFinderSettingAddon
    (
        AutoRecordPartyFinderSettings module
    )
        : AttachedAddon("LookingForGroupCondition", AddonEvent.PostSetup)
    {
        private VerticalListNode?   mainLayout;
        private HorizontalListNode? actionHeader;
        private VerticalListNode?   presetListContainer;
        private HorizontalFlexNode? pagingLayout;

        private readonly List<PresetRowNode> presetRows  = [];
        private          TextButtonNode      prevPageBtn = null!;
        private          TextButtonNode      nextPageBtn = null!;
        private          TextNode            pageLabel   = null!;

        public           int         CurrentPageIndex;
        private readonly ContextMenu contextMenu = new();

        public override void Dispose()
        {
            contextMenu.Dispose();
            base.Dispose();
        }

        public void ShowContextMenu(PartyFinderSetting setting)
        {
            contextMenu.Clear();

            contextMenu.AddItem
            (
                Lang.Get("Update"),
                () =>
                {
                    if (LookingForGroupCondition == null || !LookingForGroupCondition->IsAddonAndNodesReady()) return;

                    var currentDisplayName = setting.DisplayName;
                    var updated            = module.config.Last.Copy();
                    updated.DisplayName = currentDisplayName;

                    var index = module.config.Slot.IndexOf(setting);

                    if (index != -1)
                    {
                        module.config.Slot[index] = updated;
                        module.config.Save(module);
                        RefreshPresetList();
                    }
                }
            );
            contextMenu.Open();
        }

        protected override AttachedAddonPosition AttachPosition =>
            AttachedAddonPosition.LeftTop;

        protected override Vector2 PositionOffset =>
            new(0f, 6f);

        protected override bool CanOpenAddon =>
            LookingForGroupCondition != null && LookingForGroupCondition->IsAddonAndNodesReady();

        protected override bool CanCloseHostAddon(AtkUnitBase* hostAddon) =>
            false;

        protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
        {
            if (WindowNode is WindowNode windowNode)
                windowNode.CloseButtonNode.IsVisible = false;

            FlagHelper.UpdateFlag(ref addon->Flags1A1, 0x4,  true);
            FlagHelper.UpdateFlag(ref addon->Flags1A0, 0x80, true);
            FlagHelper.UpdateFlag(ref addon->Flags1A1, 0x40, true);
            FlagHelper.UpdateFlag(ref addon->Flags1A3, 0x1,  true);

            mainLayout = new VerticalListNode
            {
                IsVisible   = true,
                Position    = ContentStartPosition,
                ItemSpacing = 6f,
                Size        = ContentSize,
                FitContents = true
            };

            actionHeader = new HorizontalListNode
            {
                IsVisible   = true,
                Size        = ContentSize with { Y = 32f },
                ItemSpacing = 8f
            };

            var addButton = new TextButtonNode
            {
                Size        = ContentSize with { Y = 32f },
                String      = Lang.Get("Add"),
                TextTooltip = Lang.Get("AutoRecordPartyFinderSetting-Button-Save-Help"),
                OnClick = () =>
                {
                    if (!LookingForGroupCondition->IsAddonAndNodesReady()) return;
                    var setting = module.config.Last.Copy();
                    setting.DisplayName =
                        LookingForGroupCondition->GetComponentByNodeId(11)->UldManager.SearchNodeById(2)->GetAsAtkComponentNode()->Component->GetTextNodeById
                            (3)->GetAsAtkTextNode()->NodeText.AsReadOnlySeString().ToString();

                    module.config.Slot.Add(setting);
                    module.config.Save(module);
                    RefreshPresetList();
                }
            };
            var backgroundNode = (SimpleNineGridNode)addButton.BackgroundNode;
            backgroundNode.TexturePath = "ui/uld/img04/ButtonB_hr1.tex";
            backgroundNode.TextureSize = new(80, 36);
            backgroundNode.Offsets     = new(16);

            actionHeader.AddNode(addButton);

            presetListContainer = new VerticalListNode
            {
                IsVisible   = true,
                ItemSpacing = 4f,
                FitContents = true,
                FitWidth    = true,
                Size        = ContentSize
            };

            presetRows.Clear();

            for (var i = 0; i < 10; i++)
            {
                var row = new PresetRowNode(module, this);
                presetListContainer.AddNode(row);
                presetRows.Add(row);
            }

            pagingLayout = new HorizontalFlexNode
            {
                IsVisible      = true,
                Size           = ContentSize with { Y = 28f },
                AlignmentFlags = FlexFlags.CenterHorizontally
            };

            prevPageBtn = new TextButtonNode
            {
                Size   = new(40f, 24f),
                String = "<",
                OnClick = () =>
                {
                    if (CurrentPageIndex > 0)
                    {
                        CurrentPageIndex--;
                        RefreshPresetList();
                    }
                }
            };

            pageLabel = new TextNode
            {
                TextFlags     = TextFlags.AutoAdjustNodeSize,
                String        = "1 / 1",
                AlignmentType = AlignmentType.Center,
                FontSize      = 14,
                Position      = new(0, 3)
            };

            nextPageBtn = new TextButtonNode
            {
                Size   = new(40f, 24f),
                String = ">",
                OnClick = () =>
                {
                    var totalItems = module.config.Slot.Count;
                    var totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / 10.0));

                    if (CurrentPageIndex < totalPages - 1)
                    {
                        CurrentPageIndex++;
                        RefreshPresetList();
                    }
                }
            };

            pagingLayout.AddNode(prevPageBtn);
            pagingLayout.AddDummy(10f);
            pagingLayout.AddNode(pageLabel);
            pagingLayout.AddDummy(10f);
            pagingLayout.AddNode(nextPageBtn);

            mainLayout.AddNode(actionHeader);
            mainLayout.AddNode
            (
                new HorizontalLineNode
                {
                    Size     = ContentSize with { Y = 4 },
                    Position = new(0, -4)
                }
            );
            mainLayout.AddNode(presetListContainer);
            mainLayout.AddDummy();
            mainLayout.AddNode(pagingLayout);

            mainLayout.AttachNode(this);

            RefreshPresetList();
        }

        public void RefreshPresetList()
        {
            var totalItems = module.config.Slot.Count;
            var totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / 10.0));

            if (CurrentPageIndex >= totalPages)
                CurrentPageIndex = totalPages - 1;
            if (CurrentPageIndex < 0)
                CurrentPageIndex = 0;

            var items = module.config.Slot.Skip(CurrentPageIndex * 10).Take(10).ToList();

            for (var i = 0; i < 10; i++)
            {
                var row = presetRows[i];

                if (i < items.Count)
                {
                    var setting = items[i];

                    row.IsVisible = true;
                    row.Update(setting);
                }
                else
                    row.IsVisible = false;
            }

            prevPageBtn.IsEnabled = CurrentPageIndex > 0;
            nextPageBtn.IsEnabled = CurrentPageIndex < totalPages - 1;
            pageLabel.String      = $"{CurrentPageIndex + 1} / {totalPages}";

            presetListContainer.RecalculateLayout();

            if (mainLayout != null)
            {
                mainLayout.RecalculateLayout();
                SetWindowSize(Size.X, ContentStartPosition.Y + mainLayout.Height + 16f);
                mainLayout.Position = ContentStartPosition;
            }
        }

        protected override void OnHostAddon(AddonEvent type, AddonArgs? args)
        {
            if (type == AddonEvent.PostSetup)
            {
                if (module.isAppliedOnce || !LookingForGroup->IsAddonAndNodesReady())
                    return;

                module.ApplyPreset(module.config.Last);
                module.isAppliedOnce = true;
            }
        }
    }

    private class PresetRowNode : HorizontalListNode
    {
        public PartyFinderSetting Setting { get; set; } = null!;

        private readonly TextButtonNode titleButton;
        private readonly TextButtonNode deleteButton;

        public PresetRowNode(AutoRecordPartyFinderSettings module, AutoRecordPartyFinderSettingAddon addon)
        {
            IsVisible   = true;
            Size        = new(addon.ContentSize.X - 8f, 28f);
            ItemSpacing = 6f;

            titleButton = new TextButtonNode
            {
                IsVisible = true,
                Size      = new(addon.ContentSize.X - 74f, 28f),
                String    = string.Empty
            };
            titleButton.LabelNode.TextFlags |= TextFlags.Ellipsis;
            deleteButton = new TextButtonNode
            {
                IsVisible = true,
                Size      = new(68f, 28f),
                String    = Lang.Get("Delete"),
                OnClick = () =>
                {
                    module.config.Slot.Remove(Setting);
                    module.config.Save(module);

                    var newTotalPages = Math.Max(1, (int)Math.Ceiling(module.config.Slot.Count / 10.0));
                    if (addon.CurrentPageIndex >= newTotalPages)
                        addon.CurrentPageIndex = newTotalPages - 1;

                    addon.RefreshPresetList();
                },
                TextTooltip = Lang.Get("Delete")
            };

            AddNode(titleButton);
            AddNode(deleteButton);

            titleButton.AddEvent
            (
                AtkEventType.MouseClick,
                (_, _, _, _, atkEventData) =>
                {
                    if (atkEventData->IsRightClick) // 右键
                        addon.ShowContextMenu(Setting);
                    else if (atkEventData->IsLeftClick)
                        module.ApplyPreset(Setting);
                }
            );
        }

        public void Update(PartyFinderSetting setting)
        {
            Setting = setting;

            var description = new ReadOnlySeString(setting.DescriptionBytes ?? []);
            var title       = string.IsNullOrEmpty(setting.DisplayName) ? $"[{Lang.Get("None")}]" : setting.DisplayName;

            titleButton.String = description;

            var tooltipText = Lang.GetSe("AutoRecordPartyFinderSetting-Message", title, description);
            titleButton.TextTooltip = tooltipText;
        }
    }
}
