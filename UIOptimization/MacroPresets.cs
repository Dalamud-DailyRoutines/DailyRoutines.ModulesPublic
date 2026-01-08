using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Classes.Controllers;
using KamiToolKit.Nodes;

namespace DailyRoutines.ModulesPublic;

public unsafe class MacroPresets : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("MacroPresetsTitle"),
        Description = GetLoc("MacroPresetsDescription"),
        Category    = ModuleCategories.UIOptimization,
        Author      = ["Rorinnn"]
    };

    private const           int    MACROS_PER_SET  = 100;
    private const           int    MAX_MACRO_LINES = 15;
    private static readonly string DefaultOption = LuminaWrapper.GetAddonText(4764); // 未选择

    private static Config ModuleConfig = null!;

    private static AddonController? MacroController;

    private static TextNode?         LabelNode;
    private static TextDropDownNode? PresetDropdownNode;
    private static TextButtonNode?   LoadButtonNode;
    private static TextButtonNode?   SaveButtonNode;
    private static TextButtonNode?   OverwriteButtonNode;
    private static TextButtonNode?   DeleteButtonNode;

    private static MacroPresetsInputAddon?   InputDialog;
    private static MacroPresetsConfirmAddon? ConfirmDialog;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new ();

        InputDialog = new MacroPresetsInputAddon
        {
            Size         = new Vector2(300.0f, 150.0f),
            InternalName = "DRMacroPresetsInputDialog",
            Title        = $"Daily Routines {Info.Title}",
            DepthLayer   = 6
        };

        ConfirmDialog = new MacroPresetsConfirmAddon
        {
            Size         = new Vector2(300.0f, 130.0f),
            InternalName = "DRMacroPresetsConfirmDialog",
            Title        = $"Daily Routines {Info.Title}",
            DepthLayer   = 6
        };

        MacroController = new AddonController("Macro");
        MacroController.OnAttach += OnAddonAttach;
        MacroController.OnDetach += OnAddonDetach;

        MacroController.Enable();
    }

    protected override void Uninit()
    {
        MacroController?.Dispose();
        MacroController = null;
        
        OnAddonDetach(null);

        InputDialog?.Dispose();
        InputDialog = null;

        ConfirmDialog?.Dispose();
        ConfirmDialog = null;

        if (ModuleConfig != null)
            SaveConfig(ModuleConfig);
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("MacroPresets-Config-ConfirmBeforeAction", GetLoc("Overwrite")), ref ModuleConfig.ConfirmOverwrite))
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox(GetLoc("MacroPresets-Config-ConfirmBeforeAction", LuminaWrapper.GetAddonText(68)), ref ModuleConfig.ConfirmDelete))
            SaveConfig(ModuleConfig);
    }

    #region 事件

    private void OnAddonAttach(AtkUnitBase* addon)
    {
        if (addon == null) return;

        var node115 = addon->GetNodeById(115);

        if (node115 != null)
        {
            node115->X = 454f;
            node115->Y = 517f;
        }

        var node116 = addon->GetNodeById(116);

        if (node116 != null)
        {
            node116->X = 521f;
            node116->Y = 517f;
        }

        LabelNode = new TextNode
        {
            Position      = new Vector2(10.0f, 517.0f),
            AlignmentType = AlignmentType.Left,
            FontSize      = 12,
            FontType      = FontType.Axis,
            TextFlags     = TextFlags.Emboss | TextFlags.AutoAdjustNodeSize,
            TextColor     = new Vector4(1, 1, 1, 1),
            String        = $"Daily Routines {Info.Title}"
        };
        LabelNode.AttachNode(addon);

        PresetDropdownNode = new TextDropDownNode
        {
            Position         = new Vector2(10.0f,  525.0f),
            Size             = new Vector2(130.0f, 26.0f),
            MaxListOptions   = 10,
            Options          = GetPresetNames(),
            TextTooltip      = GetLoc("MacroPresets-Tooltip-SelectPreset"),
            OnOptionSelected = OnPresetSelected
        };
        PresetDropdownNode.AttachNode(addon);

        LoadButtonNode = new TextButtonNode
        {
            Position  = new Vector2(140.0f, 524.0f),
            Size      = new Vector2(60.0f,  30.0f),
            String    = LuminaWrapper.GetAddonText(6140), // 读取
            OnClick   = OnLoadPreset,
            IsEnabled = false
        };
        LoadButtonNode.CollisionNode.TextTooltip = GetLoc("MacroPresets-Tooltip-ActionPreset", LuminaWrapper.GetAddonText(6140));
        LoadButtonNode.AttachNode(addon);

        OverwriteButtonNode = new TextButtonNode
        {
            Position  = new Vector2(200.0f, 524.0f),
            Size      = new Vector2(60.0f,  30.0f),
            String    = GetLoc("Overwrite"),
            OnClick   = OnOverwritePreset,
            IsEnabled = false
        };
        OverwriteButtonNode.CollisionNode.TextTooltip = GetLoc("MacroPresets-Tooltip-ActionPreset", GetLoc("Overwrite"));
        OverwriteButtonNode.AttachNode(addon);

        DeleteButtonNode = new TextButtonNode
        {
            Position  = new Vector2(260.0f, 524.0f),
            Size      = new Vector2(60.0f,  30.0f),
            String    = LuminaWrapper.GetAddonText(68), // 删除
            OnClick   = OnDeletePreset,
            IsEnabled = false
        };
        DeleteButtonNode.CollisionNode.TextTooltip = GetLoc("MacroPresets-Tooltip-ActionPreset", LuminaWrapper.GetAddonText(68));
        DeleteButtonNode.AttachNode(addon);

        SaveButtonNode = new TextButtonNode
        {
            Position  = new Vector2(320.0f, 524.0f),
            Size      = new Vector2(60.0f,  30.0f),
            String    = LuminaWrapper.GetAddonText(552), // 保存
            OnClick   = OnSavePreset,
            IsEnabled = true
        };
        SaveButtonNode.CollisionNode.TextTooltip = GetLoc("MacroPresets-Tooltip-SaveNewPreset");
        SaveButtonNode.AttachNode(addon);
    }

    private void OnAddonDetach(AtkUnitBase* addon)
    {
        LabelNode?.Dispose();
        LabelNode = null;

        PresetDropdownNode?.Dispose();
        PresetDropdownNode = null;

        LoadButtonNode?.Dispose();
        LoadButtonNode = null;

        SaveButtonNode?.Dispose();
        SaveButtonNode = null;

        OverwriteButtonNode?.Dispose();
        OverwriteButtonNode = null;

        DeleteButtonNode?.Dispose();
        DeleteButtonNode = null;
    }

    private void OnPresetSelected(string selection)
    {
        var isDefaultOption = selection == DefaultOption;

        if (LoadButtonNode != null)
            LoadButtonNode.IsEnabled = !isDefaultOption;

        if (OverwriteButtonNode != null)
            OverwriteButtonNode.IsEnabled = !isDefaultOption;

        if (DeleteButtonNode != null)
            DeleteButtonNode.IsEnabled = !isDefaultOption;
    }

    private void OnLoadPreset()
    {
        if (PresetDropdownNode == null) return;

        var selectedPreset = PresetDropdownNode.SelectedOption;
        if (string.IsNullOrEmpty(selectedPreset) || selectedPreset == DefaultOption)
            return;

        LoadPreset(selectedPreset);
    }

    private void OnSavePreset()
    {
        if (InputDialog == null) return;

        InputDialog.PlaceholderString = GetLoc("MacroPresets-Input-PresetName");
        InputDialog.DefaultString     = string.Empty;
        InputDialog.OnInputComplete = newName =>
        {
            SavePreset(newName);

            if (PresetDropdownNode != null)
                PresetDropdownNode.Options = GetPresetNames();
        };

        InputDialog.Toggle();
    }

    private void OnOverwritePreset()
    {
        if (PresetDropdownNode == null) return;

        var selectedPreset = PresetDropdownNode.SelectedOption;
        if (string.IsNullOrEmpty(selectedPreset) || selectedPreset == DefaultOption)
            return;

        if (ModuleConfig.ConfirmOverwrite && ConfirmDialog != null)
        {
            ConfirmDialog.Message   = GetLoc("MacroPresets-Confirm-ActionPreset", GetLoc("Overwrite"), selectedPreset);
            ConfirmDialog.OnConfirm = () => SavePreset(selectedPreset, true);
            ConfirmDialog.Toggle();
        }
        else
            SavePreset(selectedPreset, true);
    }

    private void OnDeletePreset()
    {
        if (PresetDropdownNode == null) return;

        var selectedPreset = PresetDropdownNode.SelectedOption;
        if (string.IsNullOrEmpty(selectedPreset) || selectedPreset == DefaultOption)
            return;

        if (ModuleConfig.ConfirmDelete && ConfirmDialog != null)
        {
            ConfirmDialog.Message = GetLoc("MacroPresets-Confirm-ActionPreset", LuminaWrapper.GetAddonText(68), selectedPreset);
            ConfirmDialog.OnConfirm = () =>
            {
                DeletePreset(selectedPreset);

                if (PresetDropdownNode != null)
                {
                    PresetDropdownNode.Options        = GetPresetNames();
                    PresetDropdownNode.SelectedOption = DefaultOption;
                }

                if (LoadButtonNode != null)
                    LoadButtonNode.IsEnabled = false;
                if (OverwriteButtonNode != null)
                    OverwriteButtonNode.IsEnabled = false;
                if (DeleteButtonNode != null)
                    DeleteButtonNode.IsEnabled = false;
            };
            ConfirmDialog.Toggle();
        }
        else
        {
            DeletePreset(selectedPreset);

            if (PresetDropdownNode != null)
            {
                PresetDropdownNode.Options        = GetPresetNames();
                PresetDropdownNode.SelectedOption = DefaultOption;
            }

            if (LoadButtonNode != null)
                LoadButtonNode.IsEnabled = false;
            if (OverwriteButtonNode != null)
                OverwriteButtonNode.IsEnabled = false;
            if (DeleteButtonNode != null)
                DeleteButtonNode.IsEnabled = false;
        }
    }

    #endregion

    #region 预设管理

    private void SavePreset(string presetName, bool isOverwrite = false)
    {
        var macroModule = RaptureMacroModule.Instance();
        if (macroModule == null) return;

        var createdAt = DateTime.Now;

        if (isOverwrite && ModuleConfig.Presets.ContainsKey(presetName))
            createdAt = ModuleConfig.Presets[presetName].CreatedAt;

        var presetData = new PresetData
        {
            CreatedAt        = createdAt,
            IndividualMacros = ReadMacrosFromMemory(macroModule, 0),
            SharedMacros     = ReadMacrosFromMemory(macroModule, 1)
        };

        ModuleConfig.Presets[presetName] = presetData;
        SaveConfig(ModuleConfig);
    }

    private void LoadPreset(string presetName)
    {
        if (presetName == DefaultOption) return;

        var macroModule = RaptureMacroModule.Instance();
        if (macroModule == null) return;

        if (!ModuleConfig.Presets.TryGetValue(presetName, out var presetData)) return;

        WriteMacrosToMemory(macroModule, 0, presetData.IndividualMacros);

        WriteMacrosToMemory(macroModule, 1, presetData.SharedMacros);

        macroModule->SetSavePendingFlag(true, 0);
        macroModule->SetSavePendingFlag(true, 1);

        var hotbarModule = RaptureHotbarModule.Instance();
        if (hotbarModule != null)
            hotbarModule->ReloadAllMacroSlots();
    }

    private void DeletePreset(string presetName)
    {
        if (presetName == DefaultOption) return;

        if (ModuleConfig.Presets.ContainsKey(presetName))
        {
            ModuleConfig.Presets.Remove(presetName);
            SaveConfig(ModuleConfig);
        }
    }

    private List<MacroData> ReadMacrosFromMemory(RaptureMacroModule* macroModule, uint set)
    {
        List<MacroData> macros = [];

        for (uint i = 0; i < MACROS_PER_SET; i++)
        {
            var macro = macroModule->GetMacro(set, i);
            if (macro == null) continue;

            var nameSpan = macro->Name.AsSpan();
            var hasContent = false;

            for (var lineIdx = 0; lineIdx < MAX_MACRO_LINES; lineIdx++)
            {
                if (macro->Lines[lineIdx].AsSpan().Length > 0)
                {
                    hasContent = true;
                    break;
                }
            }

            // 跳过完全为空的宏
            if (nameSpan.Length == 0 && macro->IconId == 0 && !hasContent)
                continue;

            var macroData = new MacroData
            {
                Index  = i,
                IconID = macro->IconId,
                Name   = nameSpan.Length > 0 ? [..nameSpan, (byte)0] : null
            };

            for (var lineIdx = 0; lineIdx < MAX_MACRO_LINES; lineIdx++)
            {
                var lineSpan = macro->Lines[lineIdx].AsSpan();
                if (lineSpan.Length > 0)
                    macroData.Lines.Add([..lineSpan, (byte)0]);
            }

            macros.Add(macroData);
        }

        return macros;
    }

    private void WriteMacrosToMemory(RaptureMacroModule* macroModule, uint set, List<MacroData> macrosData)
    {
        for (uint i = 0; i < MACROS_PER_SET; i++)
        {
            var macro = macroModule->GetMacro(set, i);
            if (macro == null) continue;

            macro->Clear();
            macro->SetIcon(0);
        }

        // 根据索引恢复宏数据
        foreach (var data in macrosData)
        {
            if (data.Index >= MACROS_PER_SET) continue;

            var macro = macroModule->GetMacro(set, data.Index);
            if (macro == null) continue;

            macro->SetIcon(data.IconID);

            if (data.Name != null)
                macro->Name.SetString(data.Name);

            for (var lineIdx = 0; lineIdx < data.Lines.Count && lineIdx < MAX_MACRO_LINES; lineIdx++)
            {
                if (data.Lines[lineIdx] != null)
                    macro->Lines[lineIdx].SetString(data.Lines[lineIdx]);
            }
        }
    }

    #endregion

    #region 工具

    private List<string> GetPresetNames()
    {
        var sortedList = ModuleConfig.Presets
                                     .OrderByDescending(x => x.Value.CreatedAt)
                                     .Select(x => x.Key)
                                     .ToList();

        return sortedList.Prepend(DefaultOption).ToList();
    }

    #endregion

    private class MacroPresetsInputAddon : NativeAddon
    {
        private TextInputNode?  inputNode;
        private TextButtonNode? confirmButton;
        private TextButtonNode? cancelButton;

        public Action<string>? OnInputComplete   { get; set; }
        public string          PlaceholderString { get; set; } = string.Empty;
        public string          DefaultString     { get; set; } = string.Empty;

        protected override void OnSetup(AtkUnitBase* addon)
        {
            SetWindowSize(300.0f, 150.0f);

            inputNode = new TextInputNode
            {
                Position          = ContentStartPosition + new Vector2(0.0f, ContentPadding.Y),
                Size              = new Vector2(ContentSize.X, 28.0f),
                PlaceholderString = PlaceholderString,
                String            = DefaultString,
                AutoSelectAll     = true
            };
            inputNode.AttachNode(this);

            var buttonSize = new Vector2(120.0f, 28.0f);
            var targetYPos = ContentSize.Y - buttonSize.Y + ContentStartPosition.Y;

            confirmButton = new TextButtonNode
            {
                Position = new Vector2(ContentStartPosition.X, targetYPos),
                Size     = buttonSize,
                String   = LuminaWrapper.GetAddonText(1), // 确定
                OnClick = () =>
                {
                    if (inputNode != null && !string.IsNullOrWhiteSpace(inputNode.String))
                    {
                        OnInputComplete?.Invoke(inputNode.String);
                        Close();
                    }
                }
            };
            confirmButton.AttachNode(this);

            cancelButton = new TextButtonNode
            {
                Position = new Vector2(ContentSize.X - buttonSize.X + ContentPadding.X, targetYPos),
                Size     = buttonSize,
                String   = LuminaWrapper.GetAddonText(2), // 取消
                OnClick  = Close
            };
            cancelButton.AttachNode(this);
        }
    }

    private class MacroPresetsConfirmAddon : NativeAddon
    {
        private TextNode?       messageNode;
        private TextButtonNode? confirmButton;
        private TextButtonNode? cancelButton;

        public Action? OnConfirm { get; set; }
        public string  Message   { get; set; } = string.Empty;

        protected override void OnSetup(AtkUnitBase* addon)
        {
            SetWindowSize(300.0f, 130.0f);

            messageNode = new TextNode
            {
                Position      = ContentStartPosition + new Vector2(0.0f, ContentPadding.Y),
                AlignmentType = AlignmentType.Left,
                FontSize      = 14,
                FontType      = FontType.Axis,
                TextFlags     = TextFlags.Emboss | TextFlags.AutoAdjustNodeSize | TextFlags.MultiLine,
                TextColor     = new Vector4(1, 1, 1, 1),
                String        = Message
            };
            messageNode.AttachNode(this);

            var buttonSize = new Vector2(120.0f, 28.0f);
            var targetYPos = ContentSize.Y - buttonSize.Y + ContentStartPosition.Y;

            confirmButton = new TextButtonNode
            {
                Position = new Vector2(ContentStartPosition.X, targetYPos),
                Size     = buttonSize,
                String   = LuminaWrapper.GetAddonText(1), // 确定
                OnClick = () =>
                {
                    OnConfirm?.Invoke();
                    Close();
                }
            };
            confirmButton.AttachNode(this);

            cancelButton = new TextButtonNode
            {
                Position = new Vector2(ContentSize.X - buttonSize.X + ContentPadding.X, targetYPos),
                Size     = buttonSize,
                String   = LuminaWrapper.GetAddonText(2), // 取消
                OnClick  = Close
            };
            cancelButton.AttachNode(this);
        }
    }

    #region 自定义类

    private class MacroData
    {
        public uint         Index  { get; set; }
        public uint         IconID { get; set; }
        public byte[]?      Name   { get; set; }
        public List<byte[]> Lines  { get; set; } = [];
    }

    private class PresetData
    {
        public DateTime        CreatedAt        { get; set; } = DateTime.Now;
        public List<MacroData> IndividualMacros { get; set; } = [];
        public List<MacroData> SharedMacros     { get; set; } = [];
    }

    #endregion

    private class Config : ModuleConfiguration
    {
        public bool                           ConfirmOverwrite = true;
        public bool                           ConfirmDelete    = true;
        public Dictionary<string, PresetData> Presets          = [];
    }
}
