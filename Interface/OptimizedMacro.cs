using System.Text;
using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.Info;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using InteropGenerator.Runtime;
using KamiToolKit.Controllers;
using KamiToolKit.Nodes;
using OmenTools.Interop.Game.Lumina;
using OmenTools.KamiToolKit.Addons.InputString;
using OmenTools.KamiToolKit.Addons.SelectYesno;
using OmenTools.KamiToolKit.Nodes;
using OmenTools.KamiToolKit.Nodes.CircleButton;
using OmenTools.OmenService;
using ExpansionAtkResNode = FFXIVClientStructs.Expansion.FFXIV.Component.GUI.AtkResNode;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedMacro : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("OptimizedMacroTitle"),
        Description = Lang.Get("OptimizedMacroDescription"),
        Category    = ModuleCategory.Interface,
        Author      = ["Rorinnn"]
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private Config config = null!;

    private delegate InputCallbackResult InputCallbackDelegate
    (
        AtkUnitBase*      addon,
        InputCallbackType type,
        CStringPointer    rawString,
        CStringPointer    evaluatedString,
        int               eventKind
    );
    private Hook<InputCallbackDelegate>? InputCallbackHook;

    private AddonController? macroController;

    private HorizontalListNode? controlListNode;
    private StringDropDownNode? presetDropdownNode;

    private CircleButtonNode? loadButtonNode;
    private CircleButtonNode? saveButtonNode;
    private CircleButtonNode? deleteButtonNode;

    private TextMultiLineInputNode? lineNumberContainerNode;
    private int?                    lastLineCount;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        CommandManager.Instance().AddSubCommand
        (
            COMMAND,
            new(OnCommand)
            {
                HelpMessage = Lang.Get("OptimizedMacro-CommandHelp")
            }
        );

        macroController = new()
        {
            AddonName  = "Macro",
            OnSetup    = OnAddonSetup,
            OnRefresh  = OnAddonRefresh,
            OnFinalize = OnAddonFinalize
        };
        macroController.Enable();
    }

    protected override void Uninit()
    {
        CommandManager.Instance().RemoveSubCommand(COMMAND);

        macroController?.Dispose();
        macroController = null;

        OnAddonFinalize(null);
    }

    protected override void ConfigUI()
    {
        using (ImRaii.Heading1(Lang.Get("Command")))
            ImGui.TextUnformatted($"/pdr {COMMAND} {Lang.Get("OptimizedMacro-CommandHelp")}");
    }

    #region Event Handlers

    private void OnCommand
    (
        string command,
        string args
    )
    {
        if (string.IsNullOrWhiteSpace(args)) return;
        LoadPreset(args);
    }

    private InputCallbackResult InputCallbackDetour
    (
        AtkUnitBase*      addon,
        InputCallbackType type,
        CStringPointer    rawString,
        CStringPointer    evaluatedString,
        int               eventKind
    )
    {
        var orig = InputCallbackHook.Original(addon, type, rawString, evaluatedString, eventKind);
        if (type is InputCallbackType.TextChanged)
            OnAddonRefresh(addon);
        return orig;
    }

    private void OnAddonSetup
    (
        AtkUnitBase* addon
    )
    {
        if (addon == null) return;

        var nodeMacroIndexLabel = addon->GetNodeById(115);

        if (nodeMacroIndexLabel != null)
        {
            nodeMacroIndexLabel->X = 440;
            nodeMacroIndexLabel->Y = 40;
        }

        var nodeMacroIndex = addon->GetNodeById(116);

        if (nodeMacroIndex != null)
        {
            nodeMacroIndex->X = 515;
            nodeMacroIndex->Y = 40;
        }

        var nodeMacroCount = addon->GetNodeById(117);

        if (nodeMacroCount != null)
        {
            nodeMacroCount->X = 372;
            nodeMacroCount->Y = 522;
        }

        var nodeMacroInput = (AtkComponentTextInput*)addon->GetComponentByNodeId(119);

        if (nodeMacroInput != null)
        {
            var ownerNode = nodeMacroInput->OwnerNode;
            var oldWidth  = ownerNode->Width;
            var oldHeight = ownerNode->Height;

            ownerNode->SetXFloat(478);
            ownerNode->SetWidth(302);

            ((ExpansionAtkResNode*)ownerNode)->ApplyResizeAnchors(oldWidth, oldHeight, ownerNode->Width, ownerNode->Height, false);

            InputCallbackHook?.Dispose();
            InputCallbackHook = IGameInteropProvider.Instance().HookFromAddress
            (
                (nint)nodeMacroInput->Callback,
                (InputCallbackDelegate)InputCallbackDetour
            );
            InputCallbackHook.Enable();
        }

        lineNumberContainerNode = new()
        {
            Position  = new(457, 114),
            Size      = new(32, 400),
            IsVisible = false
        };
        lineNumberContainerNode.BackgroundNode.IsVisible = false;
        var textNode = lineNumberContainerNode.CurrentTextNode;
        textNode.RemoveTextFlags(TextFlags.OverflowHidden);
        textNode.RemoveTextFlags(TextFlags.WordWrap);
        textNode.AlignmentType = AlignmentType.TopRight;
        textNode.FontType      = FontType.MiedingerMed;
        AtkColors.ListFooter.ApplyTo(textNode);

        lineNumberContainerNode.AttachNode(addon);

        lastLineCount = null;

        controlListNode = new()
        {
            Height            = 28,
            Position          = new(10, 517),
            FitToContentWidth = true
        };
        controlListNode.AttachNode(addon);

        presetDropdownNode = new StringDropDownNode
        {
            Size           = new(200, controlListNode.Height),
            MaxListOptions = 10,
            Options        = GetPresetNames(),
            SelectedOption = DEFAULT_OPTION,
            OnOptionSelected = selection =>
            {
                var isDefaultOption = selection == DEFAULT_OPTION;

                loadButtonNode.IsEnabled   = !isDefaultOption;
                deleteButtonNode.IsEnabled = !isDefaultOption;

                saveButtonNode.Icon = isDefaultOption ?
                                          CircleButtonIcon.Add :
                                          CircleButtonIcon.Edit;
                saveButtonNode.TextTooltip = LuminaWrapper.GetAddonText
                (
                    isDefaultOption ?
                        552U : // 删除
                        346    // 更新
                );
            }
        };
        controlListNode.AddNode(presetDropdownNode);

        loadButtonNode = new()
        {
            Size        = new(controlListNode.Height),
            Icon        = CircleButtonIcon.Document,
            TextTooltip = LuminaWrapper.GetAddonText(6140), // 读取
            OnClick = () =>
            {
                var selectedPreset = presetDropdownNode.SelectedOption;
                if (string.IsNullOrEmpty(selectedPreset) || selectedPreset == DEFAULT_OPTION)
                    return;

                LoadPreset(selectedPreset);
            },
            IsEnabled = false
        };
        controlListNode.AddNode(loadButtonNode);

        deleteButtonNode = new()
        {
            Size        = new(controlListNode.Height),
            Icon        = CircleButtonIcon.Cross,
            TextTooltip = LuminaWrapper.GetAddonText(68), // 删除
            OnClick = () =>
            {
                var selectedPreset = presetDropdownNode.SelectedOption;
                if (string.IsNullOrEmpty(selectedPreset) || selectedPreset == DEFAULT_OPTION)
                    return;

                SelectYesnoAddon.Open
                (
                    new()
                    {
                        ParentID        = addon->Id,
                        BlockedParentID = addon->Id,
                        Prompt = Lang.Get
                        (
                            "OptimizedMacro-Dialogue-Delete",
                            new Dictionary<string, object>
                            {
                                ["presetName"] = selectedPreset
                            }
                        ),
                        Callback = (_, result) =>
                        {
                            if (result != SelectYesnoAddonResult.Yes)
                                return;

                            DeletePreset(selectedPreset);
                            presetDropdownNode.Options        = GetPresetNames();
                            presetDropdownNode.SelectedOption = DEFAULT_OPTION;
                        }
                    }
                );
            },
            IsEnabled = false
        };
        controlListNode.AddNode(deleteButtonNode);

        saveButtonNode = new()
        {
            Size        = new(controlListNode.Height),
            Icon        = CircleButtonIcon.Add,
            TextTooltip = LuminaWrapper.GetAddonText(552), // 删除
            OnClick = () =>
            {
                if (presetDropdownNode.SelectedOption is var selectedOption &&
                    selectedOption != DEFAULT_OPTION                        &&
                    !string.IsNullOrWhiteSpace(selectedOption))
                {
                    InputStringAddon.Open
                    (
                        new()
                        {
                            ParentID        = addon->Id,
                            BlockedParentID = addon->Id,
                            DefaultText     = selectedOption,
                            Prompt          = Lang.Get("OptimizedMacro-Dialogue-UpdateInput"),
                            Callback = (_, result) =>
                            {
                                if (result.Result != InputStringAddonResult.Confirmed)
                                    return;

                                var input = result.Value.ToString();
                                SavePreset(selectedOption, true, input);
                                presetDropdownNode.Options = GetPresetNames();
                            }

                        }
                    );
                }
                else
                {
                    InputStringAddon.Open
                    (
                        new()
                        {
                            ParentID        = addon->Id,
                            BlockedParentID = addon->Id,
                            Prompt          = Lang.Get("OptimizedMacro-Dialogue-SaveInput"),
                            Callback = (_, result) =>
                            {
                                if (result.Result != InputStringAddonResult.Confirmed)
                                    return;

                                var input = result.Value.ToString();
                                SavePreset(input);
                                presetDropdownNode.Options = GetPresetNames();
                            }

                        }
                    );
                }
            }
        };
        controlListNode.AddNode(saveButtonNode);
    }

    private void OnAddonFinalize
    (
        AtkUnitBase* addon
    )
    {
        presetDropdownNode?.Dispose();
        presetDropdownNode = null;

        loadButtonNode?.Dispose();
        loadButtonNode = null;

        saveButtonNode?.Dispose();
        saveButtonNode = null;

        deleteButtonNode?.Dispose();
        deleteButtonNode = null;

        controlListNode?.Dispose();
        controlListNode = null;

        lineNumberContainerNode?.Dispose();
        lineNumberContainerNode = null;
        lastLineCount           = null;

        InputCallbackHook?.Dispose();
        InputCallbackHook = null;
    }

    private void OnAddonRefresh
    (
        AtkUnitBase* addon
    )
    {
        if (addon == null)
            return;
        var nodeMacroInput = (AtkComponentTextInput*)addon->GetComponentByNodeId(119);
        if (nodeMacroInput == null)
            return;
        if (lineNumberContainerNode == null)
            return;

        var macro = nodeMacroInput->EvaluatedString.ToString();
        var lines = string.IsNullOrWhiteSpace(macro) ?
                        0 :
                        macro.Split('\r').Length;

        if (lines == lastLineCount)
            return;

        lastLineCount = lines;

        if (lines == 0)
        {
            lineNumberContainerNode.IsVisible = false;
            return;
        }

        var builder = new StringBuilder();
        for (var i = 0; i < lines; i++)
            builder.AppendLine($"{i + 1}");
        lineNumberContainerNode.String    = builder.ToString().Trim();
        lineNumberContainerNode.IsVisible = true;
    }

    #endregion

    #region Preset Management

    private void SavePreset
    (
        string  presetName,
        bool    isUpdate          = false,
        string? updatedPresetName = null
    )
    {
        var macroModule = RaptureMacroModule.Instance();
        if (macroModule == null) return;

        if (string.IsNullOrWhiteSpace(presetName))
        {
            var saveEmptyError = Lang.Get("OptimizedMacro-Notification-SaveError-Empty");
            NotifyHelper.ToastError(saveEmptyError);
            return;
        }

        var        createdAt = StandardTimeManager.Instance().Now;
        PresetData presetData;

        if (isUpdate)
        {
            if (string.IsNullOrWhiteSpace(updatedPresetName))
            {
                var emptyError = Lang.Get("OptimizedMacro-Notification-UpdateError-Empty");
                NotifyHelper.ToastError(emptyError);
                return;
            }

            if (presetName == updatedPresetName)
            {
                var sameAsBeforeError = Lang.Get("OptimizedMacro-Notification-UpdateError-SameAsBefore");
                NotifyHelper.ToastError(sameAsBeforeError);
                return;
            }

            if (!config.Presets.TryGetValue(presetName, out var exsited))
            {
                var notFoundError = Lang.Get("OptimizedMacro-Notification-UpdateError-NotFound");
                NotifyHelper.ToastError(notFoundError);
                return;
            }

            if (config.Presets.ContainsKey(updatedPresetName))
            {
                var existedError = Lang.Get("OptimizedMacro-Notification-UpdateError-Existed");
                NotifyHelper.ToastError(existedError);
                return;
            }

            presetData = new PresetData
            {
                CreatedAt        = exsited.CreatedAt,
                IndividualMacros = [.. exsited.IndividualMacros],
                SharedMacros     = [.. exsited.SharedMacros]
            };
            createdAt = exsited.CreatedAt;
        }
        else
        {
            presetData = new PresetData
            {
                CreatedAt        = createdAt,
                IndividualMacros = ReadMacrosFromMemory(macroModule, 0),
                SharedMacros     = ReadMacrosFromMemory(macroModule, 1)
            };
        }

        if (isUpdate)
        {
            config.Presets.Remove(presetName);
            config.Presets[updatedPresetName] = presetData;
        }
        else
            config.Presets[presetName] = presetData;

        config.Save(this);

        var message = string.Empty;

        if (isUpdate)
        {
            message = Lang.Get
            (
                "OptimizedMacro-Notification-Updated",
                new Dictionary<string, object>
                {
                    ["beforeName"] = presetName,
                    ["afterName"]  = updatedPresetName
                }
            );
        }
        else
        {
            message = Lang.Get
            (
                "OptimizedMacro-Notification-Saved",
                new Dictionary<string, object>
                {
                    ["presetName"] = presetName
                }
            );
        }

        NotifyHelper.Toast(message);
        NotifyHelper.Instance().Chat(message);
    }

    private void LoadPreset
    (
        string presetName
    )
    {
        var macroModule = RaptureMacroModule.Instance();
        if (macroModule == null)
            return;

        var hotbarModule = RaptureHotbarModule.Instance();
        if (hotbarModule == null)
            return;

        if (presetName == DEFAULT_OPTION)
            return;

        if (string.IsNullOrWhiteSpace(presetName) ||
            !config.Presets.TryGetValue(presetName, out var presetData))
        {
            NotifyHelper.ToastError(Lang.Get("OptimizedMacro-Notification-Load-NotFound"));
            return;
        }

        WriteMacrosToMemory(macroModule, 0, presetData.IndividualMacros);
        WriteMacrosToMemory(macroModule, 1, presetData.SharedMacros);

        macroModule->SetSavePendingFlag(true, 0);
        macroModule->SetSavePendingFlag(true, 1);
        hotbarModule->ReloadAllMacroSlots();

        var message = Lang.Get
        (
            "OptimizedMacro-Notification-Loaded",
            new Dictionary<string, object>
            {
                ["presetName"] = presetName
            }
        );

        NotifyHelper.Toast(message);
        NotifyHelper.Instance().Chat(message);
    }

    private void DeletePreset
    (
        string presetName
    )
    {
        if (presetName == DEFAULT_OPTION)
            return;

        if (string.IsNullOrWhiteSpace(presetName) ||
            !config.Presets.Remove(presetName))
        {
            NotifyHelper.ToastError(Lang.Get("OptimizedMacro-Notification-Delete-NotFound"));
            return;
        }

        config.Save(this);

        var message = Lang.Get
        (
            "OptimizedMacro-Notification-Deleted",
            new Dictionary<string, object>
            {
                ["presetName"] = presetName
            }
        );
        NotifyHelper.Instance().Chat(message);
        NotifyHelper.Toast(message);
    }

    private static List<MacroData> ReadMacrosFromMemory
    (
        RaptureMacroModule* macroModule,
        uint                set
    )
    {
        List<MacroData> macros = [];

        for (uint i = 0; i < MACROS_PER_SET; i++)
        {
            var macro = macroModule->GetMacro(set, i);
            if (macro == null) continue;

            var nameSpan   = macro->Name.AsSpan();
            var hasContent = false;

            for (var lineIdx = 0; lineIdx < MAX_MACRO_LINES; lineIdx++)
                if (macro->Lines[lineIdx].AsSpan().Length > 0)
                {
                    hasContent = true;
                    break;
                }

            // 跳过完全为空的宏
            if (nameSpan.Length == 0 && macro->IconId == 0 && !hasContent)
                continue;

            var macroData = new MacroData
            {
                Index  = i,
                IconID = macro->IconId,
                Name = nameSpan.Length > 0 ?
                           [.. nameSpan, 0] :
                           null
            };

            for (var lineIndex = 0; lineIndex < MAX_MACRO_LINES; lineIndex++)
            {
                var lineSpan = macro->Lines[lineIndex].AsSpan();
                if (lineSpan.Length > 0)
                    macroData.Lines[lineIndex] = [.. lineSpan, 0];
            }

            macros.Add(macroData);
        }

        return macros;
    }

    private static void WriteMacrosToMemory
    (
        RaptureMacroModule* macroModule,
        uint                set,
        List<MacroData>     macrosData
    )
    {
        for (uint i = 0; i < MACROS_PER_SET; i++)
        {
            var macro = macroModule->GetMacro(set, i);
            if (macro == null) continue;

            macro->Clear();
            macro->SetIcon(0);
        }

        foreach (var data in macrosData)
        {
            if (data.Index >= MACROS_PER_SET) continue;

            var macro = macroModule->GetMacro(set, data.Index);
            if (macro == null) continue;

            macro->SetIcon(data.IconID);

            if (data.Name != null)
                macro->Name.SetString(data.Name);

            foreach (var (lineIdx, lineData) in data.Lines)
            {
                if (lineIdx is >= 0 and < MAX_MACRO_LINES && lineData.Length > 0)
                    macro->Lines[lineIdx].SetString(lineData);
            }
        }
    }

    #endregion

    #region Models

    private class MacroData
    {
        public uint                    Index  { get; set; }
        public uint                    IconID { get; set; }
        public byte[]?                 Name   { get; set; }
        public Dictionary<int, byte[]> Lines  { get; set; } = [];
    }

    private class PresetData
    {
        public DateTime        CreatedAt        { get; set; } = StandardTimeManager.Instance().Now;
        public List<MacroData> IndividualMacros { get; set; } = [];
        public List<MacroData> SharedMacros     { get; set; } = [];
    }

    #endregion

    #region Tools

    private List<string> GetPresetNames()
    {
        var sortedList = config.Presets
                               .OrderByDescending(x => x.Value.CreatedAt)
                               .Select(x => x.Key)
                               .ToList();

        return [.. sortedList.Prepend(DEFAULT_OPTION)];
    }

    #endregion

    private class Config : ModuleConfig
    {
        public Dictionary<string, PresetData> Presets = [];
    }

    #region 常量

    private const int MACROS_PER_SET  = 100;
    private const int MAX_MACRO_LINES = 15;

    private const string COMMAND = "macroset";

    private static string DEFAULT_OPTION = LuminaWrapper.GetAddonText(7027); // 未选择

    #endregion
}
