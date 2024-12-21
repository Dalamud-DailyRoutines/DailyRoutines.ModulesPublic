global using static DailyRoutines.Helpers.NotifyHelper;
global using static OmenTools.Helpers.HelpersOm;
global using static DailyRoutines.Infos.Widgets;
global using static OmenTools.Helpers.HelpersOm;
global using static OmenTools.Infos.InfosOm;
global using static DailyRoutines.Helpers.NotifyHelper;
global using OmenTools.ImGuiOm;
global using OmenTools.Helpers;
global using OmenTools;
global using ImGuiNET;
using System.Collections.Generic;
using DailyRoutines.Abstracts;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.GeneratedSheets;


namespace DailyRoutines.Modules;

public class AutoSortItems : DailyModuleBase
{
    private readonly string[] _sortOptions        = [GetLoc("AutoSortItems-Desc"), GetLoc("AutoSortItems-Asc")];
    private readonly string[] _tabOptions         = [GetLoc("AutoSortItems-Tab"), GetLoc("AutoSortItems-UnTab")];
    private readonly string[] _sortOptionsCommand = ["des", "asc"];

    private static Config _config = null!;

    public override ModuleInfo Info => new()
    {
        Author = ["那年雪落"],
        Title = GetLoc("AutoSortItemsTitle"),
        Description = GetLoc("AutoSortItemsDescription"),
        ReportUrl = "https://github.com/TheDeathDragon/DailyRoutines.AutoSortItem.git",
        Category = ModuleCategories.General,
    };

    public override void Init()
    {
        _config = LoadConfig<Config>() ?? new();
        DService.ClientState.TerritoryChanged += OnZoneChanged;
        TaskHelper ??= new TaskHelper { TimeLimitMS = 30_000 };
    }

    private void OnZoneChanged(ushort zone)
    {
        if (zone <= 0) return;
        TaskHelper.Abort();
        TaskHelper.Enqueue(CheckCanSort);
    }

    private bool? CheckCanSort()
    {
        if (BetweenAreas || !IsScreenReady() || OccupiedInEvent) return false;
        var isInNormalConditions = DService.Condition[ConditionFlag.NormalConditions] ||
                                   DService.Condition[ConditionFlag.Mounted];
        if (!isInNormalConditions || !IsInNormalMap())
        {
            TaskHelper.Abort();
            return true;
        }

        TaskHelper.Enqueue(SendSortCommand, "SendSortCommand", 5_000, true, 1);
        return true;
    }

