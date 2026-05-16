using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.KamiToolKit.Addons;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Internal;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using KamiToolKit.Premade.Node.ListItem;
using KamiToolKit.Premade.Node.Simple;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading.TaskHelper;
using Action = System.Action;

namespace DailyRoutines.ModulesPublic;

public unsafe class QuickChatPanel : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("QuickChatPanelTitle"),
        Description = Lang.Get("QuickChatPanelDescription"),
        Category    = ModuleCategory.UIOptimization
    };

    private Config config = null!;

    private int dropMacroIndex   = -1;
    private int dropMessageIndex = -1;

    private TextButtonNode?      sendButton;
    private QuickChatPanelAddon? chatPanelAddon;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        TaskHelper ??= new() { TimeoutMS = 5_000 };

        if (config.SoundEffectNotes.Count <= 0)
        {
            for (var i = 1U; i < 17; i++)
                config.SoundEffectNotes[i] = $"<se.{i}>";
        }

        chatPanelAddon = new(this)
        {
            InternalName          = "DRQuickChatPanel",
            Title                 = Info.Title,
            Size                  = new(542f, 400f),
            RememberClosePosition = false,
            DisableClose          = true
        };

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "ChatLog", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "ChatLog", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "ChatLog", OnAddon);
    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
        OnAddon(AddonEvent.PreFinalize, null);

        chatPanelAddon?.Dispose();
        chatPanelAddon = null;

        // 恢复
        if (ChatLog != null)
        {
            var textInputNode = ChatLog->GetComponentNodeById(5);
            if (textInputNode == null) return;

            var inputBackground = textInputNode->Component->UldManager.SearchNodeById(17);
            if (inputBackground == null) return;

            var textInputDisplayNode = textInputNode->Component->UldManager.SearchNodeById(16);
            if (textInputDisplayNode == null) return;

            var windowNode = ChatLog->RootNode;
            if (windowNode == null) return;

            var width = (ushort)(windowNode->Width - 38);
            inputBackground->SetWidth(width);
            textInputDisplayNode->SetWidth(width);
            textInputNode->SetWidth(width);
        }
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("QuickChatPanel-Messages"));

        using (ImRaii.PushIndent())
            DrawMessageOrder();
        
        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("QuickChatPanel-Macro"));

        using (ImRaii.PushIndent())
            DrawMacroOrder();
        
        return;

        void DrawMessageOrder()
        {
            if (config.SavedMessages.Count == 0)
            {
                ImGui.TextDisabled(Lang.Get("QuickChatPanel-SavedMessagesAmountText", 0));
                return;
            }

            for (var i = 0; i < config.SavedMessages.Count; i++)
            {
                var message = config.SavedMessages[i];
                ImGui.Button($"{i + 1}. {message}##QuickChatPanelMessageOrder{i}", new(320f * GlobalUIScale, 0f));

                using (var source = ImRaii.DragDropSource())
                {
                    if (source)
                    {
                        if (ImGui.SetDragDropPayload("QuickChatPanelMessageReorder", []))
                            dropMessageIndex = i;

                        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), message);
                    }
                }

                using var target = ImRaii.DragDropTarget();
                if (!target) continue;

                ImGui.AcceptDragDropPayload("QuickChatPanelMessageReorder");
                if (!ImGui.IsMouseReleased(ImGuiMouseButton.Left) || dropMessageIndex < 0) continue;

                (config.SavedMessages[dropMessageIndex], config.SavedMessages[i]) =
                    (config.SavedMessages[i], config.SavedMessages[dropMessageIndex]);
                dropMessageIndex = -1;
                config.Save(this);
                chatPanelAddon?.RequestRebuild();
            }
        }
        
        void DrawMacroOrder()
        {
            if (config.SavedMacros.Count == 0)
            {
                ImGui.TextDisabled(Lang.Get("QuickChatPanel-SavedMacrosAmountText", 0));
                return;
            }

            for (var i = 0; i < config.SavedMacros.Count; i++)
            {
                var macro = config.SavedMacros[i];
                ImGui.Button($"{i + 1}. {macro.Name}##QuickChatPanelMacroOrder{i}", new(320f * GlobalUIScale, 0f));

                using (var source = ImRaii.DragDropSource())
                {
                    if (source)
                    {
                        if (ImGui.SetDragDropPayload("QuickChatPanelMacroReorder", []))
                            dropMacroIndex = i;

                        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), macro.Name);
                    }
                }

                using var target = ImRaii.DragDropTarget();
                if (!target) continue;

                ImGui.AcceptDragDropPayload("QuickChatPanelMacroReorder");
                if (!ImGui.IsMouseReleased(ImGuiMouseButton.Left) || dropMacroIndex < 0) continue;

                (config.SavedMacros[dropMacroIndex], config.SavedMacros[i]) =
                    (config.SavedMacros[i], config.SavedMacros[dropMacroIndex]);
                dropMacroIndex = -1;
                config.Save(this);
                chatPanelAddon?.RequestRebuild();
            }
        }
    }
    
    private void OnAddon(AddonEvent type, AddonArgs? args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
            case AddonEvent.PostDraw:
                if (ChatLog == null) return;

                var textInputNode = ChatLog->GetComponentNodeById(5);
                if (textInputNode == null) return;

                var inputBackground = textInputNode->Component->UldManager.SearchNodeById(17);
                if (inputBackground == null) return;

                var textInputDisplayNode = textInputNode->Component->UldManager.SearchNodeById(16);
                if (textInputDisplayNode == null) return;

                var windowNode = ChatLog->RootNode;
                if (windowNode == null) return;

                const float OFFSET = 40f;

                if (sendButton == null)
                {
                    sendButton = new()
                    {
                        Size        = new(64, textInputNode->Height + 4),
                        IsVisible   = true,
                        IsEnabled   = true,
                        String      = Lang.Get("Send"),
                        TextTooltip = Info.Title

                    };

                    sendButton.AddEvent
                    (
                        AtkEventType.MouseClick,
                        (_, _, _, _, data) =>
                        {
                            if (data->IsRightClick)
                                chatPanelAddon.TogglePanel();
                            else if (data->IsLeftClick)
                            {
                                if (!SendChatboxMessage())
                                    chatPanelAddon.TogglePanel();
                            }
                        }
                    );

                    sendButton.Position = new Vector2(windowNode->Width - sendButton.Width - OFFSET, 0);
                    sendButton.LabelNode.AutoAdjustTextSize();

                    sendButton?.AttachNode(textInputNode);
                }

                inputBackground->SetWidth((ushort)(windowNode->Width      - sendButton.Width - OFFSET));
                textInputDisplayNode->SetWidth((ushort)(windowNode->Width - sendButton.Width - OFFSET));
                textInputNode->SetWidth((ushort)(windowNode->Width        - sendButton.Width - OFFSET));

                sendButton.X      = windowNode->Width - sendButton.Width - OFFSET;
                sendButton.Size   = sendButton.Size with { Y = textInputNode->Height + 4 };
                sendButton.String = Lang.Get(IsAnyTextInBlock() ? "Send" : "Open");

                break;
            case AddonEvent.PreFinalize:
                sendButton?.Dispose();
                sendButton = null;
                break;
        }
    }

    private static bool IsAnyTextInBlock()
    {
        if (ChatLog == null || !ChatLog->IsAddonAndNodesReady()) return false;

        var inputNode = (AtkComponentNode*)ChatLog->GetNodeById(5);
        if (inputNode == null) return false;

        var textNode = inputNode->Component->UldManager.SearchNodeById(16)->GetAsAtkTextNode();
        if (textNode == null) return false;

        var text = textNode->NodeText.ToString();
        if (string.IsNullOrWhiteSpace(text)) return false;

        return true;
    }

    private bool SendChatboxMessage()
    {
        if (ChatLog == null || !ChatLog->IsAddonAndNodesReady()) return false;

        var inputNode = (AtkComponentNode*)ChatLog->GetNodeById(5);
        if (inputNode == null) return false;

        var textNode = inputNode->Component->UldManager.SearchNodeById(16)->GetAsAtkTextNode();
        if (textNode == null) return false;

        var text = new ReadOnlySeString(textNode->NodeText);
        if (string.IsNullOrWhiteSpace(text.ToString())) return false;
        ChatManager.Instance().SendMessage(text);

        var inputComponent = (AtkComponentTextInput*)inputNode->Component;
        inputComponent->EvaluatedString.Clear();
        inputComponent->RawString.Clear();
        inputComponent->AvailableLines.Clear();
        inputComponent->HighlightedAutoTranslateOptionColorPrefix.Clear();
        inputComponent->HighlightedAutoTranslateOptionColorSuffix.Clear();
        textNode->NodeText.Clear();

        chatPanelAddon?.Close();
        return true;
    }

    private static void CopyText(string text)
    {
        ImGui.SetClipboardText(text);
        NotifyHelper.Instance().NotificationSuccess($"{Lang.Get("CopiedToClipboard")}: {text}");
    }

    private void SendSavedMessage(string message)
    {
        ChatManager.Instance().SendMessage(message);
        chatPanelAddon?.Close();
    }

    private void ExecuteMacro(SavedMacro macro)
    {
        var gameMacro = RaptureMacroModule.Instance()->GetMacro(macro.Category, (uint)macro.Position);

        RaptureShellModule.Instance()->ExecuteMacro(gameMacro);
        chatPanelAddon?.Close();
    }

    private static void SendGameItemLink(Item item)
    {
        // TODO: 改成 ReadOnlyString, 这个等 API 16
        NotifyHelper.Instance().Chat
        (
            new SeStringBuilder()
                .AddItemLink(item.RowId, PluginConfig.Instance().ConflictKeyBinding.IsPressed())
                .Build()
                .Encode()
        );
    }

    private class Config : ModuleConfig
    {
        public MacroDisplayMode         OverlayMacroDisplayMode = MacroDisplayMode.Buttons;
        public Vector2                  OverlayOffset           = new(0);
        public List<SavedMacro>         SavedMacros             = [];
        public List<string>             SavedMessages           = [];
        public Dictionary<uint, string> SoundEffectNotes        = [];
    }

    private class QuickChatPanelAddon : AttachedAddon
    {
        private readonly Dictionary<QuickChatTab, ListButtonNode> tabButtons = [];
        private readonly LuminaSearcher<Item>                     searcher;
        private readonly QuickChatPanel                           instance;
        private readonly TaskHelper                               TaskHelper = new();

        private QuickChatTab         selectedTab;
        private ScrollingListNode?   contentList;
        private SimpleComponentNode? contentPanel;
        private SimpleNineGridNode?  contentPanelBackground;

        private string itemSearchInput  = string.Empty;
        private bool   rebuildRequested = true;

        public QuickChatPanelAddon(QuickChatPanel instance) : base("ChatLog")
        {
            this.instance = instance;
            searcher = new
            (
                LuminaGetter.Get<Item>(),
                [
                    x => x.Name.ToString(),
                    x => x.Description.ToString(),
                    x => x.RowId.ToString()
                ],
                resultLimit: 50
            );
        }

        protected override AttachedAddonPosition AttachPosition =>
            AttachedAddonPosition.RightBottom;

        protected override Vector2 PositionOffset =>
            instance.config.OverlayOffset + new Vector2(0, -28f);

        protected override bool CanOpenAddon =>
            false;

        public void TogglePanel()
        {
            if (IsOpen)
            {
                Close();
                return;
            }

            Open();
        }

        public void RequestRebuild() =>
            rebuildRequested = true;

        protected override bool CanCloseHostAddon(AtkUnitBase* hostAddon) =>
            false;

        protected override void OnAttachedAddonUpdate(AtkUnitBase* addon, AtkUnitBase* hostAddon)
        {
            if (DService.Instance().KeyState[VirtualKey.ESCAPE])
            {
                Close();
                return;
            }

            if (rebuildRequested)
            {
                rebuildRequested = false;
                RefreshCurrentTab();
            }
        }

        protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
        {
            tabButtons.Clear();

            var body = new HorizontalListNode
            {
                IsVisible   = true,
                Position    = ContentStartPosition,
                Size        = ContentSize,
                ItemSpacing = 8f,
                FitHeight   = true
            };

            var nav = new VerticalListNode
            {
                IsVisible   = true,
                Size        = new(108f, body.Height),
                ItemSpacing = 5f,
                FitWidth    = true
            };
            nav.AddNode(CreateNavButton(QuickChatTab.Messages,         Lang.Get("QuickChatPanel-Messages")));
            nav.AddNode(CreateNavButton(QuickChatTab.Macros,           Lang.Get("QuickChatPanel-Macro")));
            nav.AddNode(CreateNavButton(QuickChatTab.SystemSounds,     Lang.Get("QuickChatPanel-SystemSound")));
            nav.AddNode(CreateNavButton(QuickChatTab.GameItems,        Lang.Get("QuickChatPanel-GameItems")));
            nav.AddNode(CreateNavButton(QuickChatTab.SpecialIconChars, Lang.Get("QuickChatPanel-SpecialIconChar")));

            nav.AddDummy(8f);
            nav.AddNode(CreateNavButton(QuickChatTab.Settings, Lang.Get("Settings")));

            var contentSize = new Vector2(ContentSize.X - nav.Width - 16f, body.Height - 16f);
            
            contentPanel = new SimpleComponentNode
            {
                IsVisible = true,
                Size      = contentSize
            };

            contentPanelBackground = new SimpleNineGridNode
            {
                IsVisible          = true,
                TexturePath        = "ui/uld/ToolTipS.tex",
                TextureCoordinates = Vector2.Zero,
                TextureSize        = new(32f, 24f),
                TopOffset          = 10f,
                BottomOffset       = 10f,
                LeftOffset         = 12f,
                RightOffset        = 12f,
                Size               = contentSize + new Vector2(12f),
                Alpha              = 0.42f
            };
            contentPanelBackground.AttachNode(contentPanel);

            contentList = new()
            {
                IsVisible         = true,
                Position          = new(9f),
                Size              = contentSize - new Vector2(8f, 12f),
                ItemSpacing       = 5f,
                FitWidth          = true,
                AutoHideScrollBar = true,
                ScrollSpeed       = 36
            };
            contentList.AttachNode(contentPanel);
            
            body.AddNode(nav);
            body.AddNode(contentPanel);

            RefreshTabButtons();
            
            body.AttachNode(this);

            RefreshCurrentTab();
        }
        
        public override void Dispose()
        {
            TaskHelper.Dispose();
            base.Dispose();
        }

        private void SelectTab(QuickChatTab tab)
        {
            if (selectedTab == tab) return;

            selectedTab = tab;
            RefreshTabButtons();
            RefreshCurrentTab();
        }

        private void RefreshTabButtons()
        {
            foreach (var (tab, button) in tabButtons)
                button.Selected = tab == selectedTab;
        }

        private void RefreshCurrentTab()
        {
            TaskHelper.Enqueue(() =>
            {
                contentList.Clear();

                switch (selectedTab)
                {
                    case QuickChatTab.Messages:
                        BuildMessagesTab();
                        break;
                    case QuickChatTab.Macros:
                        BuildMacrosTab();
                        break;
                    case QuickChatTab.SystemSounds:
                        BuildSystemSoundsTab();
                        break;
                    case QuickChatTab.GameItems:
                        BuildGameItemsTab();
                        break;
                    case QuickChatTab.SpecialIconChars:
                        BuildSpecialIconCharsTab();
                        break;
                    case QuickChatTab.Settings:
                        BuildSettingsTab();
                        break;
                }

                contentList.RecalculateLayout();
            });
        }

        private ListButtonNode CreateNavButton(QuickChatTab tab, string text)
        {
            var button = new ListButtonNode
            {
                IsVisible   = true,
                IsEnabled   = true,
                Size        = new(116f, 34f),
                String      = text,
                TextTooltip = text,
                OnClick     = () => SelectTab(tab)
            };

            button.LabelNode.FontSize = 15;
            button.LabelNode.AutoAdjustTextSize();
            tabButtons[tab] = button;
            return button;
        }

        private void BuildMessagesTab()
        {
            if (instance.config.SavedMessages.Count == 0)
            {
                AddEmptyState(Lang.Get("QuickChatPanel-SavedMessagesAmountText", 0), Lang.Get("QuickChatPanel-SendMessageHelp"));
                return;
            }

            foreach (var message in instance.config.SavedMessages)
            {
                contentList?.AddNode
                (
                    CreateTextActionRow
                    (
                        message,
                        Lang.Get("QuickChatPanel-SendMessageHelp"),
                        () => CopyText(message),
                        (_, _, _, _, data) =>
                        {
                            if (data->IsRightClick) instance.SendSavedMessage(message);
                            else if (data->IsLeftClick)
                                CopyText(message);
                        }
                    )
                );
            }
        }

        private void BuildMacrosTab()
        {
            if (instance.config.SavedMacros.Count == 0)
            {
                AddEmptyState(Lang.Get("QuickChatPanel-SavedMacrosAmountText", 0), Lang.Get("QuickChatPanelTitle-DragHelp"));
                return;
            }

            if (instance.config.OverlayMacroDisplayMode == MacroDisplayMode.List)
                BuildMacroList();
            else
                BuildMacroButtonGrid();
        }

        private void BuildMacroList()
        {
            foreach (var macro in instance.config.SavedMacros)
            {
                if (string.IsNullOrWhiteSpace(macro.Name)) continue;

                contentList?.AddNode
                (
                    CreateMacroListButton
                    (
                        macro.IconID,
                        macro.Name,
                        (_, _, _, _, _) => instance.ExecuteMacro(macro)
                    )
                );
            }

            return;

            TextButtonNode CreateMacroListButton
            (
                uint                                     iconID,
                string                                   title,
                AtkEventListener.Delegates.ReceiveEvent? onMouseClick = null
            )
            {
                var button = new TextButtonNode
                {
                    IsVisible   = true,
                    IsEnabled   = true,
                    Size        = new(contentList.ContentWidth, 52f),
                    String      = string.Empty,
                    TextTooltip = title
                };

                button.LabelNode.IsVisible = false;

                var icon = new IconImageNode
                {
                    IsVisible   = true,
                    Size        = new(28f),
                    TextureSize = new(28f),
                    IconId      = iconID,
                    FitTexture  = true
                };

                icon.Position = new(12f, (button.Size.Y - icon.Size.Y) / 2 - 2f);
                icon.AttachNode(button);

                var text = new TextNode
                {
                    IsVisible     = true,
                    Position      = new(icon.Position.X + icon.Size.X + 6f, 0f),
                    String        = title,
                    AlignmentType = AlignmentType.Left,
                    FontSize      = 14,
                    TextFlags     = TextFlags.Bold
                };

                text.Size = new(button.Size.X - text.Position.X, 46f);
                text.AttachNode(button);

                if (onMouseClick != null)
                    button.AddEvent(AtkEventType.MouseClick, onMouseClick);

                return button;
            }
        }

        private void BuildMacroButtonGrid()
        {
            var row          = CreateCardRow();
            var currentWidth = 0f;

            foreach (var macro in instance.config.SavedMacros)
            {
                if (string.IsNullOrWhiteSpace(macro.Name)) continue;

                var button = CreateMacroCardButton(macro.IconID, macro.Name, () => instance.ExecuteMacro(macro));

                if (currentWidth + button.Width > contentList.ContentWidth)
                {
                    contentList?.AddNode(row);
                    row          = CreateCardRow();
                    currentWidth = 0f;
                }

                row.AddNode(button);
                currentWidth += button.Width;
            }

            if (row.Nodes.Count > 0)
                contentList?.AddNode(row);

            return;

            TextButtonNode CreateMacroCardButton(uint iconID, string text, Action onClick)
            {
                var button = new TextButtonNode
                {
                    IsVisible   = true,
                    IsEnabled   = true,
                    Size        = new(110f, 110f),
                    String      = string.Empty,
                    TextTooltip = text,
                    OnClick     = onClick
                };

                button.LabelNode.IsVisible = false;

                var icon = new IconImageNode
                {
                    IsVisible  = true,
                    Size       = new(50f),
                    IconId     = iconID,
                    FitTexture = true
                };
                icon.Position = new((button.Size.X - icon.Size.X) / 2, 14f);

                icon.AttachNode(button);

                new TextNode
                {
                    IsVisible     = true,
                    Position      = new(0f, 64f),
                    Size          = new(button.Width, 24f),
                    String        = text,
                    AlignmentType = AlignmentType.Center,
                    FontSize      = 12,
                    TextFlags     = TextFlags.Bold
                }.AttachNode(button);

                return button;
            }
        }

        private void BuildSystemSoundsTab()
        {
            var row          = CreateCompactRow();
            var currentWidth = 0f;

            foreach (var (key, value) in instance.config.SoundEffectNotes.OrderBy(x => x.Key))
            {
                var button = CreateCompactTextButton
                (
                    value,
                    Lang.Get("QuickChatPanel-SystemSoundHelp"),
                    () => UIGlobals.PlayChatSoundEffect(key),
                    (_, _, _, _, data) =>
                    {
                        if (data->MouseData.ButtonId == 1)
                        {
                            ChatManager.Instance().SendMessage($"<se.{key}><se.{key}>");
                            return;
                        }

                        UIGlobals.PlayChatSoundEffect(key);
                    }
                );

                if (currentWidth + button.Width > contentList.ContentWidth)
                {
                    contentList?.AddNode(row);
                    row          = CreateCompactRow();
                    currentWidth = 0f;
                }

                row.AddNode(button);
                currentWidth += button.Width;
            }

            if (row.Nodes.Count > 0)
                contentList?.AddNode(row);
        }

        private void BuildGameItemsTab()
        {
            var listNode = new ListNode<Item, ItemListItemNode>
            {
                IsVisible   = true,
                Size        = new(contentList.ContentWidth, contentList.Size.Y - 48f),
                ItemSpacing = 4f,
                OptionsList = GetGameItemResults(),
                OnItemSelected = item =>
                {
                    if (item.RowId != 0)
                        SendGameItemLink(item);
                }
            };

            var searchBarNode = new TextInputNode
            {
                IsVisible       = true,
                Size            = new(contentList.ContentWidth, 36f),
                String          = itemSearchInput,
                MaxCharacters   = 128,
                OnInputReceived = text => UpdateGameItemList(listNode, text.ToString()),
                OnInputComplete = text => UpdateGameItemList(listNode, text.ToString())
            };

            searchBarNode.CurrentTextNode.FontSize =  14;
            searchBarNode.CurrentTextNode.Position += new Vector2(0, 3);
            contentList?.AddNode(searchBarNode);

            contentList?.AddNode(listNode);

            return;

            List<Item> GetGameItemResults()
            {
                return string.IsNullOrWhiteSpace(itemSearchInput) ? [] : searcher.SearchResult;
            }

            void UpdateGameItemList(ListNode<Item, ItemListItemNode> node, string searchString)
            {
                itemSearchInput = searchString;
                searcher.Search(itemSearchInput);
                node.OptionsList = GetGameItemResults();
                node.ResetScroll();
            }
        }

        private void BuildSpecialIconCharsTab()
        {
            var row          = CreateGlyphRow();
            var currentWidth = 0f;

            foreach (var icon in SeIconChars)
            {
                var text = icon.ToString();

                var button = CreateGlyphButton(text, $"0x{(int)icon:X4}", () => CopyText(text));

                if (currentWidth + button.Width > contentList.ContentWidth)
                {
                    contentList?.AddNode(row);
                    row          = CreateGlyphRow();
                    currentWidth = 0f;
                }

                row.AddNode(button);
                currentWidth += button.Width;
            }

            if (row.Nodes.Count > 0)
                contentList?.AddNode(row);

            return;

            HorizontalListNode CreateGlyphRow()
            {
                return new HorizontalListNode
                {
                    IsVisible          = true,
                    Size               = new(contentList.ContentWidth, 36f),
                    ItemSpacing        = 4f,
                    FirstItemSpacing   = 0f,
                    FitToContentHeight = true
                };
            }

            TextButtonNode CreateGlyphButton(string text, string tooltip, Action onClick)
            {
                var button = new TextButtonNode
                {
                    IsVisible   = true,
                    IsEnabled   = true,
                    Size        = new(48f, 32f),
                    String      = text,
                    TextTooltip = tooltip,
                    OnClick     = onClick
                };

                button.LabelNode.FontSize = 18;
                return button;
            }
        }

        private void BuildSettingsTab()
        {
            var contentWidth = contentList.ContentWidth;

            TreeListNode? tree = null;
            tree = new()
            {
                IsVisible               = true,
                Size                    = new(contentWidth, 0f),
                CategoryVerticalSpacing = 5f
            };
            tree.OnLayoutUpdate = height =>
            {
                if (tree == null) return;

                tree.Height = height;
                contentList.RecalculateLayout();
            };

            var generalOverlay = new VerticalListNode
            {
                IsVisible        = true,
                Size             = new(contentWidth, 0f),
                FitContents      = true,
                FitWidth         = true,
                ItemSpacing      = 5f,
                FirstItemSpacing = 10f
            };

            generalOverlay.AddNode
            (
                new TextNode
                {
                    IsVisible     = true,
                    Size          = new(contentWidth, 22f),
                    String        = Lang.Get("Offset"),
                    FontSize      = 13,
                    AlignmentType = AlignmentType.Left
                }
            );

            var offsetRow = new HorizontalListNode
            {
                IsVisible          = true,
                Size               = new(contentWidth, 34f),
                ItemSpacing        = 6f,
                FirstItemSpacing   = 0f,
                FitToContentHeight = true
            };
            offsetRow.AddNode
            (
                OffsetInput
                (
                    "X",
                    (int)instance.config.OverlayOffset.X,
                    value => instance.config.OverlayOffset = instance.config.OverlayOffset with { X = value }
                )
            );
            offsetRow.AddNode
            (
                OffsetInput
                (
                    "Y",
                    (int)instance.config.OverlayOffset.Y,
                    value => instance.config.OverlayOffset = instance.config.OverlayOffset with { Y = value }
                )
            );
            generalOverlay.AddNode(offsetRow);

            tree.AddCategoryNode
            (
                new TreeListCategoryNode
                {
                    IsVisible = true,
                    Size      = new(contentWidth, 28f),
                    String    = Lang.Get("General")
                }
            );
            tree.CategoryNodes[^1].AddNode(generalOverlay);

            var messagesSection = new VerticalListNode
            {
                IsVisible   = true,
                Size        = new(contentWidth, 0f),
                FitContents = true,
                FitWidth    = true,
                ItemSpacing = 5f
            };

            var messageRow = new HorizontalListNode
            {
                IsVisible          = true,
                Size               = new(contentWidth, 36f),
                ItemSpacing        = 6f,
                FirstItemSpacing   = 0f,
                FitToContentHeight = true
            };

            var messageInputNode = new TextInputNode
            {
                IsVisible     = true,
                Size          = new(contentWidth - 84f, 32f),
                MaxCharacters = 1000
            };
            messageRow.AddNode(messageInputNode);

            var addMessageButton = new TextButtonNode
            {
                IsVisible = true,
                IsEnabled = true,
                Size      = new(78f, 32f),
                String    = Lang.Get("Add"),
                OnClick = () =>
                {
                    var text = messageInputNode.String.ToString();
                    if (string.IsNullOrWhiteSpace(text) ||
                        instance.config.SavedMessages.Contains(text))
                        return;

                    instance.config.SavedMessages.Add(text);
                    instance.config.Save(instance);

                    AtkStage.Instance()->ClearFocus();
                    RequestRebuild();
                }
            };
            addMessageButton.LabelNode.AutoAdjustTextSize();
            messageRow.AddNode(addMessageButton);
            messagesSection.AddNode(messageRow);

            foreach (var message in instance.config.SavedMessages.ToList())
            {
                var row = new HorizontalListNode
                {
                    IsVisible          = true,
                    Size               = new(contentWidth, 34f),
                    ItemSpacing        = 6f,
                    FirstItemSpacing   = 0f,
                    FitToContentHeight = true
                };
                row.AddNode
                (
                    new TextNode
                    {
                        IsVisible     = true,
                        Size          = new(contentWidth - 86f, 30f),
                        String        = message,
                        AlignmentType = AlignmentType.Left,
                        FontSize      = 14
                    }
                );
                var button = new TextButtonNode
                {
                    IsVisible = true,
                    IsEnabled = true,
                    Size      = new(80f, 30f),
                    String    = Lang.Get("Delete"),
                    OnClick = () =>
                    {
                        instance.config.SavedMessages.Remove(message);
                        instance.config.Save(instance);
                        RequestRebuild();
                    }
                };
                button.LabelNode.AutoAdjustTextSize();
                row.AddNode(button);
                messagesSection.AddNode(row);
            }

            tree.AddCategoryNode
            (
                new TreeListCategoryNode
                {
                    IsVisible   = true,
                    Size        = new(contentWidth, 28f),
                    String      = Lang.Get("QuickChatPanel-Messages"),
                    IsCollapsed = true
                }
            );
            tree.CategoryNodes[^1].AddNode(messagesSection);

            var macrosSection = new VerticalListNode
            {
                IsVisible        = true,
                Size             = new(contentWidth, 0f),
                FitContents      = true,
                FitWidth         = true,
                ItemSpacing      = 5f,
                FirstItemSpacing = 10f
            };

            macrosSection.AddNode
            (
                new TextNode
                {
                    IsVisible     = true,
                    Size          = new(contentWidth, 22f),
                    String        = Lang.Get("QuickChatPanel-MacroButton-DisplayType"),
                    FontSize      = 13,
                    AlignmentType = AlignmentType.Left
                }
            );

            var dropdown = new TextDropDownNode
            {
                IsVisible      = true,
                Size           = new(contentWidth, 30f),
                MaxListOptions = MacroDisplayModeLoc.Count,
                Options        = MacroDisplayModeLoc.Values.ToList()
            };
            dropdown.SelectedOption = MacroDisplayModeLoc[instance.config.OverlayMacroDisplayMode];
            dropdown.OnOptionSelected = text =>
            {
                var mode = MacroDisplayModeLoc.FirstOrDefault(x => x.Value == text).Key;
                if (mode == instance.config.OverlayMacroDisplayMode) return;

                instance.config.OverlayMacroDisplayMode = mode;
                instance.config.Save(instance);
                RequestRebuild();
            };
            macrosSection.AddNode(dropdown);

            macrosSection.AddDummy(5f);

            AddMacroSection(true);
            AddMacroSection(false);

            tree.AddCategoryNode
            (
                new TreeListCategoryNode
                {
                    IsVisible   = true,
                    Size        = new(contentWidth, 28f),
                    String      = Lang.Get("QuickChatPanel-Macro"),
                    IsCollapsed = true
                }
            );
            tree.CategoryNodes[^1].AddNode(macrosSection);

            var sounds = new VerticalListNode
            {
                IsVisible   = true,
                Size        = new(contentWidth, 0f),
                FitContents = true,
                FitWidth    = true,
                ItemSpacing = 5f
            };

            foreach (var (key, value) in instance.config.SoundEffectNotes.OrderBy(x => x.Key))
            {
                var row = new HorizontalListNode
                {
                    IsVisible          = true,
                    Size               = new(contentWidth, 34f),
                    ItemSpacing        = 6f,
                    FirstItemSpacing   = 0f,
                    FitToContentHeight = true
                };
                row.AddNode
                (
                    new TextNode
                    {
                        IsVisible     = true,
                        Size          = new(58f, 30f),
                        String        = $"<se.{key}>",
                        AlignmentType = AlignmentType.Left,
                        FontSize      = 14
                    }
                );

                var input = new TextInputNode
                {
                    IsVisible     = true,
                    Size          = new(contentWidth - 64f, 30f),
                    String        = value,
                    MaxCharacters = 32
                };
                input.OnInputReceived          = text => instance.config.SoundEffectNotes[key] = text.ToString();
                input.OnInputComplete          = text => SaveSoundEffectNote(key, text.ToString());
                input.OnFocusLost              = () => SaveSoundEffectNote(key,   input.String.ToString());
                input.CurrentTextNode.FontSize = 14;
                row.AddNode(input);
                sounds.AddNode(row);
            }

            tree.AddCategoryNode
            (
                new TreeListCategoryNode
                {
                    IsVisible   = true,
                    Size        = new(contentWidth, 28f),
                    String      = Lang.Get("QuickChatPanel-SystemSound"),
                    IsCollapsed = true
                }
            );
            tree.CategoryNodes[^1].AddNode(sounds);

            contentList?.AddNode(tree);
            tree.RefreshLayout();
            return;

            void AddMacroSection(bool isIndividual)
            {
                var module = RaptureMacroModule.Instance();

                macrosSection.AddNode
                (
                    new TextNode
                    {
                        IsVisible     = true,
                        Size          = new(contentWidth, 22f),
                        String        = Lang.Get($"QuickChatPanel-{(isIndividual ? "Individual" : "Shared")}Macros"),
                        FontSize      = 13,
                        AlignmentType = AlignmentType.Left
                    }
                );
                var span = isIndividual ? module->Individual : module->Shared;

                for (var i = 0; i < span.Length; i++)
                {
                    var macro = span.GetPointer(i);
                    if (macro == null) continue;

                    var name = macro->Name.ToString();
                    if (string.IsNullOrEmpty(name)) continue;

                    var savedMacro = (*macro).ToSavedMacro();
                    savedMacro.Position = i;
                    savedMacro.Category = isIndividual ? 0U : 1U;
                    macrosSection.AddNode(MacroRow(savedMacro));
                }
            }

            HorizontalListNode MacroRow(SavedMacro macro)
            {
                var isSaved = instance.config.SavedMacros.Contains(macro);
                var row = new HorizontalListNode
                {
                    IsVisible          = true,
                    Size               = new(contentWidth, 42f),
                    ItemSpacing        = 6f,
                    FirstItemSpacing   = 0f,
                    FitToContentHeight = true
                };

                var info = new SimpleComponentNode
                {
                    IsVisible = true,
                    Size      = new(contentWidth - (isSaved ? 172f : 86f), 38f)
                };

                new IconImageNode
                {
                    IsVisible  = true,
                    Position   = new(4f, 4f),
                    Size       = new(28f),
                    IconId     = macro.IconID,
                    FitTexture = true
                }.AttachNode(info);

                new TextNode
                {
                    IsVisible     = true,
                    Position      = new(38f, 0f),
                    Size          = new(info.Width - 42f, 36f),
                    String        = macro.Name,
                    AlignmentType = AlignmentType.Left,
                    FontSize      = 14,
                    TextFlags     = TextFlags.Bold
                }.AttachNode(info);
                row.AddNode(info);

                if (isSaved)
                {
                    var refreshButton = new TextButtonNode
                    {
                        IsVisible   = true,
                        IsEnabled   = true,
                        Size        = new(80f, 34f),
                        String      = Lang.Get("Refresh"),
                        TextTooltip = $"{Lang.Get("QuickChatPanel-LastUpdateTime")}: {instance.config.SavedMacros.Find(x => x.Equals(macro))?.LastUpdateTime}"
                    };
                    refreshButton.OnClick = () =>
                    {
                        var currentIndex = instance.config.SavedMacros.IndexOf(macro);
                        if (currentIndex < 0) return;

                        instance.config.SavedMacros[currentIndex] = macro;
                        instance.config.Save(instance);
                        RequestRebuild();
                    };
                    refreshButton.LabelNode.AutoAdjustTextSize();
                    row.AddNode(refreshButton);
                }

                var toggleButton = new TextButtonNode
                {
                    IsVisible = true,
                    IsEnabled = true,
                    Size      = new(80f, 34f),
                    String    = Lang.Get(isSaved ? "Delete" : "Add")
                };
                toggleButton.OnClick = () =>
                {
                    if (!instance.config.SavedMacros.Remove(macro))
                        instance.config.SavedMacros.Add(macro);

                    instance.config.Save(instance);
                    RequestRebuild();
                };
                toggleButton.LabelNode.AutoAdjustTextSize();
                row.AddNode(toggleButton);
                return row;
            }

            void SaveSoundEffectNote(uint key, string value)
            {
                if (instance.config.SoundEffectNotes.GetValueOrDefault(key) == value) return;

                instance.config.SoundEffectNotes[key] = value;
                instance.config.Save(instance);
                RequestRebuild();
            }

            HorizontalListNode OffsetInput(string label, int value, Action<int> updateValue)
            {
                var row = new HorizontalListNode
                {
                    IsVisible          = true,
                    Size               = new(154f, 32f),
                    ItemSpacing        = 4f,
                    FirstItemSpacing   = 0f,
                    FitToContentHeight = true
                };

                row.AddNode
                (
                    new TextNode
                    {
                        IsVisible     = true,
                        Position      = new(0, 2),
                        Size          = new(18f, 20f),
                        TextFlags     = TextFlags.AutoAdjustNodeSize,
                        String        = label,
                        AlignmentType = AlignmentType.Left,
                        FontSize      = 14
                    }
                );

                var input = new TextInputNode
                {
                    IsVisible = true,
                    Size      = new(132f, 30f),
                    Position  = new(0, -4),
                    String    = value.ToString(),
                    
                };
                input.CurrentTextNode.FontSize = 14;
                input.OnFocusLost = () =>
                {
                    if (!int.TryParse(input.String, out var newValue)) return;

                    updateValue(newValue);
                    instance.config.Save(instance);
                };
                row.AddNode(input);
                return row;
            }
        }

        private void AddEmptyState(string text, string? detail = null)
        {
            var state = new VerticalListNode
            {
                IsVisible   = true,
                Size        = new(contentList.ContentWidth, 72f),
                ItemSpacing = 2f,
                FitWidth    = true
            };

            state.AddNode
            (
                new TextNode
                {
                    IsVisible     = true,
                    Size          = new(contentList.ContentWidth, 30f),
                    String        = text,
                    FontSize      = 16,
                    TextColor     = ColorHelper.GetColor(3),
                    AlignmentType = AlignmentType.Center
                }
            );

            if (!string.IsNullOrWhiteSpace(detail))
            {
                state.AddNode
                (
                    new TextNode
                    {
                        IsVisible     = true,
                        Size          = new(contentList.ContentWidth, 24f),
                        String        = detail,
                        TextColor     = ColorHelper.GetColor(3),
                        AlignmentType = AlignmentType.Center
                    }
                );
            }

            contentList?.AddNode(state);
        }

        private HorizontalListNode CreateCardRow() =>
            new()
            {
                IsVisible          = true,
                Size               = new(contentList.ContentWidth, 82f),
                ItemSpacing        = 8f,
                FirstItemSpacing   = 0f,
                FitToContentHeight = true
            };

        private HorizontalListNode CreateCompactRow() =>
            new()
            {
                IsVisible          = true,
                Size               = new(contentList.ContentWidth, 38f),
                ItemSpacing        = 6f,
                FirstItemSpacing   = 0f,
                FitToContentHeight = true
            };

        private TextButtonNode CreateTextActionRow
        (
            string                                   text,
            string                                   tooltip,
            Action                                   onClick,
            AtkEventListener.Delegates.ReceiveEvent? onMouseClick = null
        )
        {
            var button = new TextButtonNode
            {
                IsVisible   = true,
                IsEnabled   = true,
                Size        = new(contentList.ContentWidth, 32f),
                String      = text,
                TextTooltip = tooltip,
                OnClick     = onClick
            };

            button.LabelNode.AlignmentType = AlignmentType.Left;
            button.LabelNode.FontSize      = 14;

            if (onMouseClick != null)
                button.AddEvent(AtkEventType.MouseClick, onMouseClick);

            return button;
        }

        private static TextButtonNode CreateCompactTextButton
        (
            string                                   text,
            string                                   tooltip,
            Action                                   onClick,
            AtkEventListener.Delegates.ReceiveEvent? onMouseClick = null
        )
        {
            var button = new TextButtonNode
            {
                IsVisible   = true,
                IsEnabled   = true,
                Size        = new(116f, 34f),
                String      = text,
                TextTooltip = tooltip,
                OnClick     = onClick
            };

            button.LabelNode.AutoAdjustTextSize();

            if (onMouseClick != null)
                button.AddEvent(AtkEventType.MouseClick, onMouseClick);

            return button;
        }
    }

    public class SavedMacro : IEquatable<SavedMacro>
    {
        public uint     Category       { get; set; } // 0 - Individual; 1 - Shared
        public int      Position       { get; set; }
        public string   Name           { get; set; } = string.Empty;
        public uint     IconID         { get; set; }
        public DateTime LastUpdateTime { get; set; } = DateTime.MinValue;

        public bool Equals(SavedMacro? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return Category == other.Category && Position == other.Position;
        }

        public override bool Equals(object? obj)
        {
            if (obj is null) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj.GetType() == GetType() && Equals((SavedMacro)obj);
        }

        public override int GetHashCode() => HashCode.Combine(Category, Position);
    }

    private enum MacroDisplayMode
    {
        List,
        Buttons
    }

    private enum QuickChatTab
    {
        Messages,
        Macros,
        SystemSounds,
        GameItems,
        SpecialIconChars,
        Settings
    }

    #region 常量

    private static readonly FrozenDictionary<MacroDisplayMode, string> MacroDisplayModeLoc = new Dictionary<MacroDisplayMode, string>
    {
        [MacroDisplayMode.List]    = Lang.Get("QuickChatPanel-List"),
        [MacroDisplayMode.Buttons] = Lang.Get("QuickChatPanel-Buttons")
    }.ToFrozenDictionary();

    private static readonly char[] SeIconChars = Enum.GetValues<SeIconChar>().Select(x => (char)x).ToArray();

    #endregion
}

static file class QuickChatPanelExtensions
{
    public static QuickChatPanel.SavedMacro ToSavedMacro(this RaptureMacroModule.Macro macro)
    {
        var savedMacro = new QuickChatPanel.SavedMacro
        {
            Name           = macro.Name.ToString(),
            IconID         = macro.IconId,
            LastUpdateTime = StandardTimeManager.Instance().Now
        };

        return savedMacro;
    }
}
