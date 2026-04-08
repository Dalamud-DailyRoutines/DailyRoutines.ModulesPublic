using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoSortItems : ModuleBase
{
    private static readonly string[] SortOptions        = [Lang.Get("Descending"), Lang.Get("Ascending")];
    private static readonly string[] TabOptions         = [Lang.Get("AutoSortItems-Splited"), Lang.Get("AutoSortItems-Merged")];
    private static readonly string[] SortOptionsCommand = ["des", "asc"];

    private static Config ModuleConfig = null!;

    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoSortItemsTitle"),
        Description = Lang.Get("AutoSortItemsDescription"),
        Category    = ModuleCategory.General,
        Author      = ["那年雪落"]
    };

    protected override void Init()
    {
        ModuleConfig =   Config.Load(this) ?? new();
        TaskHelper   ??= new() { TimeoutMS = 15_000 };

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
    }

    protected override void ConfigUI()
    {
        if (ImGui.Button(LuminaWrapper.GetAddonText(1389)))
            TaskHelper.Enqueue(CheckCanSort);

        ImGui.NewLine();

        if (ImGui.Checkbox(Lang.Get("SendChat"), ref ModuleConfig.SendChat))
            ModuleConfig.Save(this);

        ImGui.SameLine();
        if (ImGui.Checkbox(Lang.Get("SendNotification"), ref ModuleConfig.SendNotification))
            ModuleConfig.Save(this);

        ImGui.Spacing();

        var       tableSize = (ImGui.GetContentRegionAvail() * 0.75f) with { Y = 0 };
        using var table     = ImRaii.Table(Lang.Get("Sort"), 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg, tableSize);
        if (!table) return;

        ImGui.TableSetupColumn("名称", ImGuiTableColumnFlags.WidthStretch, 30);
        ImGui.TableSetupColumn("方法", ImGuiTableColumnFlags.WidthStretch, 30);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(LuminaWrapper.GetAddonText(12210));

        var typeText = LuminaWrapper.GetAddonText(9448);

        DrawTableRow("兵装库 ID", "ID",              ref ModuleConfig.ArmouryChestID,   SortOptions);
        DrawTableRow("兵装库等级",  Lang.Get("Level"), ref ModuleConfig.ArmouryItemLevel, SortOptions);
        DrawTableRow("兵装库类型",  typeText,          ref ModuleConfig.ArmouryCategory,  SortOptions, Lang.Get("AutoSortItems-ArmouryCategoryDesc"));

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(LuminaWrapper.GetAddonText(12209));

        DrawTableRow("背包 HQ", "HQ",                              ref ModuleConfig.InventoryHQ,        SortOptions);
        DrawTableRow("背包 ID", "ID",                              ref ModuleConfig.InventoryID,        SortOptions);
        DrawTableRow("背包等级",  Lang.Get("Level"),                 ref ModuleConfig.InventoryItemLevel, SortOptions);
        DrawTableRow("背包类型",  typeText,                          ref ModuleConfig.InventoryCategory,  SortOptions, Lang.Get("AutoSortItems-InventoryCategoryDesc"));
        DrawTableRow("背包分栏",  Lang.Get("AutoSortItems-Splited"), ref ModuleConfig.InventoryTab,       TabOptions,  Lang.Get("AutoSortItems-InventoryTabDesc"));
    }

    protected override void Uninit() =>
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;

    private void DrawTableRow(string id, string label, ref int value, string[] options, string note = "")
    {
        using var idPush = ImRaii.PushId($"{label}_{id}");

        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(label);

        if (!string.IsNullOrWhiteSpace(note))
            ImGuiOm.HelpMarker(note);

        var oldValue = value;
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo($"##{label}", ref value, options, options.Length) && value != oldValue)
            ModuleConfig.Save(this);
    }

    private void OnZoneChanged(ushort zone)
    {
        TaskHelper.Abort();

        if (GameState.TerritoryType == 0) return;
        TaskHelper.Enqueue(CheckCanSort);
    }

    private bool CheckCanSort()
    {
        if (!GameState.IsLoggedIn || !UIModule.IsScreenReady() || DService.Instance().Condition.IsOccupiedInEvent) return false;

        if (!DService.Instance().ClientState.IsClientIdle() || !IsInValidZone())
        {
            TaskHelper.Abort();
            return true;
        }

        TaskHelper.Enqueue(SendSortCommand, "SendSortCommand");
        return true;
    }

    private static bool IsInValidZone() =>
        GameState.Map           != 0 &&
        GameState.TerritoryType != 0 &&
        !GameState.IsInPVPArea       &&
        GameState.ContentFinderCondition == 0;

    private static bool SendSortCommand()
    {
        SendSortCondition("armourychest", "id",        ModuleConfig.ArmouryChestID);
        SendSortCondition("armourychest", "itemlevel", ModuleConfig.ArmouryItemLevel);
        SendSortCondition("armourychest", "category",  ModuleConfig.ArmouryCategory);
        ChatManager.Instance().SendMessage("/itemsort execute armourychest");

        SendSortCondition("inventory", "hq",        ModuleConfig.InventoryHQ);
        SendSortCondition("inventory", "id",        ModuleConfig.InventoryID);
        SendSortCondition("inventory", "itemlevel", ModuleConfig.InventoryItemLevel);
        SendSortCondition("inventory", "category",  ModuleConfig.InventoryCategory);

        if (ModuleConfig.InventoryTab == 0)
            ChatManager.Instance().SendMessage("/itemsort condition inventory tab");

        ChatManager.Instance().SendMessage("/itemsort execute inventory");

        if (ModuleConfig.SendNotification)
            NotifyHelper.Instance().NotificationInfo(Lang.Get("AutoSortItems-SortMessage"));
        if (ModuleConfig.SendChat)
            NotifyHelper.Instance().Chat(Lang.Get("AutoSortItems-SortMessage"));

        return true;

        void SendSortCondition(string target, string condition, int setting)
        {
            ChatManager.Instance().SendMessage($"/itemsort condition {target} {condition} {SortOptionsCommand[setting]}");
        }
    }

    public class Config : ModuleConfig
    {
        public int ArmouryCategory;
        public int ArmouryChestID;
        public int ArmouryItemLevel;
        public int InventoryCategory;
        public int InventoryHQ;
        public int InventoryID;
        public int InventoryItemLevel;
        public int InventoryTab;

        public bool SendChat;
        public bool SendNotification = true;
    }
}
