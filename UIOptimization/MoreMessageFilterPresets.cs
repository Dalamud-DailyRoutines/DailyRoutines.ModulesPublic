using DailyRoutines.Common.Info.Abstractions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Memory;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using MenuItem = Dalamud.Game.Gui.ContextMenu.MenuItem;

namespace DailyRoutines.ModulesPublic;

public class MoreMessageFilterPresets : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("MoreMessageFilterPresetsTitle"),
        Description = Lang.Get("MoreMessageFilterPresetsDescription"),
        Category    = ModuleCategory.UIOptimization,
        Author      = ["Ponta"]
    };
    
    private static readonly CompSig ApplyMessageFilterSig = new("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 4C 24 ?? 56 57 41 54 41 56 41 57 48 83 EC ?? 45 33 E4");
    private delegate        int     ApplyMessageFilterDelegate(nint filters);
    private readonly        ApplyMessageFilterDelegate ApplyMessageFilter = ApplyMessageFilterSig.GetDelegate<ApplyMessageFilterDelegate>();

    private static readonly        CompSig MessageFilterSizeSig = new("FF C5 81 FD ?? ?? ?? ?? 0F 82 ?? ?? ?? ?? 48 8B 0D");
    private static readonly unsafe int     MessageFilterSize    = ReadCMPImmediateValue((nint)((byte*)MessageFilterSizeSig.ScanText() + 2));

    private          Config                 config = null!;
    private readonly ApplyLogFilterMenuItem menuItem;

    private int    selectedFilter;
    private string inputPresetName = string.Empty;
    
    public MoreMessageFilterPresets() =>
        menuItem = new(this);
    
    protected override void Init()
    {
        config                                 =  Config.Load(this) ?? new();
        DService.Instance().ContextMenu.OnMenuOpened += OnMenuOpened;
    }

    protected override void Uninit() =>
        DService.Instance().ContextMenu.OnMenuOpened -= OnMenuOpened;

    protected override void ConfigUI()
    {
        var logTabName = GetLogTabName();

        var style = ImGui.GetStyle();

        var       tableSize = (ImGui.GetContentRegionAvail() - ScaledVector2(100f)) with { Y = 0 };
        using var table     = ImRaii.Table("MessageFilterPreset", 4, ImGuiTableFlags.Borders, tableSize);
        if (!table) return;

        ImGui.TableSetupColumn("添加", ImGuiTableColumnFlags.WidthFixed, ImGui.GetTextLineHeightWithSpacing() + style.FramePadding.X * 2f);
        ImGui.TableSetupColumn("名称", ImGuiTableColumnFlags.None,       30);
        ImGui.TableSetupColumn("目标", ImGuiTableColumnFlags.None,       30);
        ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.None,       15);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableNextColumn();
        if (ImGuiOm.ButtonIconSelectable("AddNewPreset", FontAwesomeIcon.Plus))
            ImGui.OpenPopup("AddNewPresetPopup");

        using (var popup = ImRaii.Popup("AddNewPresetPopup"))
        {
            if (popup)
            {
                ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("MoreMessageFilterPresets-SourceTab"));

                using (ImRaii.PushIndent())
                using (var combo = ImRaii.Combo("###AddFilterPresetCombo", logTabName[selectedFilter], ImGuiComboFlags.HeightLarge))
                {
                    if (combo)
                    {
                        for (var i = 0; i < logTabName.Length; ++i)
                            if (ImGui.Selectable(logTabName[i], selectedFilter == i))
                                selectedFilter = i;
                    }
                }

                ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Name"));

                var defaultName = $"{Lang.Get("Preset")} {config.Presets.Count + 1}";
                var name        = inputPresetName.IsNullOrEmpty() ? defaultName : inputPresetName;

                using (ImRaii.PushIndent())
                {
                    if (ImGui.InputText("###PresetNameInput", ref name, 256))
                        inputPresetName = name;
                }

                if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.FileArchive, Lang.Get("Save")))
                {
                    AddFilterPreset(selectedFilter, name);
                    config.Save(this);

                    inputPresetName = string.Empty;
                    ImGui.CloseCurrentPopup();
                }
            }
        }

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(Lang.Get("Name"));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(Lang.Get("MoreMessageFilterPresets-TargetTab"));

        for (var i = 0; i < config.Presets.Count; i++)
        {
            using var id = ImRaii.PushId($"FilterIndex_{i}");

            var preset = config.Presets[i];

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
            ImGui.SetNextItemWidth(-1f);

            using (var combo = ImRaii.Combo("###ApplyFilterPresetCombo", logTabName[preset.SelectedFilter], ImGuiComboFlags.HeightLarge))
            {
                if (combo)
                {
                    for (var j = 0; j < logTabName.Length; ++j)
                        if (ImGui.Selectable(logTabName[j], preset.SelectedFilter == j))
                            preset.SelectedFilter = j;
                }
            }

            ImGui.TableNextColumn();

            if (ImGui.Button(Lang.Get("Apply")))
            {
                ApplyFilterPresetAndNotify(preset);
                config.Save(this);
            }

            ImGui.SameLine();

            if (ImGui.Button(Lang.Get("Delete")))
            {
                config.Presets.RemoveAt(i);
                config.Save(this);

                break;
            }
        }
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (!menuItem.IsDisplay(args)) return;
        args.AddMenuItem(menuItem.Get());
    }

    private static unsafe string[] GetLogTabName()
    {
        var names = new string[4];

        var addonText = LuminaGetter.GetRow<Addon>(656).GetValueOrDefault().Text.ToDalamudString();

        for (var i = 0; i < names.Length; i++)
        {
            var name = RaptureLogModule.Instance()->GetTabName(i)->ToString();
            addonText.Payloads[1] = new TextPayload($"{i + 1}");

            names[i] = name.IsNullOrEmpty() ? addonText.ToString() : name;
        }

        return names;
    }

    private static nint GetMessageFilter(nint filters, int index)
    {
        nint offset = MessageFilterSize * index + 72;
        return filters + offset;
    }

    private unsafe void AddFilterPreset(int index, string name)
    {
        var          filters = LogFilterConfig.Instance();
        var          filter  = GetMessageFilter((nint)filters, index);
        FilterPreset preset  = new() { Name = name };

        fixed (byte* dst = preset.PresetValue)
            Buffer.MemoryCopy((void*)filter, dst, MessageFilterSize, MessageFilterSize);

        config.Presets.Add(preset);
    }

    private unsafe void ApplyFilterPreset(FilterPreset preset, int index)
    {
        var filters = LogFilterConfig.Instance();
        var filter  = GetMessageFilter((nint)filters, index);

        fixed (byte* src = preset.PresetValue)
            Buffer.MemoryCopy(src, (void*)filter, MessageFilterSize, MessageFilterSize);

        filters->SaveFile(true);
        ApplyMessageFilter((nint)filters);
    }

    private void ApplyFilterPresetAndNotify(FilterPreset preset, int index)
    {
        ApplyFilterPreset(preset, index);
        NotifyHelper.Instance().NotificationSuccess(Lang.Get("MoreMessageFilterPresets-Notification-Applied", preset.Name, index + 1));
    }

    private void ApplyFilterPresetAndNotify(FilterPreset preset) => 
        ApplyFilterPresetAndNotify(preset, preset.SelectedFilter);

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
    
    private class ApplyLogFilterMenuItem(MoreMessageFilterPresets module) : MenuItemBase
    {
        public override string Name       { get; protected set; } = Lang.Get("MoreMessageFilterPresetsTitle");
        public override string Identifier { get; protected set; } = nameof(MoreMessageFilterPresets);

        protected override bool IsSubmenu    { get; set; } = true;
        protected override bool WithDRPrefix { get; set; } = true;

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            if (GetSelectedTabIndex() > 3) return;

            args.OpenSubmenu(Name, ProcessMenuItems(module));
        }

        public override unsafe bool IsDisplay(IMenuOpenedArgs args)
        {
            if (module.config.Presets.Count == 0) return false;
            if (args.MenuType              != ContextMenuType.Default) return false;
            if (args.AddonName             != "ChatLog") return false;

            var agent             = (AgentContext*)args.AgentPtr;
            var contextMenu       = agent->CurrentContextMenu;
            var contextMenuCounts = contextMenu->EventParams[0].Int;
            if (contextMenuCounts == 0) return false;

            var str = contextMenu->EventParams[8].GetValueAsString();
            if (!str.Equals(LuminaWrapper.GetAddonText(370), StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        private static unsafe int GetSelectedTabIndex()
        {
            var agentChatLog     = AgentModule.Instance()->GetAgentChatLog();
            var selectedTabIndex = MemoryHelper.Read<int>((nint)agentChatLog + 0x130);

            return selectedTabIndex;
        }

        private static List<MenuItem> ProcessMenuItems(MoreMessageFilterPresets module)
        {
            var list = new List<MenuItem>();

            var selectedTabIndex = GetSelectedTabIndex();

            foreach (var preset in module.config.Presets)
            {
                list.Add
                (
                    new()
                    {
                        Name      = preset.Name,
                        OnClicked = _ => module.ApplyFilterPresetAndNotify(preset, selectedTabIndex)
                    }
                );
            }

            return list;
        }
    }

    private class FilterPreset
    {
        public string Name        = string.Empty;
        public byte[] PresetValue = new byte[MessageFilterSize];
        public int    SelectedFilter;
    }

    private class Config : ModuleConfig
    {
        public List<FilterPreset> Presets = [];
    }
}
