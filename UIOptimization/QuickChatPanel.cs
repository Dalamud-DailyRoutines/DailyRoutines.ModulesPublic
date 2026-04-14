using System.Collections.Frozen;
using System.Numerics;
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
using Dalamud.Interface.Colors;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;

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

    private LuminaSearcher<Item>? searcher;

    private string messageInput   = string.Empty;
    private int    dropMacroIndex = -1;

    private IconButtonNode? imageButton;

    private List<PanelTabBase> panelTabs = [];

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        TaskHelper ??= new() { TimeoutMS = 5_000 };

        Overlay       ??= new(this);
        Overlay.Flags |=  ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollWithMouse;
        Overlay.Flags &=  ~ImGuiWindowFlags.AlwaysAutoResize;
        Overlay.SizeConstraints = new()
        {
            MinimumSize = new(1, config.OverlayHeight)
        };

        if (config.SoundEffectNotes.Count <= 0)
        {
            for (var i = 1U; i < 17; i++)
                config.SoundEffectNotes[i] = $"<se.{i}>";
        }

        searcher ??= new(LuminaGetter.Get<Item>(), [x => x.Name.ToString(), x => x.RowId.ToString()]);

        // 初始化 Panel Tabs
        panelTabs =
        [
            new MessageTab(this),
            new MacroTab(this),
            new SystemSoundTab(this),
            new GameItemTab(this),
            new SpecialIconCharTab(this)
        ];

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

        // OverlayHeight 行
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("QuickChatPanel-OverlayHeight")}:");

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(200f * GlobalUIScale);
        ImGui.InputFloat("###OverlayHeightInput", ref config.OverlayHeight, 0, 0, "%.1f");

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.OverlayHeight = Math.Clamp(config.OverlayHeight, 100f, 10000f);
            config.Save(this);

            Overlay.SizeConstraints = new()
            {
                MinimumSize = new(1, config.OverlayHeight)
            };
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

    protected override void OverlayPreDraw()
    {
        if (DService.Instance().ObjectTable.LocalPlayer == null ||
            ChatLog                                     == null ||
            !ChatLog->IsVisible                                 ||
            ChatLog->GetNodeById(5) == null)
            Overlay.IsOpen = false;
    }

    protected override void OverlayUI()
    {
        if (DService.Instance().KeyState[VirtualKey.ESCAPE])
        {
            Overlay.IsOpen = false;
            return;
        }

        using var font = FontManager.Instance().GetUIFont(config.FontScale).Push();

        var itemSpacing = ImGui.GetStyle().ItemSpacing;

        var textInputNode = ChatLog->GetNodeById(5);
        var windowNode    = ChatLog->RootNode;
        var buttonPos     = new Vector2(windowNode->ScreenX + windowNode->Width, textInputNode->ScreenY)    + config.ButtonOffset;
        ImGui.SetWindowPos(buttonPos with { Y = buttonPos.Y - ImGui.GetWindowSize().Y - 3 * itemSpacing.Y } + config.OverlayOffset);

        var isOpen = true;
        ImGui.SetNextWindowPos(new(ImGui.GetWindowPos().X, ImGui.GetWindowPos().Y + ImGui.GetWindowHeight() - itemSpacing.Y));
        ImGui.SetNextWindowSize(new(ImGui.GetWindowWidth(), 2 * ImGui.GetTextLineHeight()));

        if (ImGui.Begin
            (
                "###QuickChatPanel-SendMessages",
                ref isOpen,
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
            ))
        {
            if (ImGuiOm.SelectableTextCentered(Lang.Get("QuickChatPanel-SendChatboxMessage")))
            {
                var inputNode = (AtkComponentNode*)ChatLog->GetNodeById(5);
                var textNode  = inputNode->Component->UldManager.SearchNodeById(16)->GetAsAtkTextNode();
                var text      = SeString.Parse(textNode->NodeText);

                if (!string.IsNullOrWhiteSpace(text.ToString()))
                {
                    ChatManager.Instance().SendMessage(text.Encode());

                    var inputComponent = (AtkComponentTextInput*)inputNode->Component;
                    inputComponent->EvaluatedString.Clear();
                    inputComponent->RawString.Clear();
                    inputComponent->AvailableLines.Clear();
                    inputComponent->HighlightedAutoTranslateOptionColorPrefix.Clear();
                    inputComponent->HighlightedAutoTranslateOptionColorSuffix.Clear();
                    textNode->NodeText.Clear();

                    Overlay.IsOpen = false;
                }
            }

            ImGui.End();
        }

        using (ImRaii.Group())
            DrawOverlayContent();

        ImGui.Separator();
    }

    private void DrawOverlayContent()
    {
        using var tabBar = ImRaii.TabBar("###QuickChatPanel", ImGuiTabBarFlags.Reorderable);
        if (!tabBar) return;

        // 使用 PanelTabs 列表绘制所有 Tab
        foreach (var panelTab in panelTabs)
        {
            using var item = ImRaii.TabItem(panelTab.TabName);
            if (item)
                panelTab.DrawTabContent();
            else if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(panelTab.Tooltip))
                ImGuiOm.TooltipHover(panelTab.Tooltip);
        }

        if (ImGui.TabItemButton($"{FontAwesomeIcon.Cog.ToIconString()}###OpenQuickChatPanelSettings"))
            ChatManager.Instance().SendMessage($"/pdr search {Lang.Get("QuickChatPanelTitle")}");
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
                        OnClick     = () => { Overlay.Toggle(); },
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

        searcher = null;
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

    private class Config : ModuleConfig
    {
        public bool                     ButtonBackgroundVisible = true;
        public int                      ButtonIcon              = 46;
        public Vector2                  ButtonOffset            = new(0);
        public float                    FontScale               = 1.5f;
        public float                    OverlayHeight           = 350f * GlobalUIScale;
        public MacroDisplayMode         OverlayMacroDisplayMode = MacroDisplayMode.Buttons;
        public Vector2                  OverlayOffset           = new(0);
        public List<SavedMacro>         SavedMacros             = [];
        public List<string>             SavedMessages           = [];
        public Dictionary<uint, string> SoundEffectNotes        = [];
    }

    // 消息 Tab
    private class MessageTab
    (
        QuickChatPanel instance
    ) : PanelTabBase(instance)
    {
        public override string TabName => Lang.Get("QuickChatPanel-Messages");

        public override string Tooltip => Lang.Get("QuickChatPanelTitle-DragHelp");

        public override void DrawTabContent()
        {
            var maxTextWidth = 300f * GlobalUIScale;

            using (var child = ImRaii.Child("MessagesChild", ImGui.GetContentRegionAvail(), false))
            {
                if (!child) return;

                for (var i = 0; i < Instance.config.SavedMessages.Count; i++)
                {
                    var message = Instance.config.SavedMessages[i];

                    var textWidth = ImGui.CalcTextSize(message).X;
                    maxTextWidth = Math.Max(textWidth + 64,       maxTextWidth);
                    maxTextWidth = Math.Max(300f * GlobalUIScale, maxTextWidth);

                    ImGuiOm.SelectableTextCentered(message);

                    if (ImGui.IsKeyDown(ImGuiKey.LeftShift))
                    {
                        if (ImGui.BeginDragDropSource())
                        {
                            if (ImGui.SetDragDropPayload("MessageReorder", []))
                                Instance.dropMacroIndex = i;
                            ImGui.TextColored(ImGuiColors.DalamudYellow, message);
                            ImGui.EndDragDropSource();
                        }

                        if (ImGui.BeginDragDropTarget())
                        {
                            if (Instance.dropMacroIndex                                       >= 0 ||
                                ImGui.AcceptDragDropPayload("MessageReorder").Handle != null)
                            {
                                Instance.SwapMessages(Instance.dropMacroIndex, i);
                                Instance.dropMacroIndex = -1;
                            }

                            ImGui.EndDragDropTarget();
                        }
                    }

                    if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                        ImGui.SetClipboardText(message);

                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                        ChatManager.Instance().SendMessage(message);

                    ImGuiOm.TooltipHover(Lang.Get("QuickChatPanel-SendMessageHelp"));

                    if (i != Instance.config.SavedMessages.Count - 1)
                        ImGui.Separator();
                }
            }

            SetWindowSize(Math.Max(350f * GlobalUIScale, maxTextWidth));
        }
    }

    // 宏 Tab
    private class MacroTab
    (
        QuickChatPanel instance
    ) : PanelTabBase(instance)
    {
        public override string TabName => Lang.Get("QuickChatPanel-Macro");

        public override string Tooltip => Lang.Get("QuickChatPanelTitle-DragHelp");

        public override void DrawTabContent()
        {
            var maxTextWidth = 300f * GlobalUIScale;

            using (var child = ImRaii.Child("MacroChild", ImGui.GetContentRegionAvail(), false))
            {
                if (!child) return;

                using (ImRaii.Group())
                {
                    for (var i = 0; i < Instance.config.SavedMacros.Count; i++)
                    {
                        var macro = Instance.config.SavedMacros[i];

                        var name = macro.Name;
                        var icon = ImageHelper.GetGameIcon(macro.IconID);
                        if (string.IsNullOrEmpty(name) || icon == null) continue;

                        switch (Instance.config.OverlayMacroDisplayMode)
                        {
                            case MacroDisplayMode.List:
                                if (ImGuiOm.SelectableImageWithText(icon.Handle, new(24), name, false))
                                {
                                    var gameMacro =
                                        RaptureMacroModule.Instance()->GetMacro(macro.Category, (uint)macro.Position);

                                    RaptureShellModule.Instance()->ExecuteMacro(gameMacro);
                                }

                                break;
                            case MacroDisplayMode.Buttons:
                                var textSize   = ImGui.CalcTextSize("六个字也行吧");
                                var buttonSize = textSize with { Y = textSize.Y * 2 + icon.Height };

                                if (ImGuiOm.ButtonImageWithTextVertical(icon, name, buttonSize))
                                {
                                    var gameMacro =
                                        RaptureMacroModule.Instance()->GetMacro(macro.Category, (uint)macro.Position);

                                    RaptureShellModule.Instance()->ExecuteMacro(gameMacro);
                                }

                                break;
                        }

                        if (ImGui.IsKeyDown(ImGuiKey.LeftShift))
                        {
                            if (ImGui.BeginDragDropSource())
                            {
                                if (ImGui.SetDragDropPayload("MacroReorder", []))
                                    Instance.dropMacroIndex = i;
                                ImGui.TextColored(ImGuiColors.DalamudYellow, name);
                                ImGui.EndDragDropSource();
                            }

                            if (ImGui.BeginDragDropTarget())
                            {
                                if (Instance.dropMacroIndex                                     >= 0 ||
                                    ImGui.AcceptDragDropPayload("MacroReorder").Handle != null)
                                {
                                    Instance.SwapMacros(Instance.dropMacroIndex, i);
                                    Instance.dropMacroIndex = -1;
                                }

                                ImGui.EndDragDropTarget();
                            }
                        }

                        switch (Instance.config.OverlayMacroDisplayMode)
                        {
                            case MacroDisplayMode.List:
                                if (i != Instance.config.SavedMacros.Count - 1)
                                    ImGui.Separator();

                                break;
                            case MacroDisplayMode.Buttons:
                                ImGui.SameLine();
                                if ((i + 1) % 5 == 0)
                                    ImGui.Dummy(new(20 * Instance.config.FontScale));
                                break;
                        }
                    }
                }

                maxTextWidth = ImGui.GetItemRectSize().X;
            }

            SetWindowSize(Math.Max(350f * GlobalUIScale, maxTextWidth));
        }
    }

    // 系统音 Tab
    private class SystemSoundTab
    (
        QuickChatPanel instance
    ) : PanelTabBase(instance)
    {
        public override string TabName => Lang.Get("QuickChatPanel-SystemSound");

        public override void DrawTabContent()
        {
            var maxTextWidth = 300f * GlobalUIScale;

            using (var child = ImRaii.Child("SystemSoundChild"))
            {
                if (!child) return;

                using (ImRaii.Group())
                {
                    foreach (var seNote in Instance.config.SoundEffectNotes)
                    {
                        ImGuiOm.ButtonSelectable($"{seNote.Value}###PlaySound{seNote.Key}");

                        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                            UIGlobals.PlayChatSoundEffect(seNote.Key);

                        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                            ChatManager.Instance().SendMessage($"<se.{seNote.Key}><se.{seNote.Key}>");

                        ImGuiOm.TooltipHover(Lang.Get("QuickChatPanel-SystemSoundHelp"));
                    }
                }

                maxTextWidth = ImGui.GetItemRectSize().X;
                maxTextWidth = Math.Max(300f * GlobalUIScale, maxTextWidth);
            }

            SetWindowSize(Math.Max(350f * GlobalUIScale, maxTextWidth));
        }
    }

    // 游戏物品 Tab
    private class GameItemTab
    (
        QuickChatPanel instance
    ) : PanelTabBase(instance)
    {
        private static  string ItemSearchInput = string.Empty;
        public override string TabName => Lang.Get("QuickChatPanel-GameItems");

        public override void DrawTabContent()
        {
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputTextWithHint("###GameItemSearchInput", Lang.Get("PleaseSearch"), ref ItemSearchInput, 128))
                Instance.searcher.Search(ItemSearchInput);
            if (ImGui.IsItemDeactivatedAfterEdit())
                Instance.searcher.Search(ItemSearchInput);

            var maxTextWidth = 300f * GlobalUIScale;

            using (var child = ImRaii.Child("GameItemChild", ImGui.GetContentRegionAvail(), false))
            {
                if (!child) return;

                ImGui.Separator();

                if (!string.IsNullOrWhiteSpace(ItemSearchInput))
                {
                    var longestText          = string.Empty;
                    var isConflictKeyHolding = PluginConfig.Instance().ConflictKeyBinding.IsPressed();

                    foreach (var data in Instance.searcher.SearchResult)
                    {
                        if (!LuminaGetter.TryGetRow(data.RowId, out Item itemData)) continue;
                        if (!DService.Instance().Texture.TryGetFromGameIcon(new(itemData.Icon, isConflictKeyHolding), out var texture)) continue;

                        var itemName = itemData.Name.ToString();
                        if (itemName.Length > longestText.Length)
                            longestText = itemName;

                        if (ImGuiOm.SelectableImageWithText(texture.GetWrapOrEmpty().Handle, ScaledVector2(24f), itemName, false))
                            NotifyHelper.Instance().Chat(new SeStringBuilder().AddItemLink(itemData.RowId, isConflictKeyHolding).Build());
                    }

                    maxTextWidth = ImGui.CalcTextSize(longestText).X;
                    maxTextWidth = Math.Max(350f * GlobalUIScale, maxTextWidth);
                }
            }

            SetWindowSize(Math.Max(350f * GlobalUIScale, maxTextWidth));
        }
    }

    // 特殊图标字符 Tab
    private class SpecialIconCharTab
    (
        QuickChatPanel instance
    ) : PanelTabBase(instance)
    {
        public override string TabName => Lang.Get("QuickChatPanel-SpecialIconChar");

        public override void DrawTabContent()
        {
            var maxTextWidth = 300f * GlobalUIScale;

            using (var child = ImRaii.Child("SeIconChild", ImGui.GetContentRegionAvail(), false))
            {
                if (!child) return;

                using (ImRaii.Group())
                {
                    var counter = -1;

                    foreach (var icon in SeIconChars)
                    {
                        counter++;
                        
                        if (ImGui.Button($"{icon}", new(96 * Instance.config.FontScale)))
                            ImGui.SetClipboardText(icon.ToString());

                        ImGuiOm.TooltipHover($"0x{(int)icon:X4}");

                        ImGui.SameLine();
                        if ((counter + 1) % 7 == 0)
                            ImGui.Dummy(new(20 * Instance.config.FontScale));
                    }
                }

                maxTextWidth = ImGui.GetItemRectSize().X;
                maxTextWidth = MathF.Max(300f * GlobalUIScale, maxTextWidth);
            }

            SetWindowSize(MathF.Max(350f * GlobalUIScale, maxTextWidth));
        }
    }

    // Panel Tab 基类
    private abstract class PanelTabBase
    (
        QuickChatPanel instance
    )
    {
        protected QuickChatPanel Instance { get; } = instance;

        public abstract string TabName { get; }

        public virtual string Tooltip { get; } = string.Empty;

        public abstract void DrawTabContent();

        protected void SetWindowSize(float maxTextWidth) =>
            ImGui.SetWindowSize(new(Math.Max(350f * GlobalUIScale, maxTextWidth), Instance.config.OverlayHeight * GlobalUIScale));
    }
    
    #region 常量

    private static readonly FrozenDictionary<MacroDisplayMode, string> MacroDisplayModeLoc = new Dictionary<MacroDisplayMode, string>
    {
        [MacroDisplayMode.List]    = Lang.Get("QuickChatPanel-List"),
        [MacroDisplayMode.Buttons] = Lang.Get("QuickChatPanel-Buttons")
    }.ToFrozenDictionary();

    private static readonly FrozenSet<char> SeIconChars = Enum.GetValues<SeIconChar>().Select(x => (char)x).ToFrozenSet();

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
