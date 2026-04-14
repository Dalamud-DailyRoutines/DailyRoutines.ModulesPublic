using System.Collections;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Component.GUI;
using InteropGenerator.Runtime;
using OmenTools.Interop.Game.Models;

namespace DailyRoutines.ModulesPublic;

public unsafe class CustomizeInterfaceText : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("CustomizeInterfaceTextTitle"),
        Description = Lang.Get("CustomizeInterfaceTextDescription"),
        Category    = ModuleCategory.UIOptimization
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };
    
    private static readonly CompSig SetPlayerNamePlateSig = new("48 89 5C 24 ?? 55 56 57 41 54 41 55 41 56 41 57 48 83 EC ?? 44 0F B6 EA");
    private delegate nint SetPlayerNamePlateDelegate
    (
        nint namePlateObjectPtr,
        bool isPrefixTitle,
        bool displayTitle,
        nint titlePtr,
        nint namePtr,
        nint fcNamePtr,
        nint prefix,
        int  iconID
    );
    private Hook<SetPlayerNamePlateDelegate>? SetPlayerNamePlateHook;

    private static readonly CompSig TextNodeSetStringSig = 
        new("E8 ?? ?? ?? ?? 48 83 C4 ?? 5B C3 CC CC CC CC CC CC CC CC CC CC 40 55 56 57 48 81 EC");
    private delegate        void    TextNodeSetStringDelegate(AtkTextNode* textNode, CStringPointer text);
    private Hook<TextNodeSetStringDelegate>? TextNodeSetStringHook;

    private Config config = null!;

    private string keyInput   = string.Empty;
    private string valueInput = string.Empty;
    private int    replaceModeInput;

    private string keyEditInput   = string.Empty;
    private string valueEditInput = string.Empty;
    private int    replaceModeEditInput;
    
    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        TextNodeSetStringHook ??= TextNodeSetStringSig.GetHook<TextNodeSetStringDelegate>(TextNodeSetStringDetour);
        TextNodeSetStringHook.Enable();

        SetPlayerNamePlateHook ??= SetPlayerNamePlateSig.GetHook<SetPlayerNamePlateDelegate>(SetPlayerNamePlayerDetour);
        SetPlayerNamePlateHook.Enable();
    }

    protected override void ConfigUI()
    {
        using (ImRaii.Group())
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted($"{Lang.Get("Key")}:");

            ImGui.SameLine();
            ImGui.SetNextItemWidth(300f * GlobalUIScale);
            ImGui.InputText("###KeyInput", ref keyInput, 96);

            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted($"{Lang.Get("Value")}:");

            ImGui.SameLine();
            ImGui.SetNextItemWidth(300f * GlobalUIScale);
            ImGui.InputText("###ValueInput", ref valueInput, 96);

            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted($"{Lang.Get("CustomizeInterfaceText-ReplaceMode")}:");

            foreach (var replaceMode in Enum.GetValues<ReplaceMode>())
            {
                ImGui.SameLine();
                ImGui.RadioButton(replaceMode.ToString(), ref replaceModeInput, (int)replaceMode);
            }
        }

        ImGui.SameLine();

        if (ImGuiOm.ButtonIconWithTextVertical(FontAwesomeIcon.Plus, Lang.Get("Add")) && !string.IsNullOrWhiteSpace(keyInput))
        {
            var pattern = new ReplacePattern(keyInput, valueInput, (ReplaceMode)replaceModeInput, true);
            if (replaceModeEditInput == (int)ReplaceMode.正则)
                pattern.Regex = new Regex(pattern.Key, RegexOptions.Compiled);

            if (!config.ReplacePatterns.Contains(pattern))
            {
                config.ReplacePatterns.Add(pattern);
                keyInput = valueInput = string.Empty;

                config.Save(this);
            }
        }

        ImGui.NewLine();

        using (var table = ImRaii.Table("###CustomizeInterfaceTextTable", 4, ImGuiTableFlags.Borders))
        {
            if (table)
            {
                ImGui.TableSetupColumn("启用",   ImGuiTableColumnFlags.WidthFixed,   20 * GlobalUIScale);
                ImGui.TableSetupColumn("键",    ImGuiTableColumnFlags.WidthStretch, 50);
                ImGui.TableSetupColumn("值",    ImGuiTableColumnFlags.WidthStretch, 50);
                ImGui.TableSetupColumn("匹配模式", ImGuiTableColumnFlags.WidthStretch, 15);

                ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.Empty);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(Lang.Get("Key"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(Lang.Get("Value"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(Lang.Get("CustomizeInterfaceText-ReplaceMode"));

                var array = config.ReplacePatterns.ToArray();

                for (var i = 0; i < config.ReplacePatterns.Count; i++)
                {
                    var replacePattern = array[i];
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    var enabled = replacePattern.Enabled;

                    if (ImGui.Checkbox($"###{i}_IsEnabled", ref enabled))
                    {
                        config.ReplacePatterns[i].Enabled = enabled;
                        config.Save(this);
                    }

                    ImGui.TableNextColumn();
                    ImGui.Selectable(replacePattern.Key, false, ImGuiSelectableFlags.DontClosePopups);

                    using (var context = ImRaii.ContextPopupItem($"{replacePattern.Key}_KeyEdit"))
                    {
                        if (context)
                        {
                            if (ImGui.IsWindowAppearing())
                                keyEditInput = replacePattern.Key;

                            ImGui.AlignTextToFramePadding();
                            ImGui.TextUnformatted($"{Lang.Get("Key")}:");

                            ImGui.SameLine();
                            ImGui.SetNextItemWidth(300f * GlobalUIScale);
                            ImGui.InputText("###KeyEditInput", ref keyEditInput, 96);

                            if (ImGui.IsItemDeactivatedAfterEdit() && !string.IsNullOrWhiteSpace(keyEditInput))
                            {
                                var pattern = new ReplacePattern(keyEditInput, "", 0, replacePattern.Enabled);

                                if (!config.ReplacePatterns.Contains(pattern))
                                {
                                    config.ReplacePatterns[i].Key = keyEditInput;
                                    if (replacePattern.Mode is ReplaceMode.正则)
                                        config.ReplacePatterns[i].Regex = new Regex(keyEditInput);

                                    config.Save(this);
                                }
                            }

                            ImGui.SameLine();

                            if (ImGui.Button(Lang.Get("Delete")))
                            {
                                if (config.ReplacePatterns.Remove(replacePattern))
                                    config.Save(this);
                            }
                        }
                    }

                    ImGui.TableNextColumn();
                    ImGui.Selectable(replacePattern.Value, false, ImGuiSelectableFlags.DontClosePopups);

                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                        valueEditInput = replacePattern.Value;

                    using (var context = ImRaii.ContextPopupItem($"{replacePattern.Key}_ValueEdit"))
                    {
                        if (context)
                        {
                            ImGui.AlignTextToFramePadding();
                            ImGui.TextUnformatted($"{Lang.Get("Value")}:");

                            ImGui.SameLine();
                            ImGui.SetNextItemWidth(300f * GlobalUIScale);
                            ImGui.InputText("###ValueEditInput", ref valueEditInput, 96);

                            if (ImGui.IsItemDeactivatedAfterEdit())
                            {
                                config.ReplacePatterns[i].Value = valueEditInput;
                                config.Save(this);
                            }
                        }
                    }

                    ImGui.TableNextColumn();
                    ImGui.Selectable(replacePattern.Mode.ToString(), false, ImGuiSelectableFlags.DontClosePopups);

                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                        replaceModeEditInput = (int)replacePattern.Mode;

                    using (var context = ImRaii.ContextPopupItem($"{replacePattern.Key}_ModeEdit"))
                    {
                        if (context)
                        {
                            ImGui.AlignTextToFramePadding();
                            ImGui.TextUnformatted($"{Lang.Get("CustomizeInterfaceText-ReplaceMode")}:");

                            foreach (var replaceMode in Enum.GetValues<ReplaceMode>())
                            {
                                ImGui.SameLine();
                                ImGui.RadioButton(replaceMode.ToString(), ref replaceModeEditInput, (int)replaceMode);

                                if (ImGui.IsItemDeactivatedAfterEdit())
                                {
                                    config.ReplacePatterns[i].Mode = (ReplaceMode)replaceModeEditInput;
                                    if ((ReplaceMode)replaceModeEditInput is ReplaceMode.正则)
                                        config.ReplacePatterns[i].Regex = new Regex(replacePattern.Key);

                                    config.Save(this);
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    private void TextNodeSetStringDetour(AtkTextNode* textNode, CStringPointer text)
    {
        if (textNode == null || !text.HasValue)
        {
            TextNodeSetStringHook.Original(textNode, text);
            return;
        }

        var origText = SeString.Parse(text.Value);

        if (origText.Payloads.Count == 0)
        {
            TextNodeSetStringHook.Original(textNode, text);
            return;
        }

        if (ApplyTextReplacements(origText, out var modifiedText))
        {
            var pinnedArray = GCHandle.Alloc(modifiedText, GCHandleType.Pinned);

            try
            {
                TextNodeSetStringHook.Original(textNode, new((byte*)pinnedArray.AddrOfPinnedObject()));
            }
            finally
            {
                pinnedArray.Free();
            }
        }
        else
            TextNodeSetStringHook.Original(textNode, text);
    }

    private nint SetPlayerNamePlayerDetour
    (
        nint namePlateObjectPtr,
        bool isPrefixTitle,
        bool displayTitle,
        nint titlePtr,
        nint namePtr,
        nint fcNamePtr,
        nint prefix,
        int  iconID
    )
    {
        using var nameMemory   = ReplaceTextAndAllocate(namePtr);
        using var titleMemory  = ReplaceTextAndAllocate(titlePtr);
        using var fcNameMemory = ReplaceTextAndAllocate(fcNamePtr);

        return SetPlayerNamePlateHook!.Original
        (
            namePlateObjectPtr,
            isPrefixTitle,
            displayTitle,
            titleMemory.Pointer,
            nameMemory.Pointer,
            fcNameMemory.Pointer,
            prefix,
            iconID
        );
    }

    private PinnedMemory ReplaceTextAndAllocate(nint originalTextPtr)
    {
        var origText = MemoryHelper.ReadSeStringNullTerminated(originalTextPtr);
        return ApplyTextReplacements(origText, out var modifiedText)
                   ? new PinnedMemory(modifiedText)
                   : new PinnedMemory(Array.Empty<byte>());
    }

    private bool ApplyTextReplacements(SeString origText, out byte[]? modifiedText)
    {
        modifiedText = null;
        var textPayloads = origText.Payloads.OfType<TextPayload>().ToArray();

        var modified = false;

        foreach (var pattern in config.ReplacePatterns)
        {
            if (!pattern.Enabled) continue;

            foreach (var payload in textPayloads)
            {
                var originalText = payload.Text;
                var replacedText = pattern.Mode switch
                {
                    ReplaceMode.部分匹配 => originalText.Contains(pattern.Key, StringComparison.Ordinal)
                                            ? originalText.Replace(pattern.Key, pattern.Value)
                                            : null,
                    ReplaceMode.完全匹配 => originalText.Equals(pattern.Key, StringComparison.Ordinal)
                                            ? pattern.Value
                                            : null,
                    ReplaceMode.正则 => pattern.Regex?.Replace(originalText, pattern.Value),
                    _              => null
                };

                if (replacedText != null)
                {
                    payload.Text = replacedText;
                    modified     = true;
                }
            }
        }

        if (modified)
        {
            modifiedText = origText.Encode();
            return true;
        }

        return false;
    }

    private class Config : ModuleConfig
    {
        public List<ReplacePattern> ReplacePatterns = [];
    }

    private enum ReplaceMode
    {
        部分匹配,
        完全匹配,
        正则
    }

    private class ReplacePattern : IComparable<ReplacePattern>, IEquatable<ReplacePattern>
    {
        public ReplacePattern() { }

        public ReplacePattern(string key, string value, ReplaceMode mode, bool enabled)
        {
            Key     = key;
            Value   = value;
            Mode    = mode;
            Enabled = enabled;

            if (mode == ReplaceMode.正则)
                Regex = new Regex(key, RegexOptions.Compiled);
        }

        public string      Key     { get; set; } = string.Empty;
        public string      Value   { get; set; } = string.Empty;
        public ReplaceMode Mode    { get; set; }
        public bool        Enabled { get; set; }
        public Regex?      Regex   { get; set; }

        public int CompareTo(ReplacePattern? other) =>
            other == null ? 1 : string.Compare(Key, other.Key, StringComparison.Ordinal);

        public bool Equals(ReplacePattern? other) => other != null && Key == other.Key;

        public override bool Equals(object? obj) => Equals(obj as ReplacePattern);

        public override int GetHashCode() => Key.GetHashCode(StringComparison.Ordinal);

        public void Deconstruct(out string key, out string value, out ReplaceMode mode, out bool enabled) =>
            (key, value, mode, enabled) = (Key, Value, Mode, Enabled);
    }

    private struct PinnedMemory : IDisposable
    {
        public readonly nint     Pointer;
        private         GCHandle handle;

        public PinnedMemory(IEnumerable array)
        {
            handle  = GCHandle.Alloc(array, GCHandleType.Pinned);
            Pointer = handle.AddrOfPinnedObject();
        }

        public void Dispose() => handle.Free();
    }
}
