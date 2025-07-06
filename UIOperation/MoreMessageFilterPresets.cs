using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using DailyRoutines.Abstracts;
using Dalamud.Interface;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace DailyRoutines.ModulesPublic;

public class MoreMessageFilterPresets : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = "更多的消息过滤预设",
        Description = "保存指定聊天消息栏的消息过滤设置，并能将保存的消息过滤设置应用到指定的消息栏中",
        Category = ModuleCategories.UIOperation,
        Author = ["Ponta"]
    };

    private static readonly CompSig ApplyMessageFilterSig = new("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 4C 24 ?? 56 57 41 54 41 56 41 57 48 83 EC ?? 45 33 E4");
    private delegate int ApplyMessageFilterDelegate(nint filters);
    private static readonly ApplyMessageFilterDelegate ApplyMessageFilter = ApplyMessageFilterSig.GetDelegate<ApplyMessageFilterDelegate>();

    private delegate int SaveMessageFilterDelegate(nint filters, bool a2);

    private static Config ModuleConfig = null!;

    private static readonly int FilterSize = 307;

    private static int SelectedFilter = 0;

    private static string InputPresetName = string.Empty;

    public override void Init() => ModuleConfig = LoadConfig<Config>() ?? new();

    public override void ConfigUI()
    {
        var logTabName = GetLogTabName();

        var style = ImGui.GetStyle();

        var tableSize = (ImGui.GetContentRegionAvail() - ScaledVector2(100f)) with { Y = 0 };
        using var table = ImRaii.Table("MessageFilterPreset", 4, ImGuiTableFlags.Borders, tableSize);
        if (!table) return;

        ImGui.TableSetupColumn("添加", ImGuiTableColumnFlags.WidthFixed,
            ImGui.GetTextLineHeightWithSpacing() + style.FramePadding.X * 2f);
        ImGui.TableSetupColumn("名称", ImGuiTableColumnFlags.None, 30);
        ImGui.TableSetupColumn("目标", ImGuiTableColumnFlags.None, 30);
        ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed,
            ImGui.CalcTextSize(GetLoc("Apply")).X + style.FramePadding.X * 2f);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableNextColumn();
        if (ImGuiOm.ButtonIconSelectable("AddNewPreset", FontAwesomeIcon.Plus, "保存当前消息栏过滤设置"))
            ImGui.OpenPopup("AddNewPresetPopup");

        using (var popup = ImRaii.Popup("AddNewPresetPopup"))
        {
            if (popup)
            {
                using (var combo = ImRaii.Combo("###AddFilterPresetCombo", logTabName[SelectedFilter], ImGuiComboFlags.HeightLarge))
                {
                    if (combo)
                    {
                        for (int i = 0; i < logTabName.Length; ++i)
                        {
                            if (ImGui.Selectable(logTabName[i], SelectedFilter == i))
                                SelectedFilter = i;
                        }
                    }
                }
                
                var defaultName = $"预设{ModuleConfig.Presets.Count + 1}";
                var name = InputPresetName.IsNullOrEmpty() ? defaultName : InputPresetName;
                ImGui.SameLine();
                if (ImGui.InputText("###PresetNameInput", ref name, 128))
                    InputPresetName = name;
                    

                ImGui.SameLine();
                if (ImGui.Button(GetLoc("Save")))
                {
                    AddFilterPreset(SelectedFilter, name);
                    SaveConfig(ModuleConfig);
                    InputPresetName = string.Empty;
                }

            }
        }

        ImGui.TableNextColumn();
        ImGui.Text("预设名称");

        ImGui.TableNextColumn();
        ImGui.Text("要应用的消息栏");

        ImGui.TableNextColumn();
        ImGui.Text("操作");

        for (int i = 0; i < ModuleConfig.Presets.Count; i++)
        {
            using var id = ImRaii.PushId($"FilterIndex_{i}");

            var preset = ModuleConfig.Presets[i];

            ImGui.TableNextRow();

            ImGui.TableNextColumn();

            ImGui.TableNextColumn();
            ImGui.Selectable($"{preset.Name}");

            using (var context = ImRaii.ContextPopupItem("PresetContextMenu"))
            {
                if (context)
                {
                    ImGui.Text("名称: ");

                    ImGui.SameLine();
                    ImGui.InputText("###RenamePresetInput", ref preset.Name, 128);
                    if (ImGui.IsItemDeactivatedAfterEdit())
                        SaveConfig(ModuleConfig);

                    if (ImGui.MenuItem(GetLoc("Delete")))
                    {
                        ModuleConfig.Presets.RemoveAt(i);
                        SaveConfig(ModuleConfig);
                        i--;
                        continue;
                    }
                }
            }

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1f);
            using (var combo = ImRaii.Combo("###ApplyFilterPresetCombo", logTabName[preset.SelectedFilter], ImGuiComboFlags.HeightLarge))
            {
                if (combo)
                {
                    for (int j = 0; j < logTabName.Length; ++j)
                    {
                        if (ImGui.Selectable(logTabName[j], preset.SelectedFilter == j))
                            preset.SelectedFilter = j;
                    }
                }
            }

            ImGui.TableNextColumn();
            if (ImGui.Button(GetLoc("Apply")))
            {
                ApplyFilterPreset(preset);
                SaveConfig(ModuleConfig);
                NotificationSuccess($"以成功将 {preset.Name} 应用到 {logTabName[preset.SelectedFilter]}", "消息过滤设置");
            }
        }
    }

    private unsafe string[] GetLogTabName()
    {
        var names = new string[4];
        for (int i = 0; i < names.Length; i++)
        {
            var name = RaptureLogModule.Instance()->GetTabName(i)->ToString();
            names[i] = name.IsNullOrEmpty() ? $"第{i + 1}栏" : name;
        }

        return names;
    }

    private nint GetMessageFilter(nint filters, int index)
    {
        nint offset = FilterSize * index + 72;
        return filters + offset;
    }

    private unsafe void AddFilterPreset(int index, string name)
    {
        var filters = LogFilterConfig.Instance();
        var filter = GetMessageFilter((nint)filters, index);
        FilterPreset preset = new();
        preset.Name = name;
        fixed (byte* dst = preset.PresetValue) 
        {
            Buffer.MemoryCopy((void*)filter, dst, FilterSize, FilterSize);
        }
        ModuleConfig.Presets.Add(preset);
    }

    private unsafe void ApplyFilterPreset(FilterPreset preset)
    {
        var filters = LogFilterConfig.Instance();
        var filter = GetMessageFilter((nint)filters, preset.SelectedFilter);
        fixed (byte* src = preset.PresetValue)
        {
            Buffer.MemoryCopy(src, (void*)filter, FilterSize, FilterSize);
        }
        filters->SaveFile(true);
        ApplyMessageFilter((nint)filters);
    }

    private class FilterPreset
    {
        public string Name = string.Empty;
        public int SelectedFilter = 0;
        public byte[] PresetValue = new byte[FilterSize];
    }

    private class Config : ModuleConfiguration
    {
        public List<FilterPreset> Presets = [];
    }
}
