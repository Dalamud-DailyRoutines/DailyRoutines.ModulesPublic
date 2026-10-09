using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using Lumina.Text.ReadOnly;
using OmenTools.KamiToolKit.Addons;
using OmenTools.KamiToolKit.Nodes;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.AutoRecordPartyFinderSettings;

public unsafe partial class AutoRecordPartyFinderSetting
{
    private sealed class AutoRecordPartyFinderSettingAddon
    (
        AutoRecordPartyFinderSetting module
    ) : AttachedAddon("LookingForGroupCondition", AddonEvent.PostSetup)
    {
        private VerticalListNode?   mainLayout;
        private HorizontalListNode? actionHeader;
        private VerticalListNode?   presetListContainer;
        private PaginationNode?     paginationBar;

        private readonly List<PresetRowNode> presetRows = [];

        public int CurrentPageIndex;

        protected override void OnSetup
        (
            AtkUnitBase*   addon,
            Span<AtkValue> atkValues
        )
        {
            mainLayout = new VerticalListNode
            {
                Position    = ContentStartPosition,
                ItemSpacing = 6f,
                Size        = ContentSize,
                FitContents = true
            };

            actionHeader = new HorizontalListNode
            {
                Size        = ContentSize with { Y = 32f },
                ItemSpacing = 8f
            };

            var addButton = new TextButtonNode
            {
                Size        = ContentSize with { Y = 32f },
                String      = Lang.Get("AutoRecordPartyFinderSetting-Button-Save"),
                TextureType = ButtonTextureType.ButtonB,
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

            actionHeader.AddNode(addButton);

            presetListContainer = new VerticalListNode
            {
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

            paginationBar = new PaginationNode
            {
                IsDisplayIndicatorText = true,
                OnSizeUpdated          = CenterPaginationBar,
                OnPreviousPage = () =>
                {
                    CurrentPageIndex--;
                    RefreshPresetList();
                },
                OnNextPage = () =>
                {
                    CurrentPageIndex++;
                    RefreshPresetList();
                }
            };

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
            mainLayout.AddNode(paginationBar);
            mainLayout.AddDummy();

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

            paginationBar.PreviousPageButtonNode.IsEnabled = CurrentPageIndex > 0;
            paginationBar.NextPageButtonNode.IsEnabled     = CurrentPageIndex < totalPages - 1;
            paginationBar.IndicatorTextNode.String         = $"{CurrentPageIndex + 1} / {totalPages}";

            presetListContainer.RecalculateLayout();

            if (mainLayout != null)
            {
                mainLayout.RecalculateLayout();
                SetWindowSize(Size.X, ContentStartPosition.Y + mainLayout.Height + 16f);
                mainLayout.Position = ContentStartPosition;
            }

            CenterPaginationBar();
        }

        private void CenterPaginationBar()
        {
            if (paginationBar is not { } bar) return;

            bar.X = (ContentSize.X - bar.Width) / 2.0f;
        }

        protected override void OnHostAddon
        (
            AddonEvent type,
            AddonArgs? args
        )
        {
            if (type == AddonEvent.PostSetup)
            {
                if (module.isAppliedOnce || !LookingForGroup->IsAddonAndNodesReady())
                    return;

                module.ApplyPreset(module.config.Last);
                module.isAppliedOnce = true;
            }
        }

        public void ShowContextMenu
        (
            PartyFinderSetting setting
        )
        {
            List<ContextMenuItem> menus =
            [
                new()
                {
                    Name = Lang.GetSe("AutoRecordPartyFinderSetting-ContextMenu-Update"),
                    OnClicked = _ =>
                    {
                        if (!LookingForGroupCondition->IsAddonAndNodesReady()) return;

                        var currentDisplayName = setting.DisplayName;
                        var updated            = module.config.Last.Copy();
                        updated.DisplayName = currentDisplayName;

                        var index = module.config.Slot.IndexOf(setting);

                        if (index == -1) return;

                        module.config.Slot[index] = updated;
                        module.config.Save(module);
                        RefreshPresetList();
                    }
                },
                new()
                {
                    Name = Lang.GetSe("AutoRecordPartyFinderSetting-ContextMenu-Delete"),
                    OnClicked = _ =>
                    {
                        module.config.Slot.Remove(setting);
                        module.config.Save(module);

                        var newTotalPages = Math.Max(1, (int)Math.Ceiling(module.config.Slot.Count / 10.0));
                        if (CurrentPageIndex >= newTotalPages)
                            CurrentPageIndex = newTotalPages - 1;

                        RefreshPresetList();
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
    }

    private class PresetRowNode : HorizontalListNode
    {
        public PartyFinderSetting Setting { get; set; } = null!;

        private readonly TextButtonNode titleButton;

        public PresetRowNode
        (
            AutoRecordPartyFinderSetting      module,
            AutoRecordPartyFinderSettingAddon addon
        )
        {
            Size = addon.ContentSize with { Y = 28f };

            titleButton = new TextButtonNode
            {
                Size   = addon.ContentSize with { Y = 28f },
                String = string.Empty
            };
            titleButton.LabelNode.TextFlags |= TextFlags.Ellipsis;

            AddNode(titleButton);

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

        public void Update
        (
            PartyFinderSetting setting
        )
        {
            Setting = setting;

            var description = new ReadOnlySeString(setting.DescriptionBytes ?? []);
            var title = string.IsNullOrEmpty(setting.DisplayName) ?
                            Lang.Get("None") :
                            setting.DisplayName;

            titleButton.String = description;

            var tooltipText = Lang.GetSe
            (
                "AutoRecordPartyFinderSetting-Message",
                new Dictionary<string, object>
                {
                    ["contentName"] = title,
                    ["description"] = description,
                }
            );
            titleButton.TextTooltip = tooltipText;
        }
    }
}
