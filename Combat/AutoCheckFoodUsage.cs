using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.Info.Lumina;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace DailyRoutines.ModulesPublic;

internal class AutoCheckFoodUsage : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoCheckFoodUsageTitle"),
        Description = Lang.Get("AutoCheckFoodUsageDescription"),
        Category    = ModuleCategory.Combat
    };

    public override string ModuleName => nameof(AutoCheckFoodUsage);

    private Config config = null!;

    private JobSelectCombo  jobSelectCombo  = null!;
    private ItemSelectCombo itemSelectCombo = null!;
    private ZoneSelectCombo zoneSelectCombo = null!;

    private bool   selectItemIsHQ       = true;
    private string conditionSearchInput = string.Empty;

    private long lastFoodUsageTime;

    private int draggedPresetIndex = -1;

    protected override void Init()
    {
        jobSelectCombo  = new("Job");
        itemSelectCombo = new("Food", Sheets.Food.Values.Where(x => x.ItemAction.Value.Data[0] == WELL_FED_STATUS_ID));
        zoneSelectCombo = new("Zone", Sheets.Zones.Values);

        config = Config.Load(this) ?? new();
        foreach (var checkPoint in Enum.GetValues<FoodCheckpoint>())
            config.EnabledCheckpoints.Add(checkPoint);

        TaskHelper ??= new() { TimeoutMS = 30_000, RetryIntervalMS = 250 };

        GameState.Instance().CountdownStart      += OnCountdownStart;
        IClientState.Instance().TerritoryChanged += OnZoneChanged;
        ICondition.Instance().ConditionChange    += OnConditionChanged;
    }

    protected override void Uninit()
    {
        TaskHelper.Abort();
        GameState.Instance().CountdownStart      -= OnCountdownStart;
        ICondition.Instance().ConditionChange    -= OnConditionChanged;
        IClientState.Instance().TerritoryChanged -= OnZoneChanged;
    }

    protected override unsafe void ConfigUI()
    {
        using var itemWidth = ImRaii.ItemWidth(200f * GlobalUIScale);

        using (ImRaii.Heading1(Lang.Get("AutoCheckFoodUsage-Checkpoint")))
        {
            foreach (var checkPoint in Enum.GetValues<FoodCheckpoint>())
            {
                var state = config.EnabledCheckpoints.Contains(checkPoint);

                if (ImGui.Checkbox(Lang.Get($"AutoCheckFoodUsage-Checkpoint-{(int)checkPoint}"), ref state))
                {
                    if (!config.EnabledCheckpoints.Add(checkPoint))
                        config.EnabledCheckpoints.Remove(checkPoint);
                    config.Save(this);
                }
            }

            if (config.EnabledCheckpoints.Contains(FoodCheckpoint.ConditionChange))
            {
                using (ImRaii.PushIndent())
                {
                    DrawConditionCombo("AutoCheckFoodUsage-WhenConditionBegin", config.ConditionStart);
                    DrawConditionCombo("AutoCheckFoodUsage-WhenConditionEnd",   config.ConditionEnd);
                }
            }
        }

        ImGui.NewLine();

        ImGui.InputInt(Lang.Get("AutoCheckFoodUsage-RefreshThreshold"), ref config.RefreshThreshold);

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.RefreshThreshold = Math.Clamp(config.RefreshThreshold, 0, MAX_FOOD_DURATION_SECONDS);
            config.Save(this);
        }

        ImGuiOm.TextDisabledWrapped(Lang.Get("AutoCheckFoodUsage-RefreshThresholdHelp"));

        ImGui.NewLine();

        using var table = ImRaii.Table("FoodPreset", 4, ImGuiTableFlags.Borders);
        if (!table) return;
        ImGui.TableSetupColumn("###Enabled",                                    ImGuiTableColumnFlags.WidthFixed,   ImGui.GetFrameHeight());
        ImGui.TableSetupColumn(Lang.Get("Food"),                                ImGuiTableColumnFlags.WidthStretch, 30);
        ImGui.TableSetupColumn(Lang.Get("AutoCheckFoodUsage-ZoneRestrictions"), ImGuiTableColumnFlags.WidthStretch, 30);
        ImGui.TableSetupColumn(Lang.Get("AutoCheckFoodUsage-JobRestrictions"),  ImGuiTableColumnFlags.WidthStretch, 30);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableNextColumn();
        if (ImGuiOm.ButtonIconSelectable("AddNewPreset", FontAwesomeIcon.Plus))
            ImGui.OpenPopup("AddNewPresetPopup");

        using (var popup = ImRaii.Popup("AddNewPresetPopup"))
        {
            if (popup)
            {
                using (ImRaii.Heading1(Lang.Get("AutoCheckFoodUsage-AddNewPreset")))
                {
                    if (itemSelectCombo.DrawRadio())
                    {
                        if (!itemSelectCombo.SelectedItem.CanBeHq)
                            selectItemIsHQ = false;
                    }

                    ImGui.SameLine();
                    ImGui.TextUnformatted(Lang.Get("Food"));

                    using (ImRaii.Disabled(!itemSelectCombo.SelectedItem.CanBeHq))
                        ImGui.Checkbox(Lang.Get("HQ"), ref selectItemIsHQ);

                    using (ImRaii.Disabled(itemSelectCombo.SelectedID == 0))
                    {
                        if (ImGuiOm.ButtonStretch(Lang.Get("Add")))
                        {
                            var preset = new FoodUsagePreset
                            {
                                ItemID = itemSelectCombo.SelectedID,
                                IsHQ   = selectItemIsHQ
                            };

                            config.Presets.Add(preset);
                            config.Save(this);
                        }
                    }
                }
            }
        }

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(Lang.Get("Food"));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(Lang.Get("AutoCheckFoodUsage-ZoneRestrictions"));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(Lang.Get("AutoCheckFoodUsage-JobRestrictions"));

        var reorderTargetIndex = -1;

        for (var i = 0; i < config.Presets.Count; i++)
        {
            var       preset = config.Presets[i];
            using var id     = ImRaii.PushId($"{preset.ItemID}_{preset.IsHQ}_{i}");

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var isEnabled = preset.Enabled;

            if (ImGui.Checkbox("", ref isEnabled))
            {
                preset.Enabled = isEnabled;
                config.Save(this);
            }

            ImGui.TableNextColumn();
            var presetName = $"{LuminaGetter.GetRowOrDefault<Item>(preset.ItemID).Name.ToString()} {(preset.IsHQ ? "\uE03C" : string.Empty)}";
            ImGui.Selectable(presetName);

            using (var source = ImRaii.DragDropSource())
            {
                if (source)
                {
                    if (ImGui.SetDragDropPayload(FOOD_PRESET_REORDER_PAYLOAD, []))
                        draggedPresetIndex = i;

                    ImGui.TextUnformatted(presetName);
                }
            }

            using (var target = ImRaii.DragDropTarget())
            {
                if (target)
                {
                    if (ImGui.AcceptDragDropPayload(FOOD_PRESET_REORDER_PAYLOAD).Handle != null)
                        reorderTargetIndex = i;
                }
            }

            using (var context = ImRaii.ContextPopupItem("PresetContextMenu"))
            {
                if (context)
                {
                    if (LuminaGetter.GetRowOrDefault<Item>(preset.ItemID).CanBeHq &&
                        ImGui.MenuItem
                        (
                            Lang.Get
                            (
                                preset.IsHQ ?
                                    "AutoCheckFoodUsage-ChangeToNQ" :
                                    "AutoCheckFoodUsage-ChangeToHQ"
                            )
                        ))
                    {
                        preset.IsHQ ^= true;
                        config.Save(this);
                    }

                    if (ImGui.MenuItem(Lang.Get("Delete")))
                    {
                        config.Presets.Remove(preset);
                        config.Save(this);
                        break;
                    }
                }
            }

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1f);
            zoneSelectCombo.SelectedIDs = preset.Zones;

            if (zoneSelectCombo.DrawCheckbox())
            {
                preset.Zones = zoneSelectCombo.SelectedIDs;
                config.Save(this);
            }

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1f);
            jobSelectCombo.SelectedIDs = preset.ClassJobs;

            if (jobSelectCombo.DrawCheckbox())
            {
                preset.ClassJobs = jobSelectCombo.SelectedIDs;
                config.Save(this);
            }
        }

        if (reorderTargetIndex >= 0 &&
            draggedPresetIndex != reorderTargetIndex)
        {
            var preset = config.Presets[draggedPresetIndex];
            config.Presets.RemoveAt(draggedPresetIndex);
            config.Presets.Insert(reorderTargetIndex, preset);
            config.Save(this);
            draggedPresetIndex = -1;
        }
    }

    private void DrawConditionCombo(string label, HashSet<ConditionFlag> selectedConditions)
    {
        using var combo = ImRaii.Combo
        (
            Lang.Get(label),
            Lang.Get("AutoCheckFoodUsage-SelectedAmount", new Dictionary<string, object> { ["count"] = selectedConditions.Count }),
            ImGuiComboFlags.HeightLarge
        );
        if (!combo)
            return;

        if (ImGui.IsWindowAppearing())
            conditionSearchInput = string.Empty;

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("###ConditionSearch", Lang.Get("PleaseSearch"), ref conditionSearchInput, 128);
        ImGui.Separator();

        foreach (var condition in CONDITIONS)
        {
            var name = condition.ToString();
            if (!string.IsNullOrWhiteSpace(conditionSearchInput) && !name.Contains(conditionSearchInput, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!ImGui.Selectable(name, selectedConditions.Contains(condition), ImGuiSelectableFlags.DontClosePopups))
                continue;

            if (!selectedConditions.Remove(condition))
                selectedConditions.Add(condition);

            config.Save(this);
        }
    }

    private unsafe bool EnqueueFoodRefresh()
    {
        if (GameState.IsInPVPArea)
            return true;

        if (!IsValidState())
            return false;

        if (lastFoodUsageTime != 0 && Environment.TickCount64 - lastFoodUsageTime < FOOD_USAGE_COOLDOWN_SECONDS * 1000)
            return true;

        var preset = GetValidPreset();
        if (preset == null)
            return true;

        var expectedParam = GetFoodParam(preset);
        TryGetWellFedParam(out var previousParam, out var previousRemainingTime);
        if (previousParam == expectedParam && previousRemainingTime > Math.Clamp(config.RefreshThreshold, 0, MAX_FOOD_DURATION_SECONDS))
            return true;

        var actionID = preset.IsHQ ?
                           preset.ItemID + HQ_ITEM_OFFSET :
                           preset.ItemID;
        if (ActionManager.Instance()->GetActionStatus(ActionType.Item, actionID) != 0)
            return false;

        if (!UseActionManager.Instance().UseActionLocation
            (
                ActionType.Item,
                actionID,
                extraParam: 0xFFFF
            ))
            return false;

        lastFoodUsageTime = Environment.TickCount64;
        var territory = GameState.TerritoryType;
        var job       = IObjectTable.Instance().LocalPlayer.ClassJob.RowId;
        TaskHelper.Enqueue
        (
            () => CheckFoodState(actionID, expectedParam, previousParam, previousRemainingTime, territory, job),
            timeoutMS: 5000
        );
        return true;
    }

    private static bool CheckFoodState
    (
        uint  actionID,
        uint  expectedParam,
        uint  previousParam,
        float previousRemainingTime,
        uint  territory,
        uint  job
    )
    {
        if (GameState.TerritoryType != territory || IObjectTable.Instance().LocalPlayer is not { } player || player.ClassJob.RowId != job)
            return true;

        if (!TryGetWellFedParam(out var currentParam, out var remainingTime) ||
            currentParam  != expectedParam                                   ||
            remainingTime <= 0                                               ||
            (previousParam == currentParam && remainingTime <= previousRemainingTime))
            return false;

        NotifyHelper.Instance().Chat
        (
            Lang.GetSe
            (
                "AutoCheckFoodUsage-NoticeMessage",
                new Dictionary<string, object>
                {
                    ["item"] = ReadOnlySeString.CreateItemLink(actionID % HQ_ITEM_OFFSET, actionID >= HQ_ITEM_OFFSET)
                }
            )
        );
        return true;
    }

    private static unsafe bool TryGetWellFedParam
    (
        out uint  foodParam,
        out float remainingTime
    )
    {
        foodParam     = 0;
        remainingTime = 0;

        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null)
            return false;

        if (!LocalPlayerState.HasStatus(WELL_FED_STATUS_ID, out var statusIndex))
            return false;

        var status = localPlayer->StatusManager.Status[statusIndex];

        foodParam     = status.Param;
        remainingTime = status.RemainingTime;
        return true;
    }

    private void OnCountdownStart(CountdownInfo countdown)
    {
        if (!config.EnabledCheckpoints.Contains(FoodCheckpoint.CountdownStart) ||
            GameState.IsInPVPArea                                              ||
            TaskHelper.IsBusy)
            return;

        TaskHelper.Enqueue(EnqueueFoodRefresh);
    }

    private void OnZoneChanged
    (
        uint territory
    )
    {
        TaskHelper.Abort();

        if (!config.EnabledCheckpoints.Contains(FoodCheckpoint.TerritoryChange) ||
            GameState.IsInPVPArea)
            return;

        TaskHelper.Enqueue(EnqueueFoodRefresh);
    }

    private void OnConditionChanged
    (
        ConditionFlag flag,
        bool          value
    )
    {
        if (!config.EnabledCheckpoints.Contains(FoodCheckpoint.ConditionChange) ||
            GameState.IsInPVPArea                                               ||
            TaskHelper.IsBusy                                                   ||
            !(value ?
                  config.ConditionStart :
                  config.ConditionEnd).Contains(flag))
            return;

        TaskHelper.Enqueue(EnqueueFoodRefresh);
    }

    private static bool IsValidState() =>
        !ICondition.Instance().IsBetweenAreas                    &&
        !ICondition.Instance().IsOccupiedInEvent                 &&
        !ICondition.Instance().IsCasting                         &&
        IObjectTable.Instance().LocalPlayer is { IsDead: false } &&
        UIModule.IsScreenReady();

    private static uint GetFoodParam
    (
        FoodUsagePreset preset
    )
    {
        var action = LuminaGetter.GetRowOrDefault<Item>(preset.ItemID).ItemAction.Value;
        return preset.IsHQ ?
                   action.DataHQ[1] + HQ_FOOD_PARAM_OFFSET :
                   action.Data[1];
    }

    private unsafe FoodUsagePreset? GetValidPreset()
    {
        var inventoryManager = InventoryManager.Instance();
        var zone             = GameState.TerritoryType;
        if (inventoryManager == null ||
            zone             == 0    ||
            LocalPlayerState.Object is not { } player)
            return null;

        FoodUsagePreset? unrestrictedPreset = null;

        foreach (var preset in config.Presets)
        {
            if (!preset.Enabled                                                                    ||
                (preset.Zones.Count     != 0 && !preset.Zones.Contains(zone))                      ||
                (preset.ClassJobs.Count != 0 && !preset.ClassJobs.Contains(player.ClassJob.RowId)) ||
                !LuminaGetter.TryGetRow<Item>(preset.ItemID, out var item)                         ||
                item.ItemAction.Value.Data[0] != WELL_FED_STATUS_ID                                ||
                (preset.IsHQ && !item.CanBeHq)                                                     ||
                inventoryManager->GetInventoryItemCount(preset.ItemID, preset.IsHQ) == 0)
                continue;

            if (preset.Zones.Count != 0)
                return preset;

            unrestrictedPreset ??= preset;
        }

        return unrestrictedPreset;
    }

    private class FoodUsagePreset
    {
        public uint          ItemID    { get; init; }
        public bool          IsHQ      { get; set; } = true;
        public HashSet<uint> Zones     { get; set; } = [];
        public HashSet<uint> ClassJobs { get; set; } = [];
        public bool          Enabled   { get; set; } = true;
    }

    private class Config : ModuleConfig
    {
        public override uint Version { get; set; } = 2;

        public HashSet<FoodCheckpoint> EnabledCheckpoints = [];
        public HashSet<ConditionFlag>  ConditionEnd       = [];
        public HashSet<ConditionFlag>  ConditionStart     = [];
        public List<FoodUsagePreset>   Presets            = [];

        public int RefreshThreshold = DEFAULT_REFRESH_THRESHOLD_SECONDS;
    }

    private enum FoodCheckpoint
    {
        TerritoryChange,
        CountdownStart,
        ConditionChange
    }

    #region 常量

    private const string FOOD_PRESET_REORDER_PAYLOAD = "AutoCheckFoodUsagePresetReorder";

    private const int  FOOD_USAGE_COOLDOWN_SECONDS       = 10;
    private const uint WELL_FED_STATUS_ID                = 48;
    private const int  MAX_FOOD_DURATION_SECONDS         = 3600;
    private const uint HQ_ITEM_OFFSET                    = 1_000_000;
    private const uint HQ_FOOD_PARAM_OFFSET              = 10_000;
    private const int  DEFAULT_REFRESH_THRESHOLD_SECONDS = 600;

    private static readonly ConditionFlag[] CONDITIONS =
        [.. Enum.GetValues<ConditionFlag>().Where(x => x is not (ConditionFlag.None or ConditionFlag.NormalConditions))];

    #endregion
}
