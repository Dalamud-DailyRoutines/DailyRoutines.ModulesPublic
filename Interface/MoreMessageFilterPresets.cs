using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Memory;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Interface;

public class MoreMessageFilterPresets : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("MoreMessageFilterPresetsTitle"),
        Description = Lang.Get("MoreMessageFilterPresetsDescription"),
        Category    = ModuleCategory.Interface,
        Author      = ["Ponta"]
    };

    private static readonly CompSig ApplyMessageFilterSig = new("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 4C 24 ?? 56 57 41 54 41 56 41 57 48 83 EC ?? 45 33 E4");
    private delegate int ApplyMessageFilterDelegate
    (
        nint filters
    );
    private ApplyMessageFilterDelegate ApplyMessageFilter = null!;

    private static readonly CompSig ReloadLogTabSig = new("48 63 C2 48 69 D0 28 09 00 00 48 81 C2 30 05 00 00 48 03 D1");
    private delegate void ReloadLogTabDelegate
    (
        nint logModule,
        int  tabIndex
    );
    private ReloadLogTabDelegate ReloadLogTab = null!;

    private static readonly        CompSig MessageFilterSizeSig = new("FF C5 81 FD ?? ?? ?? ?? 0F 82 ?? ?? ?? ?? 48 8B 0D");
    private static readonly unsafe int     MessageFilterSize    = ReadCMPImmediateValue((nint)((byte*)MessageFilterSizeSig.ScanText() + 2));
    
    private static List<(int Index, string Name)>? OrderedFilters;

    private Config                 config   = null!;
    private ApplyLogFilterMenuItem menuItem = null!;

    private int    sourceTabIndex;
    private string inputPresetName = string.Empty;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        menuItem = new(this);
        
        ApplyMessageFilter = ApplyMessageFilterSig.GetDelegate<ApplyMessageFilterDelegate>();
        ReloadLogTab       = ReloadLogTabSig.GetDelegate<ReloadLogTabDelegate>();

        foreach (var preset in config.Presets)
        {
            if (preset.PresetValue.Length == MessageFilterSize) continue;

            Array.Resize(ref preset.PresetValue, MessageFilterSize);
        }

        ContextMenuManager.Instance().Reg(menuItem);
    }

    protected override void Uninit() =>
        ContextMenuManager.Instance().Unreg(menuItem);

    protected override void ConfigUI()
    {
        var tabNames   = GetLogTabNames();
        var currentTab = GetSelectedTabIndex();
        if (currentTab == -1) return;
        
        var style = ImGui.GetStyle();

        var       tableSize = (ImGui.GetContentRegionAvail() - ScaledVector2(100f)) with { Y = 0 };
        using var table     = ImRaii.Table("MessageFilterPreset", 4, ImGuiTableFlags.Borders, tableSize);
        if (!table) return;

        ImGui.TableSetupColumn("Add", ImGuiTableColumnFlags.WidthFixed, ImGui.GetTextLineHeightWithSpacing() + (style.FramePadding.X * 2f));
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.None, 28);
        ImGui.TableSetupColumn("Enabled", ImGuiTableColumnFlags.None, 10);
        ImGui.TableSetupColumn("Operation", ImGuiTableColumnFlags.None, 26);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableNextColumn();

        if (ImGuiOm.ButtonIconSelectable("AddNewPreset", FontAwesomeIcon.Plus))
        {
            if (currentTab >= 0)
                sourceTabIndex = currentTab;

            ImGui.OpenPopup("AddNewPresetPopup");
        }

        using (var popup = ImRaii.Popup("AddNewPresetPopup"))
        {
            if (popup)
            {
                ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("MoreMessageFilterPresets-SourceTab"));

                using (ImRaii.PushIndent())
                using (var combo = ImRaii.Combo("###AddFilterPresetCombo", tabNames[sourceTabIndex], ImGuiComboFlags.HeightLarge))
                {
                    if (combo)
                    {
                        for (var i = 0; i < tabNames.Length; ++i)
                            if (ImGui.Selectable(tabNames[i], sourceTabIndex == i))
                                sourceTabIndex = i;
                    }
                }

                ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Name"));

                var defaultName = $"{Lang.Get("Preset")} {config.Presets.Count + 1}";
                var name        = inputPresetName.IsNullOrEmpty() ?
                                      defaultName :
                                      inputPresetName;

                using (ImRaii.PushIndent())
                {
                    if (ImGui.InputText("###PresetNameInput", ref name, 256))
                        inputPresetName = name;
                }

                if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.FileArchive, Lang.Get("Save")))
                {
                    AddFilterPreset(sourceTabIndex, name);
                    config.Save(this);

                    inputPresetName = string.Empty;
                    ImGui.CloseCurrentPopup();
                }
            }
        }

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(Lang.Get("Name"));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(Lang.Get("Enabled"));

        for (var i = 0; i < config.Presets.Count; i++)
        {
            using var id = ImRaii.PushId($"FilterIndex_{i}");

            var preset = config.Presets[i];

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGuiOm.TextCentered($"{i + 1}");

            ImGui.TableNextColumn();
            ImGuiOm.Selectable(preset.Name);

            using (var context = ImRaii.ContextPopupItem("PresetContextMenu"))
            {
                if (context)
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Name"));

                    ImGui.SameLine();
                    ImGui.InputText("###RenamePresetInput", ref preset.Name, 128);

                    if (ImGui.IsItemDeactivatedAfterEdit())
                    {
                        config.Save(this);
                        ImGui.CloseCurrentPopup();
                    }
                }
            }

            ImGui.TableNextColumn();

            var (enabledCount, totalCount) = CountEnabledFilters(preset);
            ImGuiOm.Text($"{enabledCount} / {totalCount}");

            ImGui.TableNextColumn();
            
            if (ImGui.Button($"{FontAwesomeIcon.List.ToIconString()} {Lang.Get("Details")}"))
                ImGui.OpenPopup("PresetDetailsPopup");

            using (var details = ImRaii.Popup("PresetDetailsPopup"))
            {
                if (details)
                    DrawPresetDetails(preset);
            }

            ImGui.SameLine();

            if (ImGuiOm.HoldButton($"Delete_{i}", $"{FontAwesomeIcon.TrashAlt.ToIconString()} {Lang.Get("Delete")}"))
            {
                config.Presets.RemoveAt(i);
                config.Save(this);

                break;
            }
        }
    }

    private static void DrawPresetDetails
    (
        FilterPreset preset
    )
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), preset.Name);
        ImGui.Separator();

        using var child = ImRaii.Child("PresetDetailsChild", ScaledVector2(520f, 480f), true);
        if (!child) return;

        foreach (var (index, name) in GetOrderedFilters())
        {
            var enabled = preset.PresetValue[index] == 1;
            var color   = enabled ?
                              KnownColor.LightGreen.ToVector4() :
                              KnownColor.Gray.ToVector4();

            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextColored(color, (enabled ? FontAwesomeIcon.Check : FontAwesomeIcon.Times).ToIconString());

            ImGui.SameLine();
            ImGui.TextColored(color, name);
        }
    }

    private unsafe void AddFilterPreset
    (
        int    index,
        string name
    )
    {
        var          filters = LogFilterConfig.Instance();
        var          filter  = GetMessageFilter((nint)filters, index);
        FilterPreset preset  = new() { Name = name };

        fixed (byte* dst = preset.PresetValue)
            Buffer.MemoryCopy((void*)filter, dst, MessageFilterSize, MessageFilterSize);

        config.Presets.Add(preset);
    }

    private unsafe void ApplyFilterPreset
    (
        FilterPreset preset,
        int          index
    )
    {
        var filters = LogFilterConfig.Instance();
        var filter  = GetMessageFilter((nint)filters, index);

        fixed (byte* src = preset.PresetValue)
            Buffer.MemoryCopy(src, (void*)filter, MessageFilterSize, MessageFilterSize);

        filters->SaveFile(true);
        ApplyMessageFilter((nint)filters);

        ReloadLogTab((nint)RaptureLogModule.Instance(), index);
    }

    private void ApplyFilterPresetAndNotify
    (
        FilterPreset preset,
        int          tabIndex
    )
    {
        if (tabIndex < 0)
            return;

        ApplyFilterPreset(preset, tabIndex);

        var message = Lang.Get("MoreMessageFilterPresets-Notification-Applied", preset.Name, GetLogTabNames()[tabIndex]);
        NotifyHelper.Instance().Chat(message);
        NotifyHelper.Toast(message);
    }

    #region 工具
    
    private static unsafe string[] GetLogTabNames()
    {
        var names = new string[LOG_TAB_COUNT];

        for (var i = 0; i < names.Length; i++)
        {
            var name = RaptureLogModule.Instance()->GetTabName(i)->ToString();

            names[i] = string.IsNullOrEmpty(name) ?
                           ISeStringEvaluator.Instance().EvaluateFromAddon(656, [i + 1]).ToString() :
                           name;
        }

        return names;
    }

    private static unsafe int GetSelectedTabIndex()
    {
        var addon = (AddonChatLog*)ChatLog;
        if (addon == null) return -1;

        var index = addon->TabIndex;

        return index < LOG_TAB_COUNT ? index : -1;
    }

    private static unsafe int GetContextMenuTabIndex()
    {
        var agent = AgentModule.Instance()->GetAgentChatLog();
        if (agent == null) return -1;

        // AgentChatLog + 0x198: 右键菜单所对应的消息栏索引
        // TODO：等待 FFCS 的 PR 合并到 AgentChatLog 里 ContextTabIndex
        var index = MemoryHelper.Read<int>((nint)agent + 0x198);

        return index is >= 0 and < LOG_TAB_COUNT ? index : -1;
    }

    private static nint GetMessageFilter
    (
        nint filters,
        int  index
    )
    {
        nint offset = (MessageFilterSize * index) + 72;
        return filters + offset;
    }

    private static List<(int Index, string Name)> GetOrderedFilters()
    {
        if (OrderedFilters != null) return OrderedFilters;

        var filters = new List<(int Index, byte Category, byte DisplayOrder, string Name)>();

        for (var i = 0; i < MessageFilterSize; i++)
        {
            if (!LuminaGetter.TryGetRow<LogFilter>((uint)i, out var row)) continue;
            if (row is { Category: 0 } or { LogKind: 0 }) continue;

            filters.Add((i, row.Category, row.DisplayOrder, row.Name.ToString()));
        }

        return OrderedFilters =
        [
            .. filters.OrderBy(x => x.Category)
                      .ThenBy(x => x.DisplayOrder)
                      .Select(x => (x.Index, x.Name))
        ];
    }

    private static (int Enabled, int Total) CountEnabledFilters
    (
        FilterPreset preset
    )
    {
        var enabled = 0;
        var total   = 0;

        foreach (var (index, _) in GetOrderedFilters())
        {
            total++;

            if (preset.PresetValue[index] == 1)
                enabled++;
        }

        return (enabled, total);
    }

    private static int ReadCMPImmediateValue
    (
        nint instructionAddress
    )
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

    #endregion

    private sealed class ApplyLogFilterMenuItem
    (
        MoreMessageFilterPresets module
    ) : ContextMenuEntry
    {
        public override string Identifier =>
            nameof(MoreMessageFilterPresets);

        public override unsafe ContextMenuItem? Create
        (
            ContextMenuOpenedArgs args
        )
        {
            if (module.config.Presets.Count == 0) return null;
            if (args.AddonName              != "ChatLog") return null;

            var agent = args.DefaultAgentContext;
            if (agent == null) return null;

            var contextMenu = agent->CurrentContextMenu;
            if (contextMenu == null) return null;

            var contextMenuCounts = contextMenu->EventParams[0].Int;
            if (contextMenuCounts == 0) return null;

            var str = contextMenu->EventParams[8].GetValueAsString();
            if (!str.Equals(LuminaWrapper.GetAddonText(370), StringComparison.OrdinalIgnoreCase))
                return null;

            var name = Lang.Get("MoreMessageFilterPresets-ContextMenu");

            return new()
            {
                Name = name,
                Submenu = new()
                {
                    Title = name,
                    Entries =
                    [
                        .. module.config.Presets.Select(preset => new PresetMenuItem(module, preset))
                    ]
                }
            };
        }
    }

    private sealed class PresetMenuItem
    (
        MoreMessageFilterPresets module,
        FilterPreset             preset
    ) : ContextMenuEntry
    {
        public override string Identifier =>
            nameof(MoreMessageFilterPresets);

        public override ContextMenuItem Create
        (
            ContextMenuOpenedArgs args
        )
        {
            var contextTabIndex = GetContextMenuTabIndex();
            if (contextTabIndex == -1) return null;

            return new()
            {
                Name      = Lang.Get("MoreMessageFilterPresets-ContextMenu-Apply", preset.Name, GetLogTabNames()[contextTabIndex]),
                OnClicked = _ => module.ApplyFilterPresetAndNotify(preset, contextTabIndex)
            };
        }
    }

    private class FilterPreset
    {
        public string Name        = string.Empty;
        public byte[] PresetValue = new byte[MessageFilterSize];
    }

    private class Config : ModuleConfig
    {
        public List<FilterPreset> Presets = [];
    }

    #region 常量

    private const int LOG_TAB_COUNT = 4;

    #endregion
}