    public override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        TaskHelper?.Abort();
        base.Uninit();
    }

    public override void ConfigUI()
    {
        
        if (ImGuiOm.CheckboxColored(GetLoc("AutoSortItems-SendSortMessage"), ref _config.SendSortMessage))
        {
            SaveConfig(_config);
        }

        ImGui.Spacing();
        if (ImGui.Button(GetLoc("AutoSortItems-ResetSettings")))
        {
            ResetConfigToDefault();
        }
        
        ImGui.Spacing();
        var tableSize = ImGui.GetContentRegionAvail() with { Y = 0 };
        using var table = ImRaii.Table(GetLoc("AutoSortItemsTableTitle"), 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg, tableSize);
        if (!table) return;

        ImGui.TableSetupColumn(GetLoc("AutoSortItems-ColumnName"), ImGuiTableColumnFlags.WidthFixed, 100f * GlobalFontScale);
        ImGui.TableSetupColumn(GetLoc("AutoSortItems-ColumnCategory"), ImGuiTableColumnFlags.WidthFixed, 150f * GlobalFontScale);
        ImGui.TableSetupColumn(GetLoc("AutoSortItems-ColumnDescription"), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();
        
        DrawTableRow(GetLoc("AutoSortItems-RowChestId"), ref _config.ArmouryChestId, _sortOptions, "");
        DrawTableRow(GetLoc("AutoSortItems-RowItemLevel"), ref _config.ArmouryItemLevel, _sortOptions, "");
        DrawTableRow(GetLoc("AutoSortItems-RowCategory"), ref _config.ArmouryCategory, _sortOptions, GetLoc("AutoSortItems-ArmouryCategoryDesc"));
        DrawTableRow(GetLoc("AutoSortItems-RowInventoryHq"), ref _config.InventoryHq, _sortOptions, "");
        DrawTableRow(GetLoc("AutoSortItems-RowInventoryId"), ref _config.InventoryId, _sortOptions, "");
        DrawTableRow(GetLoc("AutoSortItems-RowInventoryItemLevel"), ref _config.InventoryItemLevel, _sortOptions, "");
        DrawTableRow(GetLoc("AutoSortItems-RowInventoryCategory"), ref _config.InventoryCategory, _sortOptions, GetLoc("AutoSortItems-InventoryCategoryDesc"));
        DrawTableRow(GetLoc("AutoSortItems-RowInventoryTab"), ref _config.InventoryTab, _tabOptions, GetLoc("AutoSortItems-InventoryTabDesc"));
    }

    private static unsafe bool IsInNormalMap()
    {
        var currentMapData = LuminaCache.GetRow<Map>(DService.ClientState.MapId);
        if (currentMapData == null) return false;
        if (currentMapData.TerritoryType.Row > 0 &&
            currentMapData.TerritoryType.Value.ContentFinderCondition.Row > 0) return false;

        var invalidContentTypes = new HashSet<uint> { 16, 17, 18, 19, 31, 32, 34, 35 };
        var isPvp = GameMain.IsInPvPArea() || GameMain.IsInPvPInstance();
        var contentData = LuminaCache.GetRow<ContentFinderCondition>(GameMain.Instance()->CurrentContentFinderConditionId);

        return !isPvp && (contentData == null || !invalidContentTypes.Contains(contentData.ContentType.Row));
    }

    private void DrawTableRow(string label, ref int value, string[] options, string notes)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.Text(label);

        ImGui.TableSetColumnIndex(1);
        ImGui.SetNextItemWidth(-1);

        var oldValue = value;
        if (ImGui.Combo("##" + label, ref value, options, options.Length) && value != oldValue)
        {
            SaveConfig(_config);
        }

        ImGui.TableSetColumnIndex(2);
        ImGui.SetNextItemWidth(200 * GlobalFontScale);
        ImGui.Text(notes);
    }

    private void SendSortCommand()
    {
        SendSortCondition("armourychest", "id", _config.ArmouryChestId);
        SendSortCondition("armourychest", "itemlevel", _config.ArmouryItemLevel);
        SendSortCondition("armourychest", "category", _config.ArmouryCategory);
        ChatHelper.Instance.SendMessage("/itemsort execute armourychest");

        SendSortCondition("inventory", "hq", _config.InventoryHq);
        SendSortCondition("inventory", "id", _config.InventoryId);
        SendSortCondition("inventory", "itemlevel", _config.InventoryItemLevel);
        SendSortCondition("inventory", "category", _config.InventoryCategory);

        if (_config.InventoryTab == 0)
        {
            ChatHelper.Instance.SendMessage("/itemsort condition inventory tab");
        }

        ChatHelper.Instance.SendMessage("/itemsort execute inventory");

        if (_config.SendSortMessage)
        {
            Chat(GetLoc("AutoSortItems-SortMessage"));
        }

        return;

        void SendSortCondition(string target, string condition, int setting)
        {
            ChatHelper.Instance.SendMessage($"/itemsort condition {target} {condition} {_sortOptionsCommand[setting]}");
        }
    }

    private void ResetConfigToDefault()
    {
        _config = new Config();
        SaveConfig(_config);
    }

    public class Config : ModuleConfiguration
    {
        public int ArmouryChestId;
        public int ArmouryItemLevel;
        public int ArmouryCategory;
        public int InventoryHq;
        public int InventoryId;
        public int InventoryItemLevel;
        public int InventoryCategory;
        public int InventoryTab;
        public bool SendSortMessage = true;
    }
}
