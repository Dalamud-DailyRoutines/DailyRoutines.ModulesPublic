using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DailyRoutines.Abstracts;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Classes.Controllers;
using KamiToolKit.Nodes;

namespace DailyRoutines.ModulesPublic;

public unsafe class MacroPreset : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("宏预设管理"),
        Description = GetLoc("为宏界面添加预设保存、加载、删除功能，支持个人宏和共享宏"),
        Category    = ModuleCategories.UIOptimization,
        Author      = ["Rorinnn"]
    };

    private const int    MacrosPerSet   = 100;
    private const int    MaxMacroLines  = 15;
    private static readonly string DefaultOption  = GetLoc("未选择");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static Config ModuleConfig = null!;

    private static string ConfigPath = string.Empty;

    private static AddonController? MacroController;

    private static TextNode? LabelNode;
    private static TextDropDownNode? PresetDropdownNode;
    private static TextButtonNode? LoadButtonNode;
    private static TextButtonNode? SaveButtonNode;
    private static TextButtonNode? OverwriteButtonNode;
    private static TextButtonNode? DeleteButtonNode;

    private static MacroPresetInputAddon? InputDialog;
    private static MacroPresetConfirmAddon? ConfirmDialog;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new Config();

        ConfigPath = ConfigDirectoryPath;

        InputDialog = new MacroPresetInputAddon
        {
            Size = new Vector2(300.0f, 150.0f),
            InternalName = "MacroPresetInputDialog",
            Title = GetLoc("保存预设"),
            DepthLayer = 6,
        };

        ConfirmDialog = new MacroPresetConfirmAddon
        {
            Size = new Vector2(300.0f, 130.0f),
            InternalName = "MacroPresetConfirmDialog",
            Title = GetLoc("确认操作"),
            DepthLayer = 6,
        };

        MacroController = new AddonController("Macro");

        MacroController.OnAttach += OnAddonAttach;

        MacroController.OnDetach += OnAddonDetach;

        MacroController.Enable();
    }

    protected override void Uninit()
    {
        OnAddonDetach(null);

        InputDialog?.Dispose();
        InputDialog = null;

        ConfirmDialog?.Dispose();
        ConfirmDialog = null;

        MacroController?.Dispose();
        MacroController = null;

        SaveConfig(ModuleConfig);
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("覆盖预设时需要确认"), ref ModuleConfig.ConfirmOverwrite))
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox(GetLoc("删除预设时需要确认"), ref ModuleConfig.ConfirmDelete))
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
            Position = new Vector2(10.0f, 517.0f),
            AlignmentType = AlignmentType.Left,
            FontSize = 12,
            FontType = FontType.Axis,
            TextFlags = TextFlags.Emboss | TextFlags.AutoAdjustNodeSize,
            TextColor = new Vector4(1, 1, 1, 1),
            String = GetLoc("预设管理"),
        };
        LabelNode.AttachNode(addon);

        PresetDropdownNode = new TextDropDownNode
        {
            Position = new Vector2(10.0f, 525.0f),
            Size = new Vector2(130.0f, 26.0f),
            MaxListOptions = 10,
            Options = GetPresetNames(),
            TooltipString = GetLoc("选择要操作的预设"),
            OnOptionSelected = OnPresetSelected,
        };
        PresetDropdownNode.AttachNode(addon);

        LoadButtonNode = new TextButtonNode
        {
            Position = new Vector2(140.0f, 524.0f),
            Size = new Vector2(60.0f, 30.0f),
            String = GetLoc("载入"),
            OnClick = OnLoadPreset,
            IsEnabled = false,
        };
        LoadButtonNode.CollisionNode.TooltipString = GetLoc("载入选中的预设");
        LoadButtonNode.AttachNode(addon);

        OverwriteButtonNode = new TextButtonNode
        {
            Position = new Vector2(200.0f, 524.0f),
            Size = new Vector2(60.0f, 30.0f),
            String = GetLoc("覆盖"),
            OnClick = OnOverwritePreset,
            IsEnabled = false,
        };
        OverwriteButtonNode.CollisionNode.TooltipString = GetLoc("覆盖选中的预设");
        OverwriteButtonNode.AttachNode(addon);

        DeleteButtonNode = new TextButtonNode
        {
            Position = new Vector2(260.0f, 524.0f),
            Size = new Vector2(60.0f, 30.0f),
            String = GetLoc("删除"),
            OnClick = OnDeletePreset,
            IsEnabled = false,
        };
        DeleteButtonNode.CollisionNode.TooltipString = GetLoc("删除选中的预设");
        DeleteButtonNode.AttachNode(addon);

        SaveButtonNode = new TextButtonNode
        {
            Position = new Vector2(320.0f, 524.0f),
            Size = new Vector2(60.0f, 30.0f),
            String = GetLoc("保存"),
            OnClick = OnSavePreset,
            IsEnabled = true,
        };
        SaveButtonNode.CollisionNode.TooltipString = GetLoc("保存当前所有宏为新预设");
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

        InputDialog.PlaceholderString = GetLoc("请输入预设名称");
        InputDialog.DefaultString = string.Empty;
        InputDialog.OnInputComplete = newName =>
        {
            SavePreset(newName, false);

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
            ConfirmDialog.Message = GetLoc($"确定要覆盖预设 '{selectedPreset}' 吗？");
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
            ConfirmDialog.Message = GetLoc($"确定要删除预设 '{selectedPreset}' 吗？");
            ConfirmDialog.OnConfirm = () =>
            {
                DeletePreset(selectedPreset);

                if (PresetDropdownNode != null)
                {
                    PresetDropdownNode.Options = GetPresetNames();
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
                PresetDropdownNode.Options = GetPresetNames();
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

    private static void SavePreset(string presetName, bool isOverwrite = false)
    {
        var macroModule = RaptureMacroModule.Instance();
        if (macroModule == null) return;

        var directory = GetPresetDirectory();
        var filePath = Path.Combine(directory.FullName, $"{presetName}.json");

        DateTime createdAt = DateTime.Now;
        if (isOverwrite && File.Exists(filePath))
        {
            try
            {
                var existingJson = File.ReadAllText(filePath);
                var existingData = JsonSerializer.Deserialize<PresetData>(existingJson);
                if (existingData != null)
                    createdAt = existingData.CreatedAt;
            }
            catch
            {
                // ignored
            }
        }

        var presetData = new PresetData
        {
            CreatedAt = createdAt,
            IndividualMacros = ReadMacrosFromMemory(macroModule, 0),
            SharedMacros = ReadMacrosFromMemory(macroModule, 1)
        };

        var json = JsonSerializer.Serialize(presetData, JsonOptions);
        File.WriteAllText(filePath, json);
    }

    private static void LoadPreset(string presetName)
    {
        if (presetName == DefaultOption) return;

        var macroModule = RaptureMacroModule.Instance();
        if (macroModule == null) return;

        var directory = GetPresetDirectory();
        var filePath = Path.Combine(directory.FullName, $"{presetName}.json");

        if (!File.Exists(filePath)) return;

        var json = File.ReadAllText(filePath);
        var presetData = JsonSerializer.Deserialize<PresetData>(json);

        if (presetData == null) return;

        WriteMacrosToMemory(macroModule, 0, presetData.IndividualMacros);

        WriteMacrosToMemory(macroModule, 1, presetData.SharedMacros);

        macroModule->SetSavePendingFlag(true, 0);
        macroModule->SetSavePendingFlag(true, 1);

        var hotbarModule = RaptureHotbarModule.Instance();
        if (hotbarModule != null)
            hotbarModule->ReloadAllMacroSlots();
    }

    private static void DeletePreset(string presetName)
    {
        if (presetName == DefaultOption) return;

        var directory = GetPresetDirectory();
        var filePath = Path.Combine(directory.FullName, $"{presetName}.json");

        if (!File.Exists(filePath)) return;

        File.Delete(filePath);
    }

    private static List<MacroData> ReadMacrosFromMemory(RaptureMacroModule* macroModule, uint set)
    {
        List<MacroData> macros = [];

        for (uint i = 0; i < MacrosPerSet; i++)
        {
            var macro = macroModule->GetMacro(set, i);
            if (macro == null) continue;

            var macroData = new MacroData
            {
                IconID = macro->IconId,
                Name = macro->Name.ToString(),
                Lines = []
            };

            for (var lineIdx = 0; lineIdx < MaxMacroLines; lineIdx++)
            {
                var line = macro->Lines[lineIdx].ToString();
                macroData.Lines.Add(line);
            }

            macros.Add(macroData);
        }

        return macros;
    }

    private static void WriteMacrosToMemory(RaptureMacroModule* macroModule, uint set, List<MacroData> macrosData)
    {
        for (uint i = 0; i < MacrosPerSet && i < macrosData.Count; i++)
        {
            var macro = macroModule->GetMacro(set, i);
            if (macro == null) continue;

            var data = macrosData[(int)i];

            macro->Clear();

            macro->SetIcon(data.IconID);

            macro->Name.SetString(data.Name);

            for (var lineIdx = 0; lineIdx < MaxMacroLines && lineIdx < data.Lines.Count; lineIdx++)
                macro->Lines[lineIdx].SetString(data.Lines[lineIdx]);
        }
    }

    #endregion

    #region 工具

    private static List<string> GetPresetNames()
    {
        var directory = GetPresetDirectory();

        List<(string Name, DateTime CreatedAt)> presetList = [];
        foreach (var file in directory.EnumerateFiles("*.json"))
        {
            try
            {
                var json = File.ReadAllText(file.FullName);
                var preset = JsonSerializer.Deserialize<PresetData>(json);
                if (preset != null)
                {
                    var fileName = Path.GetFileNameWithoutExtension(file.Name);
                    presetList.Add((fileName, preset.CreatedAt));
                }
            }
            catch
            {
                // ignored
            }
        }

        var sortedList = presetList.OrderByDescending(x => x.CreatedAt)
                                   .Select(x => x.Name)
                                   .ToList();

        return sortedList.Prepend(DefaultOption).ToList();
    }

    private static DirectoryInfo GetPresetDirectory()
    {
        var directoryInfo = new DirectoryInfo(ConfigPath);
        if (!directoryInfo.Exists)
            directoryInfo.Create();

        return directoryInfo;
    }

    #endregion

    private class MacroPresetInputAddon : NativeAddon
    {
        private TextInputNode? inputNode;
        private TextButtonNode? confirmButton;
        private TextButtonNode? cancelButton;

        public Action<string>? OnInputComplete { get; set; }
        public string PlaceholderString { get; set; } = string.Empty;
        public string DefaultString { get; set; } = string.Empty;

        protected override unsafe void OnSetup(AtkUnitBase* addon)
        {
            SetWindowSize(300.0f, 150.0f);

            inputNode = new TextInputNode
            {
                Position = ContentStartPosition + new Vector2(0.0f, ContentPadding.Y),
                Size = new Vector2(ContentSize.X, 28.0f),
                PlaceholderString = PlaceholderString,
                String = DefaultString,
                AutoSelectAll = true,
            };
            inputNode.AttachNode(this);

            var buttonSize = new Vector2(120.0f, 28.0f);
            var targetYPos = ContentSize.Y - buttonSize.Y + ContentStartPosition.Y;

            confirmButton = new TextButtonNode
            {
                Position = new Vector2(ContentStartPosition.X, targetYPos),
                Size = buttonSize,
                String = "确定",
                OnClick = () =>
                {
                    if (inputNode != null && !string.IsNullOrWhiteSpace(inputNode.String))
                    {
                        OnInputComplete?.Invoke(inputNode.String);
                        Close();
                    }
                },
            };
            confirmButton.AttachNode(this);

            cancelButton = new TextButtonNode
            {
                Position = new Vector2(ContentSize.X - buttonSize.X + ContentPadding.X, targetYPos),
                Size = buttonSize,
                String = "取消",
                OnClick = Close,
            };
            cancelButton.AttachNode(this);
        }
    }

    private class MacroPresetConfirmAddon : NativeAddon
    {
        private TextNode? messageNode;
        private TextButtonNode? confirmButton;
        private TextButtonNode? cancelButton;

        public Action? OnConfirm { get; set; }
        public string Message { get; set; } = string.Empty;

        protected override unsafe void OnSetup(AtkUnitBase* addon)
        {
            SetWindowSize(300.0f, 130.0f);

            messageNode = new TextNode
            {
                Position = ContentStartPosition + new Vector2(0.0f, ContentPadding.Y),
                AlignmentType = AlignmentType.Left,
                FontSize = 14,
                FontType = FontType.Axis,
                TextFlags = TextFlags.Emboss | TextFlags.AutoAdjustNodeSize | TextFlags.MultiLine,
                TextColor = new Vector4(1, 1, 1, 1),
                String = Message,
            };
            messageNode.AttachNode(this);

            var buttonSize = new Vector2(120.0f, 28.0f);
            var targetYPos = ContentSize.Y - buttonSize.Y + ContentStartPosition.Y;

            confirmButton = new TextButtonNode
            {
                Position = new Vector2(ContentStartPosition.X, targetYPos),
                Size = buttonSize,
                String = "确定",
                OnClick = () =>
                {
                    OnConfirm?.Invoke();
                    Close();
                },
            };
            confirmButton.AttachNode(this);

            cancelButton = new TextButtonNode
            {
                Position = new Vector2(ContentSize.X - buttonSize.X + ContentPadding.X, targetYPos),
                Size = buttonSize,
                String = "取消",
                OnClick = Close,
            };
            cancelButton.AttachNode(this);
        }
    }

    #region 自定义类

    private class MacroData
    {
        public uint IconID { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<string> Lines { get; set; } = [];
    }

    private class PresetData
    {
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public List<MacroData> IndividualMacros { get; set; } = [];
        public List<MacroData> SharedMacros { get; set; } = [];
    }

    #endregion

    private class Config : ModuleConfiguration
    {
        public bool ConfirmOverwrite = true;
        public bool ConfirmDelete = true;
    }
}
