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
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;
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
    
    private string messageInput   = string.Empty;
    private int    dropMacroIndex = -1;

    private IconButtonNode?      imageButton;
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
            Size                  = new(554f, 400f),
            RememberClosePosition = false,
            DisableClose          = true
        };

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "ChatLog", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "ChatLog", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "ChatLog", OnAddon);
    }

    protected override void ConfigUI()
    {
        using var table = ImRaii.Table("ConfigTable", 2, ImGuiTableFlags.SizingFixedFit);
        if (!table) return;

        ImGui.TableSetupColumn("Labels",   ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("Controls", ImGuiTableColumnFlags.WidthStretch);

        // Messages 行
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("QuickChatPanel-Messages")}:");

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(200f * GlobalUIScale);

        using (var combo = ImRaii.Combo("###MessagesCombo", Lang.Get("QuickChatPanel-SavedMessagesAmountText", config.SavedMessages.Count)))
        {
            if (combo)
            {
                ImGui.InputText("###MessageToSaveInput", ref messageInput, 1000);

                ImGui.SameLine();

                if (ImGuiOm.ButtonIcon("###MessagesInputAdd", FontAwesomeIcon.Plus))
                {
                    if (!config.SavedMessages.Contains(messageInput))
                    {
                        config.SavedMessages.Add(messageInput);
                        config.Save(this);
                    }
                }

                if (config.SavedMessages.Count > 0)
                    ImGui.Separator();

                foreach (var message in config.SavedMessages.ToList())
                {
                    ImGuiOm.ButtonSelectable(message);

                    using var popup = ImRaii.ContextPopup($"{message}");
                    if (!popup) continue;

                    if (ImGuiOm.ButtonSelectable(Lang.Get("Delete")))
                        config.SavedMessages.Remove(message);
                }
            }
        }

        // Macro 行
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("QuickChatPanel-Macro")}:");

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(200f * GlobalUIScale);

        using (var combo = ImRaii.Combo
               (
                   "###MacroCombo",
                   Lang.Get("QuickChatPanel-SavedMacrosAmountText", config.SavedMacros.Count),
                   ImGuiComboFlags.HeightLargest
               ))
        {
            if (combo)
            {
                DrawMacroChild(true);

                ImGui.SameLine();
                DrawMacroChild(false);
            }
        }

        // SystemSound 行
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("QuickChatPanel-SystemSound")}:");

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(200f * GlobalUIScale);

        using (var combo = ImRaii.Combo("###SoundEffectNoteEditCombo", string.Empty, ImGuiComboFlags.HeightLarge))
        {
            if (combo)
            {

                foreach (var seNote in config.SoundEffectNotes)
                {
                    using var id = ImRaii.PushId($"{seNote.Key}");

                    ImGui.AlignTextToFramePadding();
                    ImGui.TextUnformatted($"<se.{seNote.Key}>{(seNote.Key < 10 ? "  " : "")}");

                    ImGui.SameLine();
                    ImGui.TextUnformatted("——>");

                    var note = seNote.Value;
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(200f * GlobalUIScale);
                    if (ImGui.InputText("###SENoteInput", ref note, 32))
                        config.SoundEffectNotes[seNote.Key] = note;

                    if (ImGui.IsItemDeactivatedAfterEdit())
                        config.Save(this);
                }
            }
        }

        // ButtonOffset 行
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("QuickChatPanel-ButtonOffset")}:");

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(200f * GlobalUIScale);
        ImGui.InputFloat2("###ButtonOffsetInput", ref config.ButtonOffset, format: "%.1f");
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);

        // ButtonIcon 行
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("QuickChatPanel-ButtonIcon")}:");

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(200f * GlobalUIScale);
        ImGui.InputInt("###ButtonIconInput", ref config.ButtonIcon);

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.ButtonIcon = Math.Max(config.ButtonIcon, 1);
            config.Save(this);
        }

        // ButtonBackground 行
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("QuickChatPanel-ButtonBackgroundVisible")}:");

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(200f * GlobalUIScale);
        if (ImGui.Checkbox("###ButtonBackgroundVisibleInput", ref config.ButtonBackgroundVisible))
            config.Save(this);

        // FontScale 行
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("FontScale")}:");

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(200f * GlobalUIScale);
        ImGui.InputFloat("###FontScaleInput", ref config.FontScale, 0, 0, "%.1f");

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.FontScale = (float)Math.Clamp(config.FontScale, 0.1, 10f);
            config.Save(this);
        }

        // OverlayPosOffset 行
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("QuickChatPanel-OverlayPosOffset")}:");

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(200f * GlobalUIScale);
        ImGui.InputFloat2("###OverlayPosOffsetInput", ref config.OverlayOffset, format: "%.1f");
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);

        // OverlayMacroDisplayMode 行
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("QuickChatPanel-OverlayMacroDisplayMode")}:");

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(200f * GlobalUIScale);

        using (var combo = ImRaii.Combo("###OverlayMacroDisplayModeCombo", MacroDisplayModeLoc[config.OverlayMacroDisplayMode]))
        {
            if (combo)
            {
                foreach (MacroDisplayMode mode in Enum.GetValues(typeof(MacroDisplayMode)))
                {
                    if (ImGui.Selectable(MacroDisplayModeLoc[mode], mode == config.OverlayMacroDisplayMode))
                    {
                        config.OverlayMacroDisplayMode = mode;
                        config.Save(this);
                    }
                }
            }
        }

        return;

        void DrawMacroChild(bool isIndividual)
        {
            var childSize = new Vector2(200 * GlobalUIScale, 300 * GlobalUIScale);
            var module    = RaptureMacroModule.Instance();
            if (module == null) return;

            using var child = ImRaii.Child($"{(isIndividual ? "Individual" : "Shared")}MacroSelectChild", childSize);
            if (!child) return;

            ImGui.TextUnformatted(Lang.Get($"QuickChatPanel-{(isIndividual ? "Individual" : "Shared")}Macros"));
            ImGui.Separator();

            var span = isIndividual ? module->Individual : module->Shared;

            for (var i = 0; i < span.Length; i++)
            {
                var macro = span.GetPointer(i);
                if (macro == null) continue;

                var name = macro->Name.ToString();
                var icon = ImageHelper.GetGameIcon(macro->IconId);
                if (string.IsNullOrEmpty(name) || icon == null) continue;

                var currentSavedMacro = (*macro).ToSavedMacro();
                currentSavedMacro.Position = i;
                currentSavedMacro.Category = isIndividual ? 0U : 1U;

                using (ImRaii.PushId($"{currentSavedMacro.Category}-{currentSavedMacro.Position}"))
                {
                    if (ImGuiOm.SelectableImageWithText
                        (
                            icon.Handle,
                            new(24),
                            name,
                            config.SavedMacros.Contains(currentSavedMacro),
                            ImGuiSelectableFlags.DontClosePopups
                        ))
                    {
                        if (!config.SavedMacros.Remove(currentSavedMacro))
                        {
                            config.SavedMacros.Add(currentSavedMacro);
                            config.Save(this);
                        }
                    }

                    if (!config.SavedMacros.Contains(currentSavedMacro)) continue;

                    using (var context = ImRaii.ContextPopupItem("Context"))
                    {
                        if (!context) continue;

                        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("QuickChatPanel-LastUpdateTime")}:");

                        ImGui.SameLine();
                        ImGui.TextUnformatted($"{config.SavedMacros.Find(x => x.Equals(currentSavedMacro))?.LastUpdateTime}");

                        ImGui.Separator();

                        if (ImGuiOm.SelectableTextCentered(Lang.Get("Refresh")))
                        {
                            var currentIndex = config.SavedMacros.IndexOf(currentSavedMacro);

                            if (currentIndex != -1)
                            {
                                config.SavedMacros[currentIndex] = currentSavedMacro;
                                config.Save(this);
                            }
                        }
                    }
                }
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

                inputBackground->SetWidth((ushort)(windowNode->Width      - textInputNode->Height - 40));
                textInputDisplayNode->SetWidth((ushort)(windowNode->Width - textInputNode->Height - 40));
                textInputNode->SetWidth((ushort)(windowNode->Width        - textInputNode->Height - 40));

                if (imageButton == null)
                {
                    imageButton = new()
                    {
                        Size        = new(textInputNode->Height),
                        IsVisible   = true,
                        IsEnabled   = true,
                        IconId      = (uint)config.ButtonIcon,
                        OnClick     = () => { chatPanelAddon?.TogglePanel(); },
                        TextTooltip = Info.Title,
                        Position = new Vector2(textInputNode->Width - textInputNode->Height, 0) +
                                   config.ButtonOffset
                    };

                    imageButton?.AttachNode(textInputNode);
                }

                if (Throttler.Shared.Throttle("QuickChatPanel-UpdateButtonNodes"))
                {
                    imageButton.IconId   = (uint)config.ButtonIcon;
                    imageButton.Position = new Vector2(windowNode->Width - 2 * textInputNode->Height, 0) + config.ButtonOffset;
                    imageButton.Size     = new(textInputNode->Height);

                    imageButton.BackgroundNode.IsVisible = config.ButtonBackgroundVisible;
                }

                break;
            case AddonEvent.PreFinalize:
                imageButton?.Dispose();
                imageButton = null;
                break;
        }
    }

    public void SwapMacros(int index1, int index2)
    {
        (config.SavedMacros[index1], config.SavedMacros[index2]) =
            (config.SavedMacros[index2], config.SavedMacros[index1]);

        TaskHelper.Abort();

        TaskHelper.DelayNext(500);
        TaskHelper.Enqueue(() => { config.Save(this); });
    }

    public void SwapMessages(int index1, int index2)
    {
        (config.SavedMessages[index1], config.SavedMessages[index2]) =
            (config.SavedMessages[index2], config.SavedMessages[index1]);

        TaskHelper.Abort();

        TaskHelper.DelayNext(500);
        TaskHelper.Enqueue(() => { config.Save(this); });
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
        SpecialIconChars
    }

    private class Config : ModuleConfig
    {
        public bool                     ButtonBackgroundVisible = true;
        public int                      ButtonIcon              = 46;
        public Vector2                  ButtonOffset            = new(0);
        public float                    FontScale               = 1.5f;
        public MacroDisplayMode         OverlayMacroDisplayMode = MacroDisplayMode.Buttons;
        public Vector2                  OverlayOffset           = new(0);
        public List<SavedMacro>         SavedMacros             = [];
        public List<string>             SavedMessages           = [];
        public Dictionary<uint, string> SoundEffectNotes        = [];
    }

    private bool SendChatboxMessage()
    {
        if (ChatLog == null || !ChatLog->IsAddonAndNodesReady()) return false;

        var inputNode = (AtkComponentNode*)ChatLog->GetNodeById(5);
        if (inputNode == null) return false;

        var textNode = inputNode->Component->UldManager.SearchNodeById(16)->GetAsAtkTextNode();
        if (textNode == null) return false;

        var text = SeString.Parse(textNode->NodeText);
        if (string.IsNullOrWhiteSpace(text.ToString())) return false;

        ChatManager.Instance().SendMessage(text.Encode());

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

    private static void SendGameItemLink(Item item) =>
        NotifyHelper.Instance().Chat
        (
            new SeStringBuilder()
                .AddItemLink(item.RowId, PluginConfig.Instance().ConflictKeyBinding.IsPressed())
                .Build()
        );

    private class QuickChatPanelAddon : AttachedAddon
    {
        private readonly Dictionary<QuickChatTab, ListButtonNode> tabButtons = [];
        private readonly LuminaSearcher<Item>                     searcher;
        private readonly QuickChatPanel                           instance;

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

        protected override bool CanCloseHostAddon(AtkUnitBase* hostAddon) =>
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

        public void RequestRebuild()
        {
            rebuildRequested = true;

            if (IsOpen)
                CloseAddonOnly();
        }

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

            CreateBody().AttachNode(this);

            RefreshCurrentTab();
        }

        private HorizontalListNode CreateBody()
        {
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
                Size        = new(116f, body.Height),
                ItemSpacing = 5f,
                FitWidth    = true
            };
            nav.AddNode(CreateNavButton(QuickChatTab.Messages,         Lang.Get("QuickChatPanel-Messages")));
            nav.AddNode(CreateNavButton(QuickChatTab.Macros,           Lang.Get("QuickChatPanel-Macro")));
            nav.AddNode(CreateNavButton(QuickChatTab.SystemSounds,     Lang.Get("QuickChatPanel-SystemSound")));
            nav.AddNode(CreateNavButton(QuickChatTab.GameItems,        Lang.Get("QuickChatPanel-GameItems")));
            nav.AddNode(CreateNavButton(QuickChatTab.SpecialIconChars, Lang.Get("QuickChatPanel-SpecialIconChar")));

            var settingButton = new ListButtonNode
            {
                IsVisible = true,
                IsEnabled = true,
                Size      = new(116f, 34f),
                String    = Lang.Get("Settings"),
                OnClick   = () => ChatManager.Instance().SendMessage($"/pdr search {Lang.Get("QuickChatPanelTitle")}")
            };

            settingButton.LabelNode.FontSize = 15;
            settingButton.LabelNode.AutoAdjustTextSize();

            nav.AddDummy(8f);
            nav.AddNode(settingButton);

            contentPanel = CreateContentPanel(new(MathF.Max(360f, ContentSize.X - 132f), body.Height));

            body.AddNode(nav);
            body.AddNode(contentPanel);

            RefreshTabButtons();
            return body;
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

        private SimpleComponentNode CreateContentPanel(Vector2 size)
        {
            var panel = new SimpleComponentNode
            {
                IsVisible = true,
                Size      = size
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
                Size               = size,
                Alpha              = 0.72f
            };
            contentPanelBackground.AttachNode(panel);

            contentList = new()
            {
                IsVisible         = true,
                Position          = new(9f),
                Size              = new(MathF.Max(330f, size.X - 18f), MathF.Max(70f, size.Y - 18f)),
                ItemSpacing       = 5f,
                FitWidth          = true,
                AutoHideScrollBar = true,
                ScrollSpeed       = 36
            };
            contentList.AttachNode(panel);

            return panel;
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
            if (contentList == null) return;

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
            }

            contentList.RecalculateLayout();
        }

        private void BuildMessagesTab()
        {
            if (instance.config.SavedMessages.Count == 0)
            {
                AddEmptyState(Lang.Get("QuickChatPanel-SavedMessagesAmountText", 0), Lang.Get("QuickChatPanel-SendMessageHelp"));
                return;
            }

            for (var i = 0; i < instance.config.SavedMessages.Count; i++)
            {
                var index   = i;
                var message = instance.config.SavedMessages[i];
                contentList?.AddNode
                (
                    CreateTextActionRow
                    (
                        message,
                        Lang.Get("QuickChatPanel-SendMessageHelp"),
                        () => CopyText(message),
                        (_, _, _, _, data) =>
                        {
                            if (data->MouseData.ButtonId == 1)
                            {
                                instance.SendSavedMessage(message);
                                return;
                            }

                            if (data->MouseData.Modifier.HasFlag(ModifierFlag.Shift) && instance.dropMacroIndex >= 0)
                            {
                                instance.SwapMessages(instance.dropMacroIndex, index);
                                instance.dropMacroIndex = -1;
                                RequestRebuild();
                                return;
                            }

                            if (data->MouseData.Modifier.HasFlag(ModifierFlag.Shift))
                            {
                                instance.dropMacroIndex = index;
                                return;
                            }

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
            for (var i = 0; i < instance.config.SavedMacros.Count; i++)
            {
                var index = i;
                var macro = instance.config.SavedMacros[i];
                if (string.IsNullOrWhiteSpace(macro.Name)) continue;

                contentList?.AddNode
                (
                    CreateMacroListButton
                    (
                        macro.IconID,
                        macro.Name,
                        () => instance.ExecuteMacro(macro),
                        (_, _, _, _, data) =>
                        {
                            if (data->MouseData.Modifier.HasFlag(ModifierFlag.Shift) && instance.dropMacroIndex >= 0)
                            {
                                instance.SwapMacros(instance.dropMacroIndex, index);
                                instance.dropMacroIndex = -1;
                                RequestRebuild();
                                return;
                            }

                            if (data->MouseData.Modifier.HasFlag(ModifierFlag.Shift))
                            {
                                instance.dropMacroIndex = index;
                                return;
                            }

                            instance.ExecuteMacro(macro);
                        }
                    )
                );
            }

            return;

            TextButtonNode CreateMacroListButton
            (
                uint                                     iconID,
                string                                   title,
                Action                                   onClick,
                AtkEventListener.Delegates.ReceiveEvent? onMouseClick = null
            )
            {
                var button = new TextButtonNode
                {
                    IsVisible   = true,
                    IsEnabled   = true,
                    Size        = new(contentList.ContentWidth, 52f),
                    String      = string.Empty,
                    TextTooltip = title,
                    OnClick     = onClick
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
                    IsVisible        = true,
                    Position         = new(icon.Position.X + icon.Size.X + 6f, 0f),
                    String           = title,
                    AlignmentType    = AlignmentType.Left,
                    FontSize         = 14,
                    TextFlags        = TextFlags.Bold | TextFlags.Edge,
                    TextColor        = ColorHelper.GetColor(50),
                    TextOutlineColor = ColorHelper.GetColor(1)
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

                var macro1 = macro;
                var button = CreateMacroCardButton(macro.IconID, macro.Name, () => instance.ExecuteMacro(macro1));

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
                    Size        = new(110f, 100f),
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
                icon.Position = new((button.Size.X - icon.Size.X) / 2, 10f);

                icon.AttachNode(button);

                new TextNode
                {
                    IsVisible        = true,
                    Position         = new(0f, 60f),
                    Size             = new(button.Width, 24f),
                    String           = text,
                    AlignmentType    = AlignmentType.Center,
                    FontSize         = 12,
                    TextFlags        = TextFlags.Bold | TextFlags.Edge,
                    TextColor        = ColorHelper.GetColor(50),
                    TextOutlineColor = ColorHelper.GetColor(1)
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
        }

        private List<Item> GetGameItemResults() =>
            string.IsNullOrWhiteSpace(itemSearchInput) ? [] : searcher.SearchResult;

        private void UpdateGameItemList(ListNode<Item, ItemListItemNode> listNode, string searchString)
        {
            itemSearchInput = searchString;
            searcher.Search(itemSearchInput);
            listNode.OptionsList = GetGameItemResults();
            listNode.ResetScroll();
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
