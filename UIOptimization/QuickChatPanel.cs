using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Colors;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class QuickChatPanel : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("QuickChatPanelTitle"),
        Description = GetLoc("QuickChatPanelDescription"),
        Category    = ModuleCategories.UIOptimization,
    };
    
    private static readonly Dictionary<MacroDisplayMode, string> MacroDisplayModeLoc = new()
    {
        [MacroDisplayMode.List]    = GetLoc("QuickChatPanel-List"),
        [MacroDisplayMode.Buttons] = GetLoc("QuickChatPanel-Buttons"),
    };

    private static readonly char[] SeIconChars = Enum.GetValues<SeIconChar>().Select(x => (char)x).ToArray();
    
    private static Config ModuleConfig = null!;

    private static LuminaSearcher<Item>? Searcher;
    
    private static string MessageInput    = string.Empty;
    private static int    DropMacroIndex  = -1;
    
    private static IconButtonNode? ImageButton;
    
    private static List<PanelTabBase> PanelTabs = [];

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        TaskHelper ??= new() { TimeLimitMS = 5_000 };
        
        Overlay       ??= new(this);
        Overlay.Flags |=  ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollWithMouse;
        Overlay.Flags &=  ~ImGuiWindowFlags.AlwaysAutoResize;
        Overlay.SizeConstraints = new()
        {
            MinimumSize = new(1, ModuleConfig.OverlayHeight),
        };
        
        if (ModuleConfig.SoundEffectNotes.Count <= 0)
        {
            for (var i = 1U; i < 17; i++)
                ModuleConfig.SoundEffectNotes[i] = $"<se.{i}>";
        }
        
        Searcher ??= new(LuminaGetter.Get<Item>(), [x => x.Name.ExtractText(), x => x.RowId.ToString()], x => x.Name.ExtractText());
        
        // 初始化 Panel Tabs
        PanelTabs =
        [
            new MessageTab(this),
            new MacroTab(this),
            new SystemSoundTab(this),
            new GameItemTab(this),
            new SpecialIconCharTab(this)
        ];

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "ChatLog", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "ChatLog", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "ChatLog", OnAddon);
    }

    protected override void ConfigUI()
    {
        using var table = ImRaii.Table("ConfigTable", 2, ImGuiTableFlags.SizingFixedFit);
        if (!table) return;

        ImGui.TableSetupColumn("Labels", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("Controls", ImGuiTableColumnFlags.WidthStretch);

        // Messages 行
        ImGui.TableNextRow();
        
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("QuickChatPanel-Messages")}:");
        
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        using (var combo = ImRaii.Combo("###MessagesCombo", GetLoc("QuickChatPanel-SavedMessagesAmountText", ModuleConfig.SavedMessages.Count)))
        {
            if (combo)
            {
                ImGui.InputText("###MessageToSaveInput", ref MessageInput, 1000);

                ImGui.SameLine();
                if (ImGuiOm.ButtonIcon("###MessagesInputAdd", FontAwesomeIcon.Plus))
                {
                    if (!ModuleConfig.SavedMessages.Contains(MessageInput))
                    {
                        ModuleConfig.SavedMessages.Add(MessageInput);
                        SaveConfig(ModuleConfig);
                    }
                }

                if (ModuleConfig.SavedMessages.Count > 0) 
                    ImGui.Separator();

                foreach (var message in ModuleConfig.SavedMessages.ToList())
                {
                    ImGuiOm.ButtonSelectable(message);

                    using var popup = ImRaii.ContextPopup($"{message}");
                    if (!popup) continue;
                    
                    if (ImGuiOm.ButtonSelectable(GetLoc("Delete")))
                        ModuleConfig.SavedMessages.Remove(message);
                }
            }
        }

        // Macro 行
        ImGui.TableNextRow();
        
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("QuickChatPanel-Macro")}:");
        
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        using (var combo = ImRaii.Combo("###MacroCombo", GetLoc("QuickChatPanel-SavedMacrosAmountText", ModuleConfig.SavedMacros.Count),
                                        ImGuiComboFlags.HeightLargest))
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
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("QuickChatPanel-SystemSound")}:");
        
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        using (var combo = ImRaii.Combo("###SoundEffectNoteEditCombo", string.Empty, ImGuiComboFlags.HeightLarge))
        {
            if (combo)
            {
                
                foreach (var seNote in ModuleConfig.SoundEffectNotes)
                {
                    using var id = ImRaii.PushId($"{seNote.Key}");
                    
                    ImGui.AlignTextToFramePadding();
                    ImGui.Text($"<se.{seNote.Key}>{(seNote.Key < 10 ? "  " : "")}");

                    ImGui.SameLine();
                    ImGui.Text("——>");

                    var note = seNote.Value;
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(200f * GlobalFontScale);
                    if (ImGui.InputText("###SENoteInput", ref note, 32))
                        ModuleConfig.SoundEffectNotes[seNote.Key] = note;

                    if (ImGui.IsItemDeactivatedAfterEdit())
                        SaveConfig(ModuleConfig);
                }
            }
        }

        // ButtonOffset 行
        ImGui.TableNextRow();
        
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("QuickChatPanel-ButtonOffset")}:");
        
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        ImGui.InputFloat2("###ButtonOffsetInput", ref ModuleConfig.ButtonOffset, format: "%.1f");
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(ModuleConfig);

        // ButtonIcon 行
        ImGui.TableNextRow();
        
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("QuickChatPanel-ButtonIcon")}:");
        
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        ImGui.InputInt("###ButtonIconInput", ref ModuleConfig.ButtonIcon);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            ModuleConfig.ButtonIcon = Math.Max(ModuleConfig.ButtonIcon, 1);
            SaveConfig(ModuleConfig);
        }
        
        // ButtonBackground 行
        ImGui.TableNextRow();
        
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("QuickChatPanel-ButtonBackgroundVisible")}:");
        
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        if (ImGui.Checkbox("###ButtonBackgroundVisibleInput", ref ModuleConfig.ButtonBackgroundVisible))
            SaveConfig(ModuleConfig);

        // FontScale 行
        ImGui.TableNextRow();
        
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("FontScale")}:");
        
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        ImGui.InputFloat("###FontScaleInput", ref ModuleConfig.FontScale, 0, 0, "%.1f");
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            ModuleConfig.FontScale = (float)Math.Clamp(ModuleConfig.FontScale, 0.1, 10f);
            SaveConfig(ModuleConfig);
        }

        // OverlayHeight 行
        ImGui.TableNextRow();
        
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("QuickChatPanel-OverlayHeight")}:");
        
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        ImGui.InputFloat("###OverlayHeightInput", ref ModuleConfig.OverlayHeight, 0, 0, "%.1f");
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            ModuleConfig.OverlayHeight = Math.Clamp(ModuleConfig.OverlayHeight, 100f, 10000f);
            SaveConfig(ModuleConfig);

            Overlay.SizeConstraints = new()
            {
                MinimumSize = new(1, ModuleConfig.OverlayHeight),
            };
        }

        // OverlayPosOffset 行
        ImGui.TableNextRow();
        
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("QuickChatPanel-OverlayPosOffset")}:");
        
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        ImGui.InputFloat2("###OverlayPosOffsetInput", ref ModuleConfig.OverlayOffset, format: "%.1f");
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(ModuleConfig);

        // OverlayMacroDisplayMode 行
        ImGui.TableNextRow();
        
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("QuickChatPanel-OverlayMacroDisplayMode")}:");
        
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        using (var combo = ImRaii.Combo("###OverlayMacroDisplayModeCombo", MacroDisplayModeLoc[ModuleConfig.OverlayMacroDisplayMode]))
        {
            if (combo)
            {
                foreach (MacroDisplayMode mode in Enum.GetValues(typeof(MacroDisplayMode)))
                {
                    if (ImGui.Selectable(MacroDisplayModeLoc[mode], mode == ModuleConfig.OverlayMacroDisplayMode))
                    {
                        ModuleConfig.OverlayMacroDisplayMode = mode;
                        SaveConfig(ModuleConfig);
                    }
                }
            }
        }
        
        return;

        void DrawMacroChild(bool isIndividual)
        {
            var childSize = new Vector2(200 * GlobalFontScale, 300 * GlobalFontScale);
            var module    = RaptureMacroModule.Instance();
            if (module == null) return;

            using var child = ImRaii.Child($"{(isIndividual ? "Individual" : "Shared")}MacroSelectChild", childSize);
            if (!child) return;

            ImGui.Text(GetLoc($"QuickChatPanel-{(isIndividual ? "Individual" : "Shared")}Macros"));
            ImGui.Separator();

            var span = isIndividual ? module->Individual : module->Shared;
            for (var i = 0; i < span.Length; i++)
            {
                var macro = span.GetPointer(i);
                if (macro == null) continue;

                var name = macro->Name.ExtractText();
                var icon = ImageHelper.GetGameIcon(macro->IconId);
                if (string.IsNullOrEmpty(name) || icon == null) continue;

                var currentSavedMacro = (*macro).ToSavedMacro();
                currentSavedMacro.Position = i;
                currentSavedMacro.Category = isIndividual ? 0U : 1U;
                
                using (ImRaii.PushId($"{currentSavedMacro.Category}-{currentSavedMacro.Position}"))
                {
                    if (ImGuiOm.SelectableImageWithText(icon.Handle, new(24), name,
                                                        ModuleConfig.SavedMacros.Contains(currentSavedMacro),
                                                        ImGuiSelectableFlags.DontClosePopups))
                    {
                        if (!ModuleConfig.SavedMacros.Remove(currentSavedMacro))
                        {
                            ModuleConfig.SavedMacros.Add(currentSavedMacro);
                            SaveConfig(ModuleConfig);
                        }
                    }
                    
                    if (!ModuleConfig.SavedMacros.Contains(currentSavedMacro)) continue;

                    using (var context = ImRaii.ContextPopupItem("Context"))
                    {
                        if (!context) continue;
                        
                        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("QuickChatPanel-LastUpdateTime")}:");

                        ImGui.SameLine();
                        ImGui.Text($"{ModuleConfig.SavedMacros.Find(x => x.Equals(currentSavedMacro))?.LastUpdateTime}");

                        ImGui.Separator();

                        if (ImGuiOm.SelectableTextCentered(GetLoc("Refresh")))
                        {
                            var currentIndex = ModuleConfig.SavedMacros.IndexOf(currentSavedMacro);
                            if (currentIndex != -1)
                            {
                                ModuleConfig.SavedMacros[currentIndex] = currentSavedMacro;
                                SaveConfig(ModuleConfig);
                            }
                        }
                    }
                }
            }
        }
    }

    protected override void OverlayPreDraw()
    {
        if (DService.ObjectTable.LocalPlayer == null ||
            ChatLog == null || !ChatLog->IsVisible ||
            ChatLog->GetNodeById(5) == null)
            Overlay.IsOpen = false;
    }

    protected override void OverlayUI()
    {
        if (DService.KeyState[VirtualKey.ESCAPE])
        {
            Overlay.IsOpen = false;
            return;
        }
        
        using var font = FontManager.GetUIFont(ModuleConfig.FontScale).Push();

        var itemSpacing = ImGui.GetStyle().ItemSpacing;
        
        var textInputNode = ChatLog->GetNodeById(5);
        var windowNode    = ChatLog->RootNode;
        var buttonPos     = new Vector2(windowNode->ScreenX + windowNode->Width, textInputNode->ScreenY)      + ModuleConfig.ButtonOffset;
        ImGui.SetWindowPos(buttonPos with { Y = buttonPos.Y - ImGui.GetWindowSize().Y - (3 * itemSpacing.Y) } + ModuleConfig.OverlayOffset);
        
        var isOpen = true;
        ImGui.SetNextWindowPos(new(ImGui.GetWindowPos().X, ImGui.GetWindowPos().Y + ImGui.GetWindowHeight() - itemSpacing.Y));
        ImGui.SetNextWindowSize(new(ImGui.GetWindowWidth(), 2 * ImGui.GetTextLineHeight()));

        if (ImGui.Begin("###QuickChatPanel-SendMessages", ref isOpen,
                        ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (ImGuiOm.SelectableTextCentered(GetLoc("QuickChatPanel-SendChatboxMessage")))
            {
                var inputNode = (AtkComponentNode*)ChatLog->GetNodeById(5);
                var textNode  = inputNode->Component->UldManager.SearchNodeById(16)->GetAsAtkTextNode();
                var text      = SeString.Parse(textNode->NodeText);
                if (!string.IsNullOrWhiteSpace(text.ExtractText()))
                {
                    ChatManager.SendMessage(text.Encode());

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

    private static void DrawOverlayContent()
    {
        using var tabBar = ImRaii.TabBar("###QuickChatPanel", ImGuiTabBarFlags.Reorderable);
        if (!tabBar) return;
        
        // 使用 PanelTabs 列表绘制所有 Tab
        foreach (var panelTab in PanelTabs)
        {
            using var item = ImRaii.TabItem(panelTab.TabName);
            if (item)
                panelTab.DrawTabContent();
            else if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(panelTab.Tooltip))
                ImGuiOm.TooltipHover(panelTab.Tooltip);
        }

        if (ImGui.TabItemButton($"{FontAwesomeIcon.Cog.ToIconString()}###OpenQuickChatPanelSettings"))
            ChatManager.SendMessage($"/pdr search {GetLoc("QuickChatPanelTitle")}");
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

                if (ImageButton == null)
                {
                    ImageButton = new()
                    {
                        Size      = new(textInputNode->Height),
                        IsVisible = true,
                        IsEnabled = true,
                        IconId    = (uint)ModuleConfig.ButtonIcon,
                        OnClick   = () => { Overlay.Toggle(); },
                        Tooltip   = Info.Title,
                        Position = new Vector2(textInputNode->Width - textInputNode->Height, 0) +
                                   ModuleConfig.ButtonOffset
                    };
                    
                    ImageButton?.AttachNode(textInputNode);
                }

                if (Throttler.Throttle("QuickChatPanel-UpdateButtonNodes"))
                {
                    ImageButton.IconId   = (uint)ModuleConfig.ButtonIcon;
                    ImageButton.Position = new Vector2(windowNode->Width - (2 * textInputNode->Height), 0) + ModuleConfig.ButtonOffset;
                    ImageButton.Size     = new(textInputNode->Height);

                    ImageButton.BackgroundNode.IsVisible = ModuleConfig.ButtonBackgroundVisible;
                }

                break;
            case AddonEvent.PreFinalize:
                ImageButton?.DetachNode();
                ImageButton = null;
                break;
        }
    }
    
    public void SwapMacros(int index1, int index2)
    {
        (ModuleConfig.SavedMacros[index1], ModuleConfig.SavedMacros[index2]) =
            (ModuleConfig.SavedMacros[index2], ModuleConfig.SavedMacros[index1]);

        TaskHelper.Abort();

        TaskHelper.DelayNext(500);
        TaskHelper.Enqueue(() => { SaveConfig(ModuleConfig); });
    }

    public void SwapMessages(int index1, int index2)
    {
        (ModuleConfig.SavedMessages[index1], ModuleConfig.SavedMessages[index2]) =
            (ModuleConfig.SavedMessages[index2], ModuleConfig.SavedMessages[index1]);

        TaskHelper.Abort();

        TaskHelper.DelayNext(500);
        TaskHelper.Enqueue(() => { SaveConfig(ModuleConfig); });
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);     
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
        
        Searcher = null;
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
        Buttons,
    }

    private class Config : ModuleConfiguration
    {
        public List<SavedMacro>         SavedMacros             = [];
        public List<string>             SavedMessages           = [];
        public Dictionary<uint, string> SoundEffectNotes        = [];
        public int                      ButtonIcon              = 46;
        public Vector2                  ButtonOffset            = new(0);
        public bool                     ButtonBackgroundVisible = true;
        public float                    FontScale               = 1.5f;
        public float                    OverlayHeight           = 350f * GlobalFontScale;
        public Vector2                  OverlayOffset           = new(0);
        public MacroDisplayMode         OverlayMacroDisplayMode = MacroDisplayMode.Buttons;
    }
    
    // 消息 Tab
    private class MessageTab(QuickChatPanel instance) : PanelTabBase(instance)
    {
        public override string TabName => GetLoc("QuickChatPanel-Messages");

        public override string Tooltip => GetLoc("QuickChatPanelTitle-DragHelp");

        public override void DrawTabContent()
        {
            var maxTextWidth = 300f * GlobalFontScale;
            using (var child = ImRaii.Child("MessagesChild", ImGui.GetContentRegionAvail(), false))
            {
                if (!child) return;
                
                for (var i = 0; i < ModuleConfig.SavedMessages.Count; i++)
                {
                    var message = ModuleConfig.SavedMessages[i];

                    var textWidth = ImGui.CalcTextSize(message).X;
                    maxTextWidth = Math.Max(textWidth + 64,    maxTextWidth);
                    maxTextWidth = Math.Max(300f * GlobalFontScale, maxTextWidth);

                    ImGuiOm.SelectableTextCentered(message);

                    if (ImGui.IsKeyDown(ImGuiKey.LeftShift))
                    {
                        if (ImGui.BeginDragDropSource())
                        {
                            if (ImGui.SetDragDropPayload("MessageReorder", [])) 
                                DropMacroIndex = i;
                            ImGui.TextColored(ImGuiColors.DalamudYellow, message);
                            ImGui.EndDragDropSource();
                        }

                        if (ImGui.BeginDragDropTarget())
                        {
                            if (DropMacroIndex                                          >= 0 ||
                                ImGui.AcceptDragDropPayload("MessageReorder").Handle != null)
                            {
                                Instance.SwapMessages(DropMacroIndex, i);
                                DropMacroIndex = -1;
                            }

                            ImGui.EndDragDropTarget();
                        }
                    }

                    if (ImGui.IsItemClicked(ImGuiMouseButton.Left)) 
                        ImGui.SetClipboardText(message);

                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) 
                        ChatManager.SendMessage(message);

                    ImGuiOm.TooltipHover(GetLoc("QuickChatPanel-SendMessageHelp"));

                    if (i != ModuleConfig.SavedMessages.Count - 1)
                        ImGui.Separator();
                }
            }

            SetWindowSize(Math.Max(350f * GlobalFontScale, maxTextWidth));
        }
    }
    
    // 宏 Tab
    private class MacroTab(QuickChatPanel instance) : PanelTabBase(instance)
    {
        public override string TabName => GetLoc("QuickChatPanel-Macro");
        
        public override string Tooltip => GetLoc("QuickChatPanelTitle-DragHelp");
        
        public override void DrawTabContent()
        {
            var maxTextWidth = 300f * GlobalFontScale;
            using (var child = ImRaii.Child("MacroChild", ImGui.GetContentRegionAvail(), false))
            {
                if (!child) return;
                
                using (ImRaii.Group())
                {
                    for (var i = 0; i < ModuleConfig.SavedMacros.Count; i++)
                    {
                        var macro = ModuleConfig.SavedMacros[i];

                        var name = macro.Name;
                        var icon = ImageHelper.GetGameIcon(macro.IconID);
                        if (string.IsNullOrEmpty(name) || icon == null) continue;

                        switch (ModuleConfig.OverlayMacroDisplayMode)
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
                                var textSize = ImGui.CalcTextSize("六个字也行吧");
                                var buttonSize = textSize with { Y = (textSize.Y * 2) + icon.Height };

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
                                    DropMacroIndex = i;
                                ImGui.TextColored(ImGuiColors.DalamudYellow, name);
                                ImGui.EndDragDropSource();
                            }

                            if (ImGui.BeginDragDropTarget())
                            {
                                if (DropMacroIndex >= 0 ||
                                    ImGui.AcceptDragDropPayload("MacroReorder").Handle != null)
                                {
                                    Instance.SwapMacros(DropMacroIndex, i);
                                    DropMacroIndex = -1;
                                }

                                ImGui.EndDragDropTarget();
                            }
                        }

                        switch (ModuleConfig.OverlayMacroDisplayMode)
                        {
                            case MacroDisplayMode.List:
                                if (i != ModuleConfig.SavedMacros.Count - 1)
                                    ImGui.Separator();

                                break;
                            case MacroDisplayMode.Buttons:
                                ImGui.SameLine();
                                if ((i + 1) % 5 == 0)     
                                    ImGui.Dummy(new(20 * ModuleConfig.FontScale)); 
                                break;
                        }
                    }
                }
                
                maxTextWidth = ImGui.GetItemRectSize().X;
            }

            SetWindowSize(Math.Max(350f * GlobalFontScale, maxTextWidth));
        }
    }
    
    // 系统音 Tab
    private class SystemSoundTab(QuickChatPanel instance) : PanelTabBase(instance)
    {
        public override string TabName => GetLoc("QuickChatPanel-SystemSound");
        
        public override void DrawTabContent()
        {
            var maxTextWidth = 300f * GlobalFontScale;
            using (var child = ImRaii.Child("SystemSoundChild"))
            {
                if (!child) return;
                
                using (ImRaii.Group())
                {
                    foreach (var seNote in ModuleConfig.SoundEffectNotes)
                    {
                        ImGuiOm.ButtonSelectable($"{seNote.Value}###PlaySound{seNote.Key}");

                        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                            UIGlobals.PlayChatSoundEffect(seNote.Key);

                        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                            ChatManager.SendMessage($"<se.{seNote.Key}><se.{seNote.Key}>");

                        ImGuiOm.TooltipHover(GetLoc("QuickChatPanel-SystemSoundHelp"));
                    }
                }
                
                maxTextWidth = ImGui.GetItemRectSize().X;
                maxTextWidth = Math.Max(300f * GlobalFontScale, maxTextWidth);
            }

            SetWindowSize(Math.Max(350f * GlobalFontScale, maxTextWidth));
        }
    }
    
    // 游戏物品 Tab
    private class GameItemTab(QuickChatPanel instance) : PanelTabBase(instance)
    {
        public override string TabName => GetLoc("QuickChatPanel-GameItems");
        
        private static string ItemSearchInput = string.Empty;
        
        public override void DrawTabContent()
        {
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputTextWithHint("###GameItemSearchInput", GetLoc("PleaseSearch"), ref ItemSearchInput, 128))
                Searcher.Search(ItemSearchInput);
            if (ImGui.IsItemDeactivatedAfterEdit())
                Searcher.Search(ItemSearchInput);
            
            var maxTextWidth = 300f * GlobalFontScale;
            using (var child = ImRaii.Child("GameItemChild", ImGui.GetContentRegionAvail(), false))
            {
                if (!child) return;

                ImGui.Separator();
                
                if (!string.IsNullOrWhiteSpace(ItemSearchInput))
                {
                    var longestText          = string.Empty;
                    var isConflictKeyHolding = IsConflictKeyPressed();
                    foreach (var data in Searcher.SearchResult)
                    {
                        if (!LuminaGetter.TryGetRow(data.RowId, out Item itemData)) continue;
                        if (!DService.Texture.TryGetFromGameIcon(new(itemData.Icon, isConflictKeyHolding), out var texture)) continue;
                        
                        var itemName = itemData.Name.ExtractText();
                        if (itemName.Length > longestText.Length)
                            longestText = itemName;

                        if (ImGuiOm.SelectableImageWithText(texture.GetWrapOrEmpty().Handle, ScaledVector2(24f), itemName, false))
                            Chat(new SeStringBuilder().AddItemLink(itemData.RowId, isConflictKeyHolding).Build());
                    }

                    maxTextWidth = ImGui.CalcTextSize(longestText).X;
                    maxTextWidth = Math.Max(350f * GlobalFontScale, maxTextWidth);
                }
            }

            SetWindowSize(Math.Max(350f * GlobalFontScale, maxTextWidth));
        }
    }
    
    // 特殊图标字符 Tab
    private class SpecialIconCharTab(QuickChatPanel instance) : PanelTabBase(instance)
    {
        public override string TabName => GetLoc("QuickChatPanel-SpecialIconChar");
        
        public override void DrawTabContent()
        {
            var maxTextWidth = 300f * GlobalFontScale;
            using (var child = ImRaii.Child("SeIconChild", ImGui.GetContentRegionAvail(), false))
            {
                if (!child) return;
                
                using (ImRaii.Group())
                {
                    for (var i = 0; i < SeIconChars.Length; i++)
                    {
                        var icon = SeIconChars[i];

                        if (ImGui.Button($"{icon}", new(96 * ModuleConfig.FontScale)))
                            ImGui.SetClipboardText(icon.ToString());

                        ImGuiOm.TooltipHover($"0x{(int)icon:X4}");

                        ImGui.SameLine();
                        if ((i + 1) % 7 == 0)     
                            ImGui.Dummy(new(20 * ModuleConfig.FontScale));
                    }
                }

                maxTextWidth = ImGui.GetItemRectSize().X;
                maxTextWidth = Math.Max(300f * GlobalFontScale, maxTextWidth);
            }

            SetWindowSize(Math.Max(350f * GlobalFontScale, maxTextWidth));
        }
    }
    
    // Panel Tab 基类
    private abstract class PanelTabBase(QuickChatPanel instance)
    {
        protected QuickChatPanel Instance { get; } = instance;

        public abstract string TabName { get; }
        
        public virtual string Tooltip { get; } = string.Empty;
        
        public abstract void DrawTabContent();
        
        protected static void SetWindowSize(float maxTextWidth) => 
            ImGui.SetWindowSize(new(Math.Max(350f * GlobalFontScale, maxTextWidth), ModuleConfig.OverlayHeight * GlobalFontScale));
    }
}

public static class QuickChatPanelExtensions
{
    public static QuickChatPanel.SavedMacro ToSavedMacro(this RaptureMacroModule.Macro macro)
    {
        var savedMacro = new QuickChatPanel.SavedMacro
        {
            Name           = macro.Name.ExtractText(),
            IconID         = macro.IconId,
            LastUpdateTime = DateTime.Now
        };

        return savedMacro;
    }
}
