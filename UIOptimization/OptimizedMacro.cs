using System.Numerics;
using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Controllers;
using KamiToolKit.Nodes;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedMacro : ModuleBase
{
    private const int MACROS_PER_SET  = 100;
    private const int MAX_MACRO_LINES = 15;

    private const string COMMAND = "macroset";

    private static string DefaultOption = LuminaWrapper.GetAddonText(4764); // 未选择

    private static Config ModuleConfig = null!;

    private static AddonController? MacroController;

    private static HorizontalListNode? ControlListNode;
    private static TextDropDownNode?   PresetDropdownNode;
    private static TextButtonNode?     LoadButtonNode;
    private static TextButtonNode?     SaveButtonNode;
    private static TextButtonNode?     DeleteButtonNode;

    private static MacroPresetsInputAddon?   InputDialog;
    private static MacroPresetsConfirmAddon? ConfirmDialog;

    public override ModuleInfo Info { get; } = new()
    {
        Title           = Lang.Get("OptimizedMacroTitle"),
        Description     = Lang.Get("OptimizedMacroDescription"),
        Category        = ModuleCategory.UIOptimization,
        Author          = ["Rorinnn"],
        PreviewImageURL = ["https://gh.atmoomen.top/raw.githubusercontent.com/AtmoOmen/StaticAssets/main/DailyRoutines/image/OptimizedMacro-UI.png"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    protected override void Init()
    {
        ModuleConfig = Config.Load(this) ?? new();

        CommandManager.Instance().AddSubCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("OptimizedMacro-CommandHelp") });

        InputDialog = new MacroPresetsInputAddon
        {
            Size         = new(300, 120),
            InternalName = "DRMacroPresetsInputDialog",
            Title        = Lang.Get("PleaseInput"),
            DepthLayer   = 6
        };

        ConfirmDialog = new MacroPresetsConfirmAddon
        {
            Size         = new(300, 100),
            InternalName = "DRMacroPresetsConfirmDialog",
            Title        = Lang.Get("PleaseConfirmOperation"),
            DepthLayer   = 6
        };

        MacroController = new()
        {
            AddonName  = "Macro",
            OnSetup    = OnAddonSetup,
            OnFinalize = OnAddonFinalize
        };

        MacroController.Enable();
    }

    protected override void Uninit()
    {
        CommandManager.Instance().RemoveSubCommand(COMMAND);

        MacroController?.Dispose();
        MacroController = null;

        OnAddonFinalize(null);

        InputDialog?.Dispose();
        InputDialog = null;

        ConfirmDialog?.Dispose();
        ConfirmDialog = null;

        if (ModuleConfig != null)
            ModuleConfig.Save(this);
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Command"));

        using (ImRaii.PushIndent())
            ImGui.TextUnformatted($"/pdr {COMMAND} {Lang.Get("OptimizedMacro-CommandHelp")}");

        ImGui.NewLine();

        if (ImGui.Checkbox(Lang.Get("OptimizedMacro-ConfirmBeforeDelete"), ref ModuleConfig.ConfirmOverwrite))
            ModuleConfig.Save(this);

        if (ImGui.Checkbox(Lang.Get("OptimizedMacro-ConfirmBeforeOverwrite"), ref ModuleConfig.ConfirmDelete))
            ModuleConfig.Save(this);
    }

    #region Tools

    private static List<string> GetPresetNames()
    {
        var sortedList = ModuleConfig.Presets
                                     .OrderByDescending(x => x.Value.CreatedAt)
                                     .Select(x => x.Key)
                                     .ToList();

        return sortedList.Prepend(DefaultOption).ToList();
    }

    #endregion

    private abstract class BaseMacroDialog : NativeAddon
    {
        protected TextButtonNode? CancelButton;
        protected TextButtonNode? ConfirmButton;

        protected void SetupButtons(float yOffset = 0f)
        {
            var buttonSize = new Vector2(120, 28);
            var targetYPos = ContentSize.Y - buttonSize.Y + ContentStartPosition.Y + yOffset;

            ConfirmButton = new TextButtonNode
            {
                Position = ContentStartPosition with { Y = targetYPos },
                Size     = buttonSize,
                String   = LuminaWrapper.GetAddonText(1), // 确定
                OnClick  = OnConfirmClick
            };
            ConfirmButton.AttachNode(this);

            CancelButton = new TextButtonNode
            {
                Position = new Vector2(ContentSize.X - buttonSize.X + ContentPadding.X, targetYPos),
                Size     = buttonSize,
                String   = LuminaWrapper.GetAddonText(2), // 取消
                OnClick  = Close
            };
            CancelButton.AttachNode(this);
        }

        protected abstract void OnConfirmClick();
    }

    private class MacroPresetsInputAddon : BaseMacroDialog
    {
        private TextInputNode? inputNode;

        public Action<string>? OnInputComplete   { get; set; }
        public string          PlaceholderString { get; set; } = string.Empty;
        public string          DefaultString     { get; set; } = string.Empty;

        protected override void OnSetup(AtkUnitBase* addon)
        {
            inputNode = new TextInputNode
            {
                Position          = ContentStartPosition + ContentPadding with { X = 0 },
                Size              = ContentSize with { Y = 28 },
                PlaceholderString = PlaceholderString,
                String            = DefaultString,
                AutoSelectAll     = true
            };
            inputNode.AttachNode(this);

            SetupButtons();
        }

        protected override void OnConfirmClick()
        {
            if (inputNode != null && !string.IsNullOrWhiteSpace(inputNode.String.ToString()))
            {
                OnInputComplete?.Invoke(inputNode.String.ToString());
                Close();
            }
        }
    }

    private class MacroPresetsConfirmAddon : BaseMacroDialog
    {
        public Action? OnConfirm { get; set; }

        protected override void OnSetup(AtkUnitBase* addon) =>
            SetupButtons(-5f);

        protected override void OnConfirmClick()
        {
            OnConfirm?.Invoke();
            Close();
        }
    }

    private class Config : ModuleConfig
    {
        public bool                           ConfirmDelete    = true;
        public bool                           ConfirmOverwrite = true;
        public Dictionary<string, PresetData> Presets          = [];
    }

    #region Event Handlers

    private static void OnCommand(string command, string args)
    {
        if (string.IsNullOrWhiteSpace(args)) return;
        LoadPreset(args);
    }

    private void OnAddonSetup(AtkUnitBase* addon)
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
            nodeMacroCount->X = 450;
            nodeMacroCount->Y = 521;
        }

        ControlListNode = new()
        {
            Size     = new(400, 30),
            Position = new(10, 517)
        };
        ControlListNode.AttachNode(addon);

        PresetDropdownNode = new TextDropDownNode
        {
            Size             = new(150, 30),
            Position         = new(0, -1),
            MaxListOptions   = 10,
            Options          = GetPresetNames(),
            OnOptionSelected = OnPresetSelected
        };
        ControlListNode.AddNode(PresetDropdownNode);

        LoadButtonNode = new TextButtonNode
        {
            Size      = new(100, 30),
            String    = LuminaWrapper.GetAddonText(6140), // 读取
            OnClick   = OnLoadPreset,
            IsEnabled = false
        };
        LoadButtonNode.LabelNode.AutoAdjustTextSize();
        ControlListNode.AddNode(LoadButtonNode);

        DeleteButtonNode = new TextButtonNode
        {
            Size      = new(100, 30),
            String    = LuminaWrapper.GetAddonText(68), // 删除
            OnClick   = OnDeletePreset,
            IsEnabled = false
        };
        DeleteButtonNode.LabelNode.AutoAdjustTextSize();
        ControlListNode.AddNode(DeleteButtonNode);

        SaveButtonNode = new TextButtonNode
        {
            Size      = new(100, 30),
            String    = LuminaWrapper.GetAddonText(552), // 保存
            IsEnabled = true,
            OnClick = () =>
            {
                if (PresetDropdownNode.SelectedOption != DefaultOption)
                    OnOverwritePreset();
                else
                    OnSavePreset();
            }
        };
        SaveButtonNode.LabelNode.AutoAdjustTextSize();
        ControlListNode.AddNode(SaveButtonNode);
    }

    private static void OnAddonFinalize(AtkUnitBase* addon)
    {
        PresetDropdownNode?.Dispose();
        PresetDropdownNode = null;

        LoadButtonNode?.Dispose();
        LoadButtonNode = null;

        SaveButtonNode?.Dispose();
        SaveButtonNode = null;

        DeleteButtonNode?.Dispose();
        DeleteButtonNode = null;

        ControlListNode?.Dispose();
        ControlListNode = null;
    }

    private static void OnPresetSelected(string selection)
    {
        var isDefaultOption = selection == DefaultOption;

        LoadButtonNode.IsEnabled   = !isDefaultOption;
        DeleteButtonNode.IsEnabled = !isDefaultOption;
    }

    private static void OnLoadPreset()
    {
        var selectedPreset = PresetDropdownNode.SelectedOption;
        if (string.IsNullOrEmpty(selectedPreset) || selectedPreset == DefaultOption)
            return;

        LoadPreset(selectedPreset);
    }

    private void OnSavePreset()
    {
        if (InputDialog == null) return;

        InputDialog.PlaceholderString = $"{Lang.Get("Name")} ({Lang.Get("Preset")})";
        InputDialog.DefaultString     = string.Empty;
        InputDialog.OnInputComplete = newName =>
        {
            SavePreset(newName);
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

        if (ModuleConfig.ConfirmOverwrite)
        {
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

        if (ModuleConfig.ConfirmDelete)
        {
            ConfirmDialog.OnConfirm = () => DeletePreset(selectedPreset);
            ConfirmDialog.Toggle();
        }
        else
            DeletePreset(selectedPreset);
    }

    #endregion

    #region Preset Management

    private void SavePreset(string presetName, bool isOverwrite = false)
    {
        try
        {
            var macroModule = RaptureMacroModule.Instance();
            if (macroModule == null) return;

            if (string.IsNullOrWhiteSpace(presetName))
                return;

            var createdAt = StandardTimeManager.Instance().Now;

            if (isOverwrite && ModuleConfig.Presets.TryGetValue(presetName, out var preset))
                createdAt = preset.CreatedAt;

            var presetData = new PresetData
            {
                CreatedAt        = createdAt,
                IndividualMacros = ReadMacrosFromMemory(macroModule, 0),
                SharedMacros     = ReadMacrosFromMemory(macroModule, 1)
            };

            ModuleConfig.Presets[presetName] = presetData;
            ModuleConfig.Save(this);

            NotifyHelper.Instance().Chat(Lang.Get(isOverwrite ? "OptimizedMacro-Notification-Overwritten" : "OptimizedMacro-Notification-Saved", presetName));
        }
        catch
        {
            NotifyHelper.Instance().Chat(Lang.Get("OptimizedMacro-Notification-SaveError", presetName));
        }
    }

    private static void LoadPreset(string presetName)
    {
        try
        {
            var macroModule = RaptureMacroModule.Instance();
            if (macroModule == null) return;

            var hotbarModule = RaptureHotbarModule.Instance();
            if (hotbarModule == null) return;

            if (presetName == DefaultOption) return;

            if (string.IsNullOrWhiteSpace(presetName) || !ModuleConfig.Presets.TryGetValue(presetName, out var presetData))
                throw new Exception();

            WriteMacrosToMemory(macroModule, 0, presetData.IndividualMacros);
            WriteMacrosToMemory(macroModule, 1, presetData.SharedMacros);

            macroModule->SetSavePendingFlag(true, 0);
            macroModule->SetSavePendingFlag(true, 1);
            hotbarModule->ReloadAllMacroSlots();

            NotifyHelper.Instance().Chat(Lang.Get("OptimizedMacro-Notification-Loaded", presetName));
        }
        catch
        {
            NotifyHelper.Instance().Chat(Lang.Get("OptimizedMacro-Notification-LoadError", presetName));
        }
    }

    private void DeletePreset(string presetName)
    {
        try
        {
            if (presetName == DefaultOption) return;

            if (string.IsNullOrWhiteSpace(presetName) || !ModuleConfig.Presets.Remove(presetName))
                throw new Exception();

            ModuleConfig.Save(this);
            NotifyHelper.Instance().Chat(Lang.Get("OptimizedMacro-Notification-Deleted", presetName));

            PresetDropdownNode.Options        = GetPresetNames();
            PresetDropdownNode.SelectedOption = DefaultOption;

            LoadButtonNode.IsEnabled   = false;
            DeleteButtonNode.IsEnabled = false;
        }
        catch
        {
            NotifyHelper.Instance().Chat(Lang.Get("OptimizedMacro-Notification-DeleteError", presetName));
        }
    }

    private static List<MacroData> ReadMacrosFromMemory(RaptureMacroModule* macroModule, uint set)
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
                Name   = nameSpan.Length > 0 ? [..nameSpan, 0] : null
            };

            for (var lineIdx = 0; lineIdx < MAX_MACRO_LINES; lineIdx++)
            {
                var lineSpan = macro->Lines[lineIdx].AsSpan();
                if (lineSpan.Length > 0)
                    macroData.Lines[lineIdx] = [..lineSpan, 0];
            }

            macros.Add(macroData);
        }

        return macros;
    }

    private static void WriteMacrosToMemory(RaptureMacroModule* macroModule, uint set, List<MacroData> macrosData)
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
}
