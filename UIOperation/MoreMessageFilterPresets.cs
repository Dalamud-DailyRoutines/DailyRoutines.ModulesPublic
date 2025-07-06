using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using DailyRoutines.Abstracts;
using Dalamud.Interface;
using Dalamud.Utility;

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

    // type: 4 - 消息过滤
    private static readonly CompSig GetSettingsObjectSig = new("40 53 48 83 EC ?? 0F B6 D9 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 4C 8B C0");
    private delegate nint GetSettingsObjectDelegate(byte type);
    private static readonly GetSettingsObjectDelegate GetSettingsObject = GetSettingsObjectSig.GetDelegate<GetSettingsObjectDelegate>();

    private static readonly CompSig ApplyMessageFilterSig = new("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 4C 24 ?? 56 57 41 54 41 56 41 57 48 83 EC ?? 45 33 E4");
    private delegate int ApplyMessageFilterDelegate(nint filters);
    private static readonly ApplyMessageFilterDelegate ApplyMessageFilter = ApplyMessageFilterSig.GetDelegate<ApplyMessageFilterDelegate>();

    private delegate int SaveMessageFilterDelegate(nint filters, bool a2);

    private static Config ModuleConfig = null!;

    private static readonly int FilterSize = 307;

    private static int SelectedFilter = 0;

    private static string InputPresetName = string.Empty;

    // 不知道有没有什么便捷的方法可以获取消息栏实际名称，这里偷个懒先写死
    private static readonly string[] FilterIndexName =
    [
        "第1栏",
        "第2栏",
        "第3栏",
        "第4栏"
    ];
    public class FilterPreset
    {
        public string Name = string.Empty;
        public int SelectedFilter = 0;
        public byte[] PresetValue = new byte[FilterSize];
    }
    public class Config : ModuleConfiguration
    {
        public List<FilterPreset> Presets = [];
    }

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
    }

    public override void ConfigUI()
    {
        ImGuiStylePtr style = ImGui.GetStyle();

        var tableSize = (ImGui.GetContentRegionAvail() - ScaledVector2(100f)) with { Y = 0 };
        using var table = ImRaii.Table("MessageFilterPreset", 4, ImGuiTableFlags.Borders, tableSize);
        if (!table) return;

        ImGui.TableSetupColumn("添加", ImGuiTableColumnFlags.WidthFixed,
            ImGui.CalcTextSize(FontAwesomeIcon.Plus.ToIconString()).X + style.FramePadding.X * 2f);
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
                using (var combo = ImRaii.Combo("###AddFilterPresetCombo", FilterIndexName[SelectedFilter], ImGuiComboFlags.HeightLarge))
                {
                    if (combo)
                    {
                        for (int i = 0; i < FilterIndexName.Length; ++i)
                        {
                            if (ImGui.Selectable(FilterIndexName[i], SelectedFilter == i))
                                SelectedFilter = i;
                        }
                    }
                }
                
                string defaultName = $"预设{ModuleConfig.Presets.Count + 1}";
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
            using (var combo = ImRaii.Combo("###ApplyFilterPresetCombo", FilterIndexName[preset.SelectedFilter], ImGuiComboFlags.HeightLarge))
            {
                if (combo)
                {
                    for (int j = 0; j < FilterIndexName.Length; ++j)
                    {
                        if (ImGui.Selectable(FilterIndexName[j], preset.SelectedFilter == j))
                            preset.SelectedFilter = j;
                    }
                }
            }

            ImGui.TableNextColumn();
            if (ImGui.Button(GetLoc("Apply")))
            {
                ApplyFilterPreset(preset);
                SaveConfig(ModuleConfig);
                NotificationSuccess($"以成功将 {preset.Name} 应用到 {FilterIndexName[preset.SelectedFilter]}", "消息过滤设置");
            }
        }
    }

    private nint GetMessageFilter(nint filters, int index)
    {
        nint offset = (nint)(FilterSize * index + 72);
        return filters + offset;
    }

    private unsafe void AddFilterPreset(int index, string name)
    {
        nint filters = GetSettingsObject(4);
        nint filter = GetMessageFilter(filters, index);
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
        nint filters = GetSettingsObject(4);
        nint filter = GetMessageFilter(filters, preset.SelectedFilter);
        fixed (byte* src = preset.PresetValue)
        {
            Buffer.MemoryCopy(src, (void*)filter, FilterSize, FilterSize);
        }
        SaveMessageFilter(filters);
        ApplyMessageFilter(filters);
    }
    private unsafe void SaveMessageFilter(nint filters)
    {
        nint vtable = *(nint*)filters;
        nint vfunc = *(nint*)(vtable + 104);
        var saveMessageFilter = Marshal.GetDelegateForFunctionPointer<SaveMessageFilterDelegate>(vfunc);
        saveMessageFilter(filters, true);
    }
}
