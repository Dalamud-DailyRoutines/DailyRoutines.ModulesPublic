using System;
using System.Collections.Generic;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface;
using Dalamud.Memory;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public class MoreMessageFilterPresets : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("MoreMessageFilterPresetsTitle"),
        Description = GetLoc("MoreMessageFilterPresetsDescription"),
        Category    = ModuleCategories.UIOptimization,
        Author      = ["Ponta"]
    };
    
    private static readonly CompSig ApplyMessageFilterSig = new("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 4C 24 ?? 56 57 41 54 41 56 41 57 48 83 EC ?? 45 33 E4");
    private delegate int ApplyMessageFilterDelegate(nint filters);
    private static readonly ApplyMessageFilterDelegate ApplyMessageFilter = ApplyMessageFilterSig.GetDelegate<ApplyMessageFilterDelegate>();

    private static readonly        CompSig MessageFilterSizeSig = new("FF C5 81 FD ?? ?? ?? ?? 0F 82 ?? ?? ?? ?? 48 8B 0D");
    private static readonly unsafe int     MessageFilterSize    = ReadCMPImmediateValue((nint)((byte*)MessageFilterSizeSig.ScanText() + 2));
    
    private static Config ModuleConfig = null!;

    private static int    SelectedFilter;
    private static string InputPresetName = string.Empty;

    private static readonly ApplyLogFilterMenuItem MenuItem = new();

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        DService.ContextMenu.OnMenuOpened += OnMenuOpened;
    }

    public override void Uninit() =>
        DService.ContextMenu.OnMenuOpened -= OnMenuOpened;

    public override void ConfigUI()
    {
        var logTabName = GetLogTabName();

        var style = ImGui.GetStyle();

        var       tableSize = (ImGui.GetContentRegionAvail() - ScaledVector2(100f)) with { Y = 0 };
        using var table     = ImRaii.Table("MessageFilterPreset", 4, ImGuiTableFlags.Borders, tableSize);
        if (!table) return;

        ImGui.TableSetupColumn("添加", ImGuiTableColumnFlags.WidthFixed, ImGui.GetTextLineHeightWithSpacing() + (style.FramePadding.X * 2f));
        ImGui.TableSetupColumn("名称", ImGuiTableColumnFlags.None,       30);
        ImGui.TableSetupColumn("目标", ImGuiTableColumnFlags.None,       30);
        ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.None,       15);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableNextColumn();
        if (ImGuiOm.ButtonIconSelectable("AddNewPreset", FontAwesomeIcon.Plus, "保存当前消息栏过滤设置"))
            ImGui.OpenPopup("AddNewPresetPopup");

        using (var popup = ImRaii.Popup("AddNewPresetPopup"))
        {
            if (popup)
            {
                ImGui.TextColored(LightSkyBlue, GetLoc("MoreMessageFilterPresets-SourceTab"));
                
                using (ImRaii.PushIndent())
                using (var combo = ImRaii.Combo("###AddFilterPresetCombo", logTabName[SelectedFilter], ImGuiComboFlags.HeightLarge))
                {
                    if (combo)
                    {
                        for (var i = 0; i < logTabName.Length; ++i)
                        {
                            if (ImGui.Selectable(logTabName[i], SelectedFilter == i))
                                SelectedFilter = i;
                        }
                    }
                }

                ImGui.TextColored(LightSkyBlue, GetLoc("Name"));
                
                var defaultName = $"{GetLoc("Preset")} {ModuleConfig.Presets.Count + 1}";
                var name        = InputPresetName.IsNullOrEmpty() ? defaultName : InputPresetName;
                using (ImRaii.PushIndent())
                {
                    if (ImGui.InputText("###PresetNameInput", ref name, 256))
                        InputPresetName = name;
                }

                if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.FileArchive, GetLoc("Save")))
                {
                    AddFilterPreset(SelectedFilter, name);
                    SaveConfig(ModuleConfig);
                    
                    InputPresetName = string.Empty;
                    ImGui.CloseCurrentPopup();
                }
            }
        }

        ImGui.TableNextColumn();
        ImGui.Text(GetLoc("Name"));

        ImGui.TableNextColumn();
        ImGui.Text(GetLoc("MoreMessageFilterPresets-TargetTab"));

        for (var i = 0; i < ModuleConfig.Presets.Count; i++)
        {
            using var id = ImRaii.PushId($"FilterIndex_{i}");

            var preset = ModuleConfig.Presets[i];

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGuiOm.Text($"{i + 1}");

            ImGui.TableNextColumn();
            ImGuiOm.Selectable($"{preset.Name}");

            using (var context = ImRaii.ContextPopupItem("PresetContextMenu"))
            {
                if (context)
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(LightSkyBlue, GetLoc("Name"));

                    ImGui.SameLine();
                    ImGui.InputText("###RenamePresetInput", ref preset.Name, 128);
                    if (ImGui.IsItemDeactivatedAfterEdit())
                    {
                        SaveConfig(ModuleConfig);
                        ImGui.CloseCurrentPopup();
                    }
                }
            }

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1f);
            using (var combo = ImRaii.Combo("###ApplyFilterPresetCombo", logTabName[preset.SelectedFilter], ImGuiComboFlags.HeightLarge))
            {
                if (combo)
                {
                    for (var j = 0; j < logTabName.Length; ++j)
                    {
                        if (ImGui.Selectable(logTabName[j], preset.SelectedFilter == j))
                            preset.SelectedFilter = j;
                    }
                }
            }

            ImGui.TableNextColumn();
            if (ImGui.Button(GetLoc("Apply")))
            {
                ApplyFilterPresetAndNotify(preset);
                SaveConfig(ModuleConfig);
            }

            ImGui.SameLine();
            if (ImGui.Button(GetLoc("Delete")))
            {
                ModuleConfig.Presets.RemoveAt(i);
                SaveConfig(ModuleConfig);
                
                break;
            }
        }
    }
    private static unsafe void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (!MenuItem.IsDisplay(args)) return;
        args.AddMenuItem(MenuItem.Get());
    }

    private static unsafe string[] GetLogTabName()
    {
        var names = new string[4];

        var addonText = LuminaGetter.GetRow<Addon>(656).GetValueOrDefault().Text.ToDalamudString();
        
        for (var i = 0; i < names.Length; i++)
        {
            var name = RaptureLogModule.Instance()->GetTabName(i)->ToString();
            addonText.Payloads[1] = new TextPayload($"{i + 1}");

            names[i] = name.IsNullOrEmpty() ? addonText.ExtractText() : name;
        }

        return names;
    }

    private static nint GetMessageFilter(nint filters, int index)
    {
        nint offset = (MessageFilterSize * index) + 72;
        return filters + offset;
    }

    private static unsafe void AddFilterPreset(int index, string name)
    {
        var          filters = LogFilterConfig.Instance();
        var          filter  = GetMessageFilter((nint)filters, index);
        FilterPreset preset  = new() { Name = name };
        
        fixed (byte* dst = preset.PresetValue)
        {
            Buffer.MemoryCopy((void*)filter, dst, MessageFilterSize, MessageFilterSize);
        }

        ModuleConfig.Presets.Add(preset);
    }

    private static unsafe void ApplyFilterPreset(FilterPreset preset, int index)
    {
        var filters = LogFilterConfig.Instance();
        var filter  = GetMessageFilter((nint)filters, index);
        
        fixed (byte* src = preset.PresetValue)
        {
            Buffer.MemoryCopy(src, (void*)filter, MessageFilterSize, MessageFilterSize);
        }

        filters->SaveFile(true);
        ApplyMessageFilter((nint)filters);
    }

    private static void ApplyFilterPresetAndNotify(FilterPreset preset, int index)
    {
        ApplyFilterPreset(preset, index);
        NotificationSuccess(GetLoc("MoreMessageFilterPresets-Notification-Applied", preset.Name, index + 1));
    }

    private static void ApplyFilterPresetAndNotify(FilterPreset preset)
        => ApplyFilterPresetAndNotify(preset, preset.SelectedFilter);

    private static int ReadCMPImmediateValue(nint instructionAddress)
    {
        var instruction = MemoryHelper.ReadRaw(instructionAddress, 6);

        switch (instruction.Length)
        {
            // 81 FD XX XX XX XX
            case >= 6 when instruction[0] == 0x81 && instruction[1] == 0xFD:
            {
                var imm32 = BitConverter.ToInt32(instruction, 2);
                return imm32;
            }
            // 83 FD XX
            case >= 3 when instruction[0] == 0x83 && instruction[1] == 0xFD:
            {
                var imm8 = (sbyte)instruction[2];
                return imm8;
            }
            default:
                throw new InvalidOperationException("未知的汇编指令");
        }
    }

    private class ApplyLogFilterMenuItem : MenuItemBase
    {
        public override string Name { get; protected set; } = GetLoc("ApplyLogFilterPreset");
        protected override bool IsSubmenu { get; set; } = true;
        protected override bool WithDRPrefix { get; set; } = true;

        protected override unsafe void OnClicked(IMenuItemClickedArgs args)
        {
            if (GetSelectedTabIndex() > 3) return;

            args.OpenSubmenu(Name, ProcessMenuItems());
        }

        public override unsafe bool IsDisplay(IMenuOpenedArgs args)
        {
            if (ModuleConfig.Presets.Count == 0) return false;
            if (args.MenuType != ContextMenuType.Default) return false;
            if (args.AddonName != "ChatLog") return false;

            var agent = (AgentContext*)args.AgentPtr;
            var contextMenu = agent->CurrentContextMenu;
            var contextMenuCounts = contextMenu->EventParams[0].Int;
            if (contextMenuCounts == 0) return false;

            var str = contextMenu->EventParams[7].GetValueAsString();
            if (!str.Equals(LuminaWrapper.GetAddonText(370), StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        private static unsafe int GetSelectedTabIndex()
        {
            var agentChatLog = AgentModule.Instance()->GetAgentChatLog();
            var selectedTabIndex = MemoryHelper.Read<int>((nint)agentChatLog + 0x130);

            return selectedTabIndex;
        }

        private static List<MenuItem> ProcessMenuItems()
        {
            var list = new List<MenuItem>();

            var selectedTabIndex = GetSelectedTabIndex();

            foreach (var preset in ModuleConfig.Presets)
                list.Add(new()
                {
                    Name = preset.Name,
                    OnClicked = _ => ApplyFilterPresetAndNotify(preset, selectedTabIndex)
                });

            return list;
        }
    }

    private class FilterPreset
    {
        public          string Name = string.Empty;
        public          int    SelectedFilter;
        public readonly byte[] PresetValue = new byte[MessageFilterSize];
    }

    private class Config : ModuleConfiguration
    {
        public readonly List<FilterPreset> Presets = [];
    }
}
