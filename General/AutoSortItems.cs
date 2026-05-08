using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoSortItems : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoSortItemsTitle"),
        Description = Lang.Get("AutoSortItemsDescription"),
        Category    = ModuleCategory.General,
        Author      = ["那年雪落"]
    };
    
    private readonly string[] sortOptions = [Lang.Get("Descending"), Lang.Get("Ascending")];
    private readonly string[] tabOptions  = [Lang.Get("AutoSortItems-Splited"), Lang.Get("AutoSortItems-Merged")];

    private Config config = null!;

    protected override void Init()
    {
        config =   Config.Load(this) ?? new();
        TaskHelper   ??= new() { TimeoutMS = 15_000 };

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
    }
    
    protected override void Uninit() =>
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;

    protected override void ConfigUI()
    {
        if (ImGui.Button(LuminaWrapper.GetAddonText(1389)))
            TaskHelper.Enqueue(CheckCanSort);

        ImGui.NewLine();

        if (ImGui.Checkbox(Lang.Get("SendChat"), ref config.SendChat))
            config.Save(this);

        ImGui.SameLine();
        if (ImGui.Checkbox(Lang.Get("SendNotification"), ref config.SendNotification))
            config.Save(this);

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

        DrawTableRow("兵装库 ID", "ID",              ref config.ArmouryChestID,   sortOptions);
        DrawTableRow("兵装库等级",  Lang.Get("Level"), ref config.ArmouryItemLevel, sortOptions);
        DrawTableRow("兵装库类型",  typeText,          ref config.ArmouryCategory,  sortOptions, Lang.Get("AutoSortItems-ArmouryCategoryDesc"));

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(LuminaWrapper.GetAddonText(12209));

        DrawTableRow("背包 HQ", "HQ",                              ref config.InventoryHQ,        sortOptions);
        DrawTableRow("背包 ID", "ID",                              ref config.InventoryID,        sortOptions);
        DrawTableRow("背包等级",  Lang.Get("Level"),                 ref config.InventoryItemLevel, sortOptions);
        DrawTableRow("背包类型",  typeText,                          ref config.InventoryCategory,  sortOptions, Lang.Get("AutoSortItems-InventoryCategoryDesc"));
        DrawTableRow("背包分栏",  Lang.Get("AutoSortItems-Splited"), ref config.InventoryTab,       tabOptions,  Lang.Get("AutoSortItems-InventoryTabDesc"));
    }
    
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
            config.Save(this);
    }

    private void OnZoneChanged(uint u)
    {
        TaskHelper.Abort();

        if (GameState.TerritoryType == 0) return;
        TaskHelper.Enqueue(CheckCanSort);
    }

    private bool CheckCanSort()
    {
        if (!GameState.IsLoggedIn || !UIModule.IsScreenReady() || DService.Instance().Condition.IsOccupiedInEvent) return false;

        if (!DService.Instance().Condition.IsIdle || !IsInValidZone())
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

    private bool SendSortCommand()
    {
        SendSortCondition("armourychest", "id",        config.ArmouryChestID);
        SendSortCondition("armourychest", "itemlevel", config.ArmouryItemLevel);
        SendSortCondition("armourychest", "category",  config.ArmouryCategory);
        ChatManager.Instance().SendMessage("/itemsort execute armourychest");

        SendSortCondition("inventory", "hq",        config.InventoryHQ);
        SendSortCondition("inventory", "id",        config.InventoryID);
        SendSortCondition("inventory", "itemlevel", config.InventoryItemLevel);
        SendSortCondition("inventory", "category",  config.InventoryCategory);

        if (config.InventoryTab == 0)
            ChatManager.Instance().SendMessage("/itemsort condition inventory tab");

        ChatManager.Instance().SendMessage("/itemsort execute inventory");

        if (config.SendNotification)
            NotifyHelper.Instance().NotificationInfo(Lang.Get("AutoSortItems-SortMessage"));
        if (config.SendChat)
            NotifyHelper.Instance().Chat(Lang.Get("AutoSortItems-SortMessage"));

        return true;

        void SendSortCondition(string target, string condition, int setting)
        {
            ChatManager.Instance().SendMessage($"/itemsort condition {target} {condition} {SortOptionsCommand[setting]}");
        }
    }

    private class Config : ModuleConfig
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
    
    #region 常量
    
    private static readonly string[] SortOptionsCommand = ["des", "asc"];
    
    #endregion
}
