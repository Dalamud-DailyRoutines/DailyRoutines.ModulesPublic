using System.Numerics;
using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OmenTools.Interop.Game.Lumina;
using OmenTools.KamiToolKit.Addons;
using OmenTools.KamiToolKit.Nodes;
using OmenTools.OmenService;
using OmenTools.Threading.TaskHelper;
using Action = System.Action;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe class QuickChatPanel : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("QuickChatPanelTitle"),
        Description = Lang.Get("QuickChatPanelDescription"),
        Category    = ModuleCategory.Interface
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
            InternalName = "DRQuickChatPanel",
            Title        = Info.Title,
            Size         = new(600f, 400f)
        };

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup,   "ChatLog", OnAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostDraw,    "ChatLog", OnAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize, "ChatLog", OnAddon);
    }

    protected override void Uninit()
    {
        IAddonLifecycle.Instance().UnregisterListener(OnAddon);

        sendButton?.Dispose();
        sendButton = null;

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
                ImGui.TextDisabled
                (
                    Lang.Get
                    (
                        "QuickChatPanel-SavedMessagesAmountText",
                        new Dictionary<string, object>
                        {
                            ["count"] = 0
                        }
                    )
                );
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
            }
        }

        void DrawMacroOrder()
        {
            if (config.SavedMacros.Count == 0)
            {
                ImGui.TextDisabled
                (
                    Lang.Get
                    (
                        "QuickChatPanel-SavedMacrosAmountText",
                        new Dictionary<string, object>
                        {
                            ["count"] = 0
                        }
                    )
                );
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
            }
        }
    }

    private void OnAddon
    (
        AddonEvent type,
        AddonArgs? args
    )
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

                sendButton.X    = windowNode->Width - sendButton.Width - OFFSET;
                sendButton.Size = sendButton.Size with { Y = textInputNode->Height + 4 };
                sendButton.String = Lang.Get
                (
                    IsAnyTextInBlock() ?
                        "Send" :
                        "Open"
                );

                break;
            case AddonEvent.PreFinalize:
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
        return !string.IsNullOrWhiteSpace(text);
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

    private static void CopyText
    (
        string text
    )
    {
        ImGui.SetClipboardText(text);
        NotifyHelper.Instance().NotificationSuccess($"{Lang.Get("CopiedToClipboard")}: {text}");
    }

    private void SendSavedMessage
    (
        string message
    )
    {
        ChatManager.Instance().SendMessage(message);
        chatPanelAddon?.Close();
    }

    private void ExecuteMacro
    (
        SavedMacro macro
    )
    {
        var gameMacro = RaptureMacroModule.Instance()->GetMacro(macro.Category, (uint)macro.Position);

        RaptureShellModule.Instance()->ExecuteMacro(gameMacro);
        chatPanelAddon?.Close();
    }

    private class Config : ModuleConfig
    {
        public MacroDisplayMode         OverlayMacroDisplayMode = MacroDisplayMode.Buttons;
        public Vector2                  OverlayOffset           = new(0);
        public QuickChatTab             SelectedTab             = QuickChatTab.Messages;
        public List<SavedMacro>         SavedMacros             = [];
        public List<string>             SavedMessages           = [];
        public Dictionary<uint, string> SoundEffectNotes        = [];
    }

    private class QuickChatPanelAddon : AttachedAddon
    {
        protected override AttachedAddonPosition AttachPosition =>
            AttachedAddonPosition.RightBottom;

        protected override Vector2 PositionOffset =>
            instance.config.OverlayOffset + new Vector2(0, -34f);

        protected override bool AutoOpenAddon =>
            false;

        private readonly Dictionary<QuickChatTab, ListButtonNode>                  tabButtons      = [];
        private readonly Dictionary<QuickChatTab, ResNode>                         tabRoots        = [];
        private readonly Dictionary<QuickChatTab, ScrollingNode<VerticalListNode>> tabContentLists = [];

        private readonly LuminaSearcher<Item> searcher;
        private readonly QuickChatPanel       instance;
        private readonly TaskHelper           taskHelper = new();

        private QuickChatTab         selectedTab;
        private SimpleComponentNode? contentPanel;
        private SimpleNineGridNode?  contentPanelBackground;
        private Vector2              tabContentSize;

        private string itemSearchInput = string.Empty;

        private float NavWidth =>
            ContentSize.X * NAV_WIDTH_RATIO;

        private float RowHeight =>
            ContentSize.Y / VISIBLE_ROWS;

        private float Gap =>
            RowHeight * GAP_RATIO;

        public QuickChatPanelAddon
        (
            QuickChatPanel instance
        ) : base("ChatLog")
        {
            this.instance = instance;
            selectedTab = Enum.IsDefined(instance.config.SelectedTab) ?
                              instance.config.SelectedTab :
                              QuickChatTab.Messages;
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

        public override void Dispose()
        {
            taskHelper.Dispose();
            base.Dispose();
        }

        protected override void OnUpdate
        (
            AtkUnitBase* addon
        )
        {
            if (IKeyState.Instance()[VirtualKey.ESCAPE])
            {
                Close();
                if (SystemMenu->IsAddonAndNodesReady())
                    SystemMenu->Close(true);
            }

            base.OnUpdate(addon);
        }

        protected override void OnSetup
        (
            AtkUnitBase*   addon,
            Span<AtkValue> atkValues
        )
        {
            tabButtons.Clear();
            tabRoots.Clear();
            tabContentLists.Clear();
            itemSearchInput = string.Empty;

            var gap      = Gap;
            var navWidth = NavWidth;

            var body = new HorizontalListNode
            {
                Position    = ContentStartPosition,
                Size        = ContentSize,
                ItemSpacing = gap,
                FitHeight   = true
            };

            var nav = new VerticalListNode
            {
                Size        = new(navWidth, body.Height),
                ItemSpacing = gap,
                FitWidth    = true
            };
            nav.AddNode(CreateNavButton(QuickChatTab.Messages,         Lang.Get("QuickChatPanel-Messages")));
            nav.AddNode(CreateNavButton(QuickChatTab.Macros,           Lang.Get("QuickChatPanel-Macro")));
            nav.AddNode(CreateNavButton(QuickChatTab.SystemSounds,     Lang.Get("QuickChatPanel-SystemSound")));
            nav.AddNode(CreateNavButton(QuickChatTab.GameItems,        Lang.Get("QuickChatPanel-GameItems")));
            nav.AddNode(CreateNavButton(QuickChatTab.SpecialIconChars, Lang.Get("QuickChatPanel-SpecialIconChar")));

            nav.AddDummy(gap);
            nav.AddNode(CreateNavButton(QuickChatTab.Settings, Lang.Get("Settings")));

            var contentSize = new Vector2(ContentSize.X - navWidth - gap, ContentSize.Y);
            tabContentSize = contentSize;

            contentPanel = new SimpleComponentNode
            {
                Size = contentSize
            };

            contentPanelBackground = new SimpleNineGridNode
            {
                TexturePath        = "ui/uld/ToolTipS.tex",
                TextureCoordinates = Vector2.Zero,
                TextureSize        = new(32f, 24f),
                TopOffset          = 10f,
                BottomOffset       = 10f,
                LeftOffset         = 12f,
                RightOffset        = 12f,
                Size               = contentSize + new Vector2(gap),
                Alpha              = 0.42f
            };
            contentPanelBackground.AttachNode(contentPanel);

            body.AddNode(nav);
            body.AddNode(contentPanel);

            RefreshTabButtons();

            body.AttachNode(this);

            ShowTab(selectedTab);
        }

        public void TogglePanel()
        {
            if (IsRequestedOpen)
            {
                Close();
                return;
            }

            Open();
        }

        private void SelectTab
        (
            QuickChatTab tab
        )
        {
            if (selectedTab == tab) return;

            selectedTab                 = tab;
            instance.config.SelectedTab = tab;
            instance.config.Save(instance);
            RefreshTabButtons();
            ShowTab(tab);
        }

        private void RefreshTabButtons()
        {
            foreach (var (tab, button) in tabButtons)
                button.Selected = tab == selectedTab;
        }

        private void EnsureTabBuilt
        (
            QuickChatTab tab
        )
        {
            if (tabRoots.ContainsKey(tab)) return;
            if (contentPanel == null) return;

            var gap = Gap;

            var tabRoot = new ResNode
            {
                Size = tabContentSize
            };
            tabRoot.AttachNode(contentPanel);

            var contentList = new ScrollingNode<VerticalListNode>
            {
                Position          = new(gap),
                Size              = tabContentSize - new Vector2(gap * 2f),
                ScrollSpeed       = (int)RowHeight,
                AutoHideScrollBar = true
            };
            contentList.ContentNode.ItemSpacing = gap;
            contentList.ContentNode.FitWidth    = true;
            contentList.ContentNode.FitContents = true;
            contentList.AttachNode(tabRoot);

            tabRoots[tab]        = tabRoot;
            tabContentLists[tab] = contentList;

            BuildTab(tab, contentList);
            contentList.RecalculateSizes();
        }

        private void BuildTab
        (
            QuickChatTab                    tab,
            ScrollingNode<VerticalListNode> contentList
        )
        {
            switch (tab)
            {
                case QuickChatTab.Messages:
                    BuildMessagesTab(contentList);
                    break;
                case QuickChatTab.Macros:
                    BuildMacrosTab(contentList);
                    break;
                case QuickChatTab.SystemSounds:
                    BuildSystemSoundsTab(contentList);
                    break;
                case QuickChatTab.GameItems:
                    BuildGameItemsTab(contentList);
                    break;
                case QuickChatTab.SpecialIconChars:
                    BuildSpecialIconCharsTab(contentList);
                    break;
                case QuickChatTab.Settings:
                    BuildSettingsTab(contentList);
                    break;
            }
        }

        private void ShowTab
        (
            QuickChatTab tab
        ) =>
            taskHelper.Enqueue
            (() =>
                {
                    EnsureTabBuilt(tab);

                    foreach (var (otherTab, root) in tabRoots)
                        root.IsVisible = otherTab == tab;
                }
            );

        private ListButtonNode CreateNavButton
        (
            QuickChatTab tab,
            string       text
        )
        {
            var button = new ListButtonNode
            {
                Size        = new(NavWidth, RowHeight),
                String      = text,
                TextTooltip = text,
                OnClick     = () => SelectTab(tab)
            };

            button.LabelNode.FontSize = 15;
            button.LabelNode.AutoAdjustTextSize();
            tabButtons[tab] = button;
            return button;
        }

        private void BuildMessagesTab
        (
            ScrollingNode<VerticalListNode> contentList
        )
        {
            if (instance.config.SavedMessages.Count == 0)
            {
                AddEmptyState
                (
                    contentList,
                    Lang.Get
                    (
                        "QuickChatPanel-SavedMessagesAmountText",
                        new Dictionary<string, object>
                        {
                            ["count"] = 0
                        }
                    ),
                    Lang.Get("QuickChatPanel-SendMessageHelp")
                );
                return;
            }

            foreach (var message in instance.config.SavedMessages)
            {
                contentList.ContentNode.AddNode
                (
                    CreateTextActionRow
                    (
                        contentList,
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

        private void BuildMacrosTab
        (
            ScrollingNode<VerticalListNode> contentList
        )
        {
            if (instance.config.SavedMacros.Count == 0)
            {
                AddEmptyState
                (
                    contentList,
                    Lang.Get
                    (
                        "QuickChatPanel-SavedMacrosAmountText",
                        new Dictionary<string, object>
                        {
                            ["count"] = 0
                        }
                    )
                );
                return;
            }

            if (instance.config.OverlayMacroDisplayMode == MacroDisplayMode.List)
                BuildMacroList(contentList);
            else
                BuildMacroButtonGrid(contentList);
        }

        private void BuildMacroList
        (
            ScrollingNode<VerticalListNode> contentList
        )
        {
            var rowWidth  = contentList.ContentNode.Width;
            var gap       = Gap;
            var rowHeight = RowHeight * 1.4f;
            var iconSize  = RowHeight * 0.75f;

            foreach (var macro in instance.config.SavedMacros)
            {
                if (string.IsNullOrWhiteSpace(macro.Name)) continue;

                contentList.ContentNode.AddNode
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
                    Size        = new(rowWidth, rowHeight),
                    String      = string.Empty,
                    TextTooltip = title
                };

                button.LabelNode.IsVisible = false;

                var icon = new IconImageNode
                {
                    Size        = new(iconSize),
                    TextureSize = new(iconSize),
                    IconId      = iconID,
                    FitTexture  = true
                };

                icon.Position = new(gap, (button.Size.Y - icon.Size.Y) / 2f);
                icon.AttachNode(button);

                var text = new TextNode
                {
                    Position      = new(icon.Position.X + icon.Size.X + gap, 0f),
                    String        = title,
                    AlignmentType = AlignmentType.Left,
                    FontSize      = 14,
                    TextFlags     = TextFlags.Bold
                };

                text.Size = new(button.Size.X - text.Position.X - gap, button.Size.Y - gap);
                text.AttachNode(button);

                if (onMouseClick != null)
                    button.AddEvent(AtkEventType.MouseClick, onMouseClick);

                return button;
            }
        }

        private void BuildMacroButtonGrid
        (
            ScrollingNode<VerticalListNode> contentList
        )
        {
            var rowWidth = contentList.ContentNode.Width;

            var (columns, cardSize) = GetGridLayout(rowWidth, Gap, RowHeight * 3f);

            var macros = instance.config.SavedMacros.Where(x => !string.IsNullOrWhiteSpace(x.Name)).ToList();

            foreach (var chunk in macros.Chunk(columns))
            {
                var row = CreateGridRow(rowWidth, cardSize);

                foreach (var macro in chunk)
                    row.AddNode(CreateMacroCardButton(macro, cardSize));

                contentList.ContentNode.AddNode(row);
            }

            return;

            TextButtonNode CreateMacroCardButton
            (
                SavedMacro macro,
                float      size
            )
            {
                var button = new TextButtonNode
                {
                    Size        = new(size),
                    String      = string.Empty,
                    TextTooltip = macro.Name,
                    OnClick     = () => instance.ExecuteMacro(macro)
                };

                button.LabelNode.IsVisible = false;

                var icon = new IconImageNode
                {
                    Size       = new(size * 0.45f),
                    IconId     = macro.IconID,
                    FitTexture = true
                };
                icon.Position = new((size - icon.Size.X) / 2f, size * 0.13f);

                icon.AttachNode(button);

                new TextNode
                {
                    Position      = new(0f, size   * 0.58f),
                    Size          = new(size, size * 0.22f),
                    String        = macro.Name,
                    AlignmentType = AlignmentType.Center,
                    FontSize      = 12,
                    TextFlags     = TextFlags.Bold
                }.AttachNode(button);

                return button;
            }
        }

        private void BuildSystemSoundsTab
        (
            ScrollingNode<VerticalListNode> contentList
        )
        {
            var rowWidth = contentList.ContentNode.Width;

            var (columns, buttonWidth) = GetGridLayout(rowWidth, Gap, RowHeight * 3f);

            foreach (var chunk in instance.config.SoundEffectNotes.OrderBy(x => x.Key).Chunk(columns))
            {
                var row = CreateGridRow(rowWidth, RowHeight);

                foreach (var (key, value) in chunk)
                {
                    row.AddNode
                    (
                        CreateCompactTextButton
                        (
                            value,
                            new(buttonWidth, RowHeight),
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
                        )
                    );
                }

                contentList.ContentNode.AddNode(row);
            }
        }

        private void BuildGameItemsTab
        (
            ScrollingNode<VerticalListNode> contentList
        )
        {
            var rowWidth = contentList.ContentNode.Width;

            var listNode = new ListNode<Item, ItemListItemNode>
            {
                ItemSpacing = Gap,
                Size        = new(rowWidth, contentList.Size.Y - RowHeight - Gap),
                OptionsList = GetGameItemResults(),
                OnItemSelected = item =>
                {
                    if (item.RowId == 0)
                        return;

                    ContextMenuManager.Instance().OpenItem(item.RowId, AddonId);
                }
            };

            var searchBarNode = new TextInputNode
            {
                Size            = new(rowWidth, RowHeight),
                String          = itemSearchInput,
                MaxCharacters   = 128,
                OnInputReceived = text => UpdateGameItemList(listNode, text.ToString()),
                OnInputComplete = text => UpdateGameItemList(listNode, text.ToString())
            };

            searchBarNode.CurrentTextNode.FontSize = 14;
            contentList.ContentNode.AddNode(searchBarNode);

            contentList.ContentNode.AddNode(listNode);

            return;

            List<Item> GetGameItemResults() =>
                string.IsNullOrWhiteSpace(itemSearchInput) ?
                    [] :
                    searcher.SearchResult;

            void UpdateGameItemList
            (
                ListNode<Item, ItemListItemNode> node,
                string                           searchString
            )
            {
                itemSearchInput = searchString;
                searcher.Search(itemSearchInput);
                node.OptionsList = GetGameItemResults();
                node.ResetScroll();
            }
        }

        private void BuildSpecialIconCharsTab
        (
            ScrollingNode<VerticalListNode> contentList
        )
        {
            var rowWidth = contentList.ContentNode.Width;

            var (columns, buttonWidth) = GetGridLayout(rowWidth, Gap, RowHeight * 1.3f);

            foreach (var chunk in SeIconChars.Chunk(columns))
            {
                var row = CreateGridRow(rowWidth, RowHeight);

                foreach (var icon in chunk)
                {
                    var text = icon.ToString();

                    var button = new TextButtonNode
                    {
                        Size        = new(buttonWidth, RowHeight),
                        String      = text,
                        TextTooltip = $"0x{(int)icon:X4}",
                        OnClick     = () => CopyText(text)
                    };

                    button.LabelNode.FontSize = 18;
                    row.AddNode(button);
                }

                contentList.ContentNode.AddNode(row);
            }
        }

        private void BuildSettingsTab
        (
            ScrollingNode<VerticalListNode> contentList
        )
        {
            var contentWidth  = contentList.ContentNode.Width;
            var gap           = Gap;
            var controlHeight = RowHeight;
            var labelHeight   = RowHeight    * 0.6f;
            var buttonWidth   = contentWidth * 0.2f;
            var inputWidth    = contentWidth - buttonWidth - gap;

            var generalOverlay = new CollapsingHeaderNode
            {
                Size             = new(contentWidth, controlHeight),
                String           = Lang.Get("General"),
                FitWidth         = true,
                ItemSpacing      = gap,
                FirstItemSpacing = gap,
                OnToggle         = UpdateSettingsLayout
            };

            generalOverlay.AddNode
            (
                new TextNode
                {
                    Size          = new(contentWidth, labelHeight),
                    String        = Lang.Get("Offset"),
                    FontSize      = 13,
                    AlignmentType = AlignmentType.Left
                }
            );

            var offsetRow = new HorizontalListNode
            {
                Size               = new(contentWidth, controlHeight),
                ItemSpacing        = gap,
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

            var messagesSection = new CollapsingHeaderNode
            {
                Size             = new(contentWidth, controlHeight),
                String           = Lang.Get("QuickChatPanel-Messages"),
                IsCollapsed      = true,
                FitWidth         = true,
                FirstItemSpacing = gap,
                ItemSpacing      = gap,
                OnToggle         = UpdateSettingsLayout
            };

            var messageRow = new HorizontalListNode
            {
                Size               = new(contentWidth, controlHeight),
                ItemSpacing        = gap,
                FirstItemSpacing   = 0f,
                FitToContentHeight = true
            };

            var messageInputNode = new TextInputNode
            {
                Size          = new(inputWidth, controlHeight),
                MaxCharacters = 1000
            };
            messageRow.AddNode(messageInputNode);

            var addMessageButton = new TextButtonNode
            {
                Size   = new(buttonWidth, controlHeight),
                String = Lang.Get("Add"),
                OnClick = () =>
                {
                    var text = messageInputNode.String.ToString();
                    if (string.IsNullOrWhiteSpace(text) ||
                        instance.config.SavedMessages.Contains(text))
                        return;

                    instance.config.SavedMessages.Add(text);
                    instance.config.Save(instance);

                    AtkStage.Instance()->ClearFocus();
                }
            };
            addMessageButton.LabelNode.AutoAdjustTextSize();
            messageRow.AddNode(addMessageButton);
            messagesSection.AddNode(messageRow);

            foreach (var message in instance.config.SavedMessages.ToList())
            {
                var row = new HorizontalListNode
                {
                    Size               = new(contentWidth, controlHeight),
                    ItemSpacing        = gap,
                    FirstItemSpacing   = 0f,
                    FitToContentHeight = true
                };
                row.AddNode
                (
                    new TextNode
                    {
                        Size          = new(inputWidth, controlHeight),
                        String        = message,
                        AlignmentType = AlignmentType.Left,
                        FontSize      = 14
                    }
                );
                var button = new TextButtonNode
                {
                    Size   = new(buttonWidth, controlHeight),
                    String = Lang.Get("Delete"),
                    OnClick = () =>
                    {
                        instance.config.SavedMessages.Remove(message);
                        instance.config.Save(instance);
                    }
                };
                button.LabelNode.AutoAdjustTextSize();
                row.AddNode(button);
                messagesSection.AddNode(row);
            }

            var macrosSection = new CollapsingHeaderNode
            {
                Size             = new(contentWidth, controlHeight),
                String           = Lang.Get("QuickChatPanel-Macro"),
                IsCollapsed      = true,
                FitWidth         = true,
                ItemSpacing      = gap,
                FirstItemSpacing = gap,
                OnToggle         = UpdateSettingsLayout
            };

            macrosSection.AddNode
            (
                new TextNode
                {
                    Size          = new(contentWidth, labelHeight),
                    String        = Lang.Get("QuickChatPanel-MacroButton-DisplayType"),
                    FontSize      = 13,
                    AlignmentType = AlignmentType.Left
                }
            );

            var dropdown = new StringDropDownNode
            {
                Size           = new(contentWidth, controlHeight),
                MaxListOptions = MacroDisplayModeLoc.Length,
                Options        = [.. MacroDisplayModeLoc.Select(x => x.Text)]
            };
            dropdown.SelectedOption = MacroDisplayModeLoc.First(x => x.Mode == instance.config.OverlayMacroDisplayMode).Text;
            dropdown.OnOptionSelected = text =>
            {
                var mode = MacroDisplayModeLoc.FirstOrDefault(x => x.Text == text).Mode;
                if (mode == instance.config.OverlayMacroDisplayMode) return;

                instance.config.OverlayMacroDisplayMode = mode;
                instance.config.Save(instance);
            };
            macrosSection.AddNode(dropdown);

            macrosSection.AddDummy(gap);

            AddMacroSection(true);
            AddMacroSection(false);

            var sounds = new CollapsingHeaderNode
            {
                Size             = new(contentWidth, controlHeight),
                String           = Lang.Get("QuickChatPanel-SystemSound"),
                IsCollapsed      = true,
                FitWidth         = true,
                FirstItemSpacing = gap,
                ItemSpacing      = gap,
                OnToggle         = UpdateSettingsLayout
            };

            var soundLabelWidth = RowHeight * 1.6f;

            foreach (var (key, value) in instance.config.SoundEffectNotes.OrderBy(x => x.Key))
            {
                var row = new HorizontalListNode
                {
                    Size               = new(contentWidth, controlHeight),
                    ItemSpacing        = gap,
                    FirstItemSpacing   = 0f,
                    FitToContentHeight = true
                };
                row.AddNode
                (
                    new TextNode
                    {
                        Size          = new(soundLabelWidth, controlHeight),
                        String        = $"<se.{key}>",
                        AlignmentType = AlignmentType.Left,
                        FontSize      = 14
                    }
                );

                var input = new TextInputNode
                {
                    Size          = new(contentWidth - soundLabelWidth - gap, controlHeight),
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

            contentList.ContentNode.AddNode([generalOverlay, messagesSection, macrosSection, sounds]);
            return;

            void UpdateSettingsLayout
            (
                bool isExpanded
            )
            {
                contentList.ContentNode.RecalculateLayout();
                contentList.RecalculateSizes();
            }

            void AddMacroSection
            (
                bool isIndividual
            )
            {
                var module = RaptureMacroModule.Instance();

                macrosSection.AddNode
                (
                    new TextNode
                    {
                        Size = new(contentWidth, labelHeight),
                        String = LuminaWrapper.GetAddonText
                        (
                            isIndividual ?
                                17337U :
                                17338
                        ),
                        FontSize      = 13,
                        AlignmentType = AlignmentType.Left
                    }
                );
                var span = isIndividual ?
                               module->Individual :
                               module->Shared;

                for (var i = 0; i < span.Length; i++)
                {
                    var macro = span.GetPointer(i);
                    if (macro == null) continue;

                    var name = macro->Name.ToString();
                    if (string.IsNullOrEmpty(name)) continue;

                    var savedMacro = new SavedMacro
                    {
                        Position = i,
                        Category = isIndividual ?
                                       0U :
                                       1U,
                        Name           = name,
                        IconID         = macro->IconId,
                        LastUpdateTime = StandardTimeManager.Instance().Now
                    };
                    macrosSection.AddNode(MacroRow(savedMacro));
                }
            }

            HorizontalListNode MacroRow
            (
                SavedMacro macro
            )
            {
                var isSaved = instance.config.SavedMacros.Contains(macro);
                var row = new HorizontalListNode
                {
                    Size               = new(contentWidth, controlHeight),
                    ItemSpacing        = gap,
                    FirstItemSpacing   = 0f,
                    FitToContentHeight = true
                };

                var buttonCount = isSaved ?
                                      2 :
                                      1;

                var info = new SimpleComponentNode
                {
                    Size = new
                    (
                        contentWidth - (buttonCount * (buttonWidth + gap)),
                        controlHeight
                    )
                };

                var iconSize = RowHeight * 0.75f;

                var icon = new IconImageNode
                {
                    Position   = new(gap / 2f, (info.Height - iconSize) / 2f),
                    Size       = new(iconSize),
                    IconId     = macro.IconID,
                    FitTexture = true
                };
                icon.AttachNode(info);

                new TextNode
                {
                    Position      = new(icon.Position.X + iconSize        + gap, 0f),
                    Size          = new(info.Width      - icon.Position.X - iconSize - gap, controlHeight),
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
                        Size        = new(buttonWidth, controlHeight),
                        String      = Lang.Get("Refresh"),
                        TextTooltip = $"{Lang.Get("QuickChatPanel-LastUpdateTime")}: {instance.config.SavedMacros.Find(x => x.Equals(macro))?.LastUpdateTime}"
                    };
                    refreshButton.OnClick = () =>
                    {
                        var currentIndex = instance.config.SavedMacros.IndexOf(macro);
                        if (currentIndex < 0) return;

                        instance.config.SavedMacros[currentIndex] = macro;
                        instance.config.Save(instance);
                    };
                    refreshButton.LabelNode.AutoAdjustTextSize();
                    row.AddNode(refreshButton);
                }

                var toggleButton = new TextButtonNode
                {
                    Size = new(buttonWidth, controlHeight),
                    String = Lang.Get
                    (
                        isSaved ?
                            "Delete" :
                            "Add"
                    )
                };
                toggleButton.OnClick = () =>
                {
                    if (!instance.config.SavedMacros.Remove(macro))
                        instance.config.SavedMacros.Add(macro);

                    instance.config.Save(instance);
                };
                toggleButton.LabelNode.AutoAdjustTextSize();
                row.AddNode(toggleButton);
                return row;
            }

            void SaveSoundEffectNote
            (
                uint   key,
                string value
            )
            {
                if (instance.config.SoundEffectNotes.GetValueOrDefault(key) == value) return;

                instance.config.SoundEffectNotes[key] = value;
                instance.config.Save(instance);
            }

            HorizontalListNode OffsetInput
            (
                string      label,
                int         value,
                Action<int> updateValue
            )
            {
                var rowWidth   = (contentWidth - gap) / 2f;
                var labelWidth = RowHeight            * 0.5f;

                var row = new HorizontalListNode
                {
                    Size               = new(rowWidth, controlHeight),
                    ItemSpacing        = gap,
                    FirstItemSpacing   = 0f,
                    FitToContentHeight = true
                };

                row.AddNode
                (
                    new TextNode
                    {
                        Size          = new(labelWidth, controlHeight),
                        TextFlags     = TextFlags.AutoAdjustNodeSize,
                        String        = label,
                        AlignmentType = AlignmentType.Left,
                        FontSize      = 14
                    }
                );

                var input = new TextInputNode
                {
                    Size   = new(rowWidth - labelWidth - gap, controlHeight),
                    String = value.ToString()
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

        private void AddEmptyState
        (
            ScrollingNode<VerticalListNode> contentList,
            string                          text,
            string?                         detail = null
        )
        {
            var rowWidth = contentList.ContentNode.Width;

            var state = new VerticalListNode
            {
                Size        = new(rowWidth, RowHeight * 2f),
                ItemSpacing = Gap,
                FitWidth    = true
            };

            state.AddNode
            (
                new TextNode
                {
                    Size          = new(rowWidth, RowHeight),
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
                        Size          = new(rowWidth, RowHeight * 0.8f),
                        String        = detail,
                        TextColor     = ColorHelper.GetColor(3),
                        AlignmentType = AlignmentType.Center
                    }
                );
            }

            contentList.ContentNode.AddNode(state);
        }

        private HorizontalListNode CreateGridRow
        (
            float rowWidth,
            float rowHeight
        ) =>
            new()
            {
                Size               = new(rowWidth, rowHeight),
                ItemSpacing        = Gap,
                FirstItemSpacing   = 0f,
                FitToContentHeight = true
            };

        private static (int Columns, float CellSize) GetGridLayout
        (
            float rowWidth,
            float gap,
            float preferredWidth
        )
        {
            var columns  = Math.Max(1, (int)((rowWidth + gap) / (preferredWidth + gap)));
            var cellSize = (rowWidth - ((columns - 1) * gap)) / columns;

            return (columns, cellSize);
        }

        private TextButtonNode CreateTextActionRow
        (
            ScrollingNode<VerticalListNode>          contentList,
            string                                   text,
            string                                   tooltip,
            Action                                   onClick,
            AtkEventListener.Delegates.ReceiveEvent? onMouseClick = null
        )
        {
            var button = new TextButtonNode
            {
                Size        = new(contentList.ContentNode.Width, RowHeight),
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
            Vector2                                  size,
            string                                   tooltip,
            Action                                   onClick,
            AtkEventListener.Delegates.ReceiveEvent? onMouseClick = null
        )
        {
            var button = new TextButtonNode
            {
                Size        = size,
                String      = text,
                TextTooltip = tooltip,
                OnClick     = onClick
            };

            button.LabelNode.AutoAdjustTextSize();

            if (onMouseClick != null)
                button.AddEvent(AtkEventType.MouseClick, onMouseClick);

            return button;
        }

        #region 常量

        private const float GAP_RATIO       = 0.2f;
        private const float NAV_WIDTH_RATIO = 0.25f;
        private const float VISIBLE_ROWS    = 10f;

        #endregion
    }

    public class SavedMacro : IEquatable<SavedMacro>
    {
        public uint     Category       { get; init; }
        public int      Position       { get; init; }
        public string   Name           { get; set; } = string.Empty;
        public uint     IconID         { get; set; }
        public DateTime LastUpdateTime { get; set; } = DateTime.MinValue;

        public bool Equals
        (
            SavedMacro? other
        )
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return Category == other.Category && Position == other.Position;
        }

        public override bool Equals
        (
            object? obj
        )
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

    private static readonly (MacroDisplayMode Mode, string Text)[] MacroDisplayModeLoc =
    [
        (MacroDisplayMode.List, Lang.Get("QuickChatPanel-List")),
        (MacroDisplayMode.Buttons, Lang.Get("QuickChatPanel-Buttons"))
    ];

    private static readonly char[] SeIconChars = [.. Enum.GetValues<SeIconChar>().Select(x => (char)x)];

    #endregion
}
