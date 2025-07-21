using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DailyRoutines.Modules;

public class AutoMovePetPosition : DailyModuleBase
{
    private static Config                          ModuleConfig       = null!;
    private        DateTime                        BattleStartTime    = DateTime.MinValue;
    private static string                          ContentSearchInput = string.Empty;
    private        bool                            IsPicking;
    private        (uint territoryKey, int index)? currentPickingRow;

    private static readonly Dictionary<uint, ContentFinderCondition> ValidContents;

    private static readonly HashSet<uint> ValidJobs = [26, 27, 28];

    static AutoMovePetPosition()
    {
        ValidContents = PresetSheet.Contents
                                  .Where(pair => pair.Value.ContentMemberType.RowId == 3) // 只要 8 人本
                                  .ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    public override ModuleInfo Info { get; } = new()
    {
        Author = ["Wotou"],
        Title = GetLoc("AutoMovePetPositionTitle"),
        Description = GetLoc("AutoMovePetPositionDescription"),
        Category = ModuleCategories.Combat
    };

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        TaskHelper ??= new TaskHelper { TimeLimitMS = 30_000 };

        DService.ClientState.TerritoryChanged += OnTerritoryChanged;
        DService.DutyState.DutyRecommenced += OnDutyRecommenced;
        DService.Condition.ConditionChange += OnConditionChanged;

        TaskHelper.Enqueue(SchedulePetMovements);
    }

    protected override void ConfigUI()
    {
        var tableWidth = (ImGui.GetContentRegionAvail() * 0.9f) with { Y = 0 };
        
        using var table = ImRaii.Table("PositionSchedulesTable", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg, tableWidth);
        if (!table) return;

        ImGui.TableSetupColumn("新增", ImGuiTableColumnFlags.WidthFixed,   ImGui.GetTextLineHeightWithSpacing());
        ImGui.TableSetupColumn("区域", ImGuiTableColumnFlags.WidthStretch, 20);
        ImGui.TableSetupColumn("备注", ImGuiTableColumnFlags.WidthStretch, 25);
        ImGui.TableSetupColumn("延迟", ImGuiTableColumnFlags.WidthFixed,   50f * GlobalFontScale);
        ImGui.TableSetupColumn("坐标", ImGuiTableColumnFlags.WidthStretch, 20);
        ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthStretch, 15);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);

        // (1) 第一列：添加
        ImGui.TableNextColumn();
        if (ImGuiOm.ButtonIconSelectable("AddNewPreset", FontAwesomeIcon.Plus))
        {
            // 添加新 TerritoryId (默认等于 1)
            if (!ModuleConfig.PositionSchedules.ContainsKey(1))
                ModuleConfig.PositionSchedules[1] = new List<PositionSchedule>();

            ModuleConfig.PositionSchedules[1].Add(new PositionSchedule(Guid.NewGuid().ToString())
            {
                Enabled = true,
                ZoneID = 1,
                DelayS = 0,
                Position = default
            });
            SaveConfig(ModuleConfig);

            TaskHelper.Abort();
            TaskHelper.Enqueue(SchedulePetMovements);
        }

        // (2) 其余表头
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(GetLoc("Zone"));
        
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(GetLoc("Note"));
        
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(FontAwesomeIcon.Clock.ToIconString());
        ImGuiOm.TooltipHover($"{GetLoc("AutoMovePetPosition-Delay")} (s)");
        
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(GetLoc("Position"));
        
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(GetLoc("Operation"));

        foreach (var (zoneID, scheduleList) in ModuleConfig.PositionSchedules.ToArray())
        {
            // 如果当前列表为空，就跳过
            if (scheduleList.Count == 0) continue;

            for (var i = 0; i < scheduleList.Count; i++)
            {
                var schedule = scheduleList[i];

                using var id = ImRaii.PushId(schedule.Guid);
                ImGui.TableNextRow();

                // 0) 启用 CheckBox
                var enabled = schedule.Enabled;
                ImGui.TableNextColumn();
                if (ImGui.Checkbox("##启用", ref enabled))
                {
                    schedule.Enabled = enabled;
                    SaveConfig(ModuleConfig);

                    TaskHelper.Abort();
                    TaskHelper.Enqueue(SchedulePetMovements);
                }

                // 1) ZoneID (区域ID)
                var editingZoneID  = schedule.ZoneID;
                if (!LuminaGetter.TryGetRow<TerritoryType>(editingZoneID, out var zone)) continue;
                var editingContent = zone.ContentFinderCondition.ValueNullable;

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                if (ContentSelectCombo(ref editingContent, ref ContentSearchInput, ValidContents))
                {
                    editingZoneID = editingContent!.Value.TerritoryType.RowId;

                    var scheduleCopy = schedule.Copy();
                    scheduleCopy.ZoneID = editingZoneID;

                    scheduleList.Remove(schedule);

                    ModuleConfig.PositionSchedules.TryAdd(editingZoneID, new());
                    ModuleConfig.PositionSchedules[editingZoneID].Add(scheduleCopy);
                    SaveConfig(ModuleConfig);

                    TaskHelper.Abort();
                    TaskHelper.Enqueue(SchedulePetMovements);
                    continue;
                }

                // 2) 备注
                ImGui.TableNextColumn();
                var remark = schedule.Note;
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                ImGui.InputText("##备注", ref remark, 256);
                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    if (remark != schedule.Note)
                    {
                        schedule.Note = remark;
                        SaveConfig(ModuleConfig);
                        TaskHelper.Abort();
                        TaskHelper.Enqueue(SchedulePetMovements);
                    }
                }

                // 3) TimeInSeconds
                ImGui.TableNextColumn();
                var timeInSeconds = schedule.DelayS;
                ImGui.SetNextItemWidth(50f * GlobalFontScale);
                ImGui.InputInt("##延迟", ref timeInSeconds, 0, 0);
                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    timeInSeconds = Math.Max(0, timeInSeconds);
                    if (timeInSeconds != schedule.DelayS)
                    {
                        schedule.DelayS = timeInSeconds;
                        SaveConfig(ModuleConfig);
                        TaskHelper.Abort();
                        TaskHelper.Enqueue(SchedulePetMovements);
                    }
                }

                // 4) 坐标
                var pos = schedule.Position;
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(125f * GlobalFontScale);
                ImGui.InputFloat2("##坐标", ref pos, "%.1f");
                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    schedule.Position = pos;
                    SaveConfig(ModuleConfig);

                    TaskHelper.Abort();
                    TaskHelper.Enqueue(SchedulePetMovements);
                }

                ImGui.SameLine();
                if (ImGuiOm.ButtonIcon("当前坐标", FontAwesomeIcon.Crosshairs,
                                       GetLoc("AutoMovePetPosition-GetCurrent")))
                {
                    if (DService.ObjectTable.LocalPlayer is { } localPlayer)
                    {
                        schedule.Position = localPlayer.Position.ToVector2();
                        SaveConfig(ModuleConfig);

                        TaskHelper.Abort();
                        TaskHelper.Enqueue(SchedulePetMovements);
                    }
                }

                // 按钮：取鼠标
                ImGui.SameLine();
                if (!IsPicking)
                {
                    if (ImGuiOm.ButtonIcon("鼠标位置", FontAwesomeIcon.MousePointer,
                                           GetLoc("AutoMovePetPosition-GetMouseHelp")))
                    {
                        IsPicking         = true;
                        currentPickingRow = (zoneID, i);
                    }
                }
                else
                {
                    if (ImGuiOm.ButtonIcon("取消鼠标位置读取", FontAwesomeIcon.Times, GetLoc("Cancel")))
                    {
                        IsPicking         = false;
                        currentPickingRow = null;
                    }
                }

                if (IsPicking)
                {
                    if ((ImGui.IsKeyDown(ImGuiKey.LeftAlt)  || ImGui.IsKeyDown(ImGuiKey.RightAlt)) &&
                        (ImGui.IsKeyDown(ImGuiKey.LeftCtrl) || ImGui.IsKeyDown(ImGuiKey.RightCtrl)))
                    {
                        if (DService.Gui.ScreenToWorld(ImGui.GetMousePos(), out var worldPos))
                        {
                            var currentPickingZone  = currentPickingRow?.territoryKey ?? 0;
                            var currentPickingIndex = currentPickingRow?.index        ?? -1;
                            if (currentPickingZone == 0 || currentPickingIndex == -1) continue;

                            ModuleConfig.PositionSchedules
                                [currentPickingZone][currentPickingIndex].Position = worldPos.ToVector2();
                            SaveConfig(ModuleConfig);

                            TaskHelper.Abort();
                            TaskHelper.Enqueue(SchedulePetMovements);

                            IsPicking         = false;
                            currentPickingRow = null;
                        }
                    }
                }


                // 5) 操作（删除）
                ImGui.TableNextColumn();
                if (ImGuiOm.ButtonIcon("删除", FontAwesomeIcon.TrashAlt, $"{GetLoc("Delete")} (Ctrl)"))
                {
                    if (ImGui.IsKeyDown(ImGuiKey.LeftCtrl))
                    {
                        scheduleList.RemoveAt(i);
                        SaveConfig(ModuleConfig);

                        TaskHelper.Abort();
                        TaskHelper.Enqueue(SchedulePetMovements);

                        continue;
                    }
                }

                ImGui.SameLine();
                if (ImGuiOm.ButtonIcon("导出", FontAwesomeIcon.FileExport, GetLoc("Export")))
                    ExportToClipboard(schedule);

                ImGui.SameLine();
                if (ImGuiOm.ButtonIcon("导入", FontAwesomeIcon.FileImport, GetLoc("Import")))
                {
                    var importedSchedule = ImportFromClipboard<PositionSchedule>();
                    if (importedSchedule == null) return;
                    
                    var importZoneID = importedSchedule.ZoneID;
                    ModuleConfig.PositionSchedules.TryAdd(importZoneID, []);

                    if (!ModuleConfig.PositionSchedules[importZoneID].Contains(importedSchedule))
                    {
                        scheduleList.Add(importedSchedule);
                        SaveConfig(ModuleConfig);

                        TaskHelper.Abort();
                        TaskHelper.Enqueue(SchedulePetMovements);
                    }
                }
            }
        }
    }

    private void OnTerritoryChanged(ushort zone)
    {
        ResetBattleTimer();
        
        TaskHelper.Abort();
        TaskHelper.Enqueue(SchedulePetMovements);
    }

    private void OnDutyRecommenced(object? sender, ushort e)
    {
        ResetBattleTimer();
        
        TaskHelper.Abort();
        TaskHelper.Enqueue(SchedulePetMovements);
    }

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (value && flag == ConditionFlag.InCombat)
        {
            ResetBattleTimer();
            StartBattleTimer();
            
            TaskHelper.Abort();
            TaskHelper.Enqueue(SchedulePetMovements);
        }
    }

    private void StartBattleTimer()
    {
        BattleStartTime = DateTime.Now;
    }

    private void ResetBattleTimer()
    {
        BattleStartTime = DateTime.MinValue;
    }

    private void SchedulePetMovements()
    {
        if (!CheckIsEightPlayerDuty()) return;

        // 获取当前区域
        var zoneID = DService.ClientState.TerritoryType;
        if (!ModuleConfig.PositionSchedules.TryGetValue(zoneID, out var schedulesForThisDuty)) return;

        if (DService.ObjectTable.LocalPlayer is { } localPlayer)
        {
            if (!ValidJobs.Contains(localPlayer.ClassJob.RowId)) return;
            
            // 只选启用的
            var enabledSchedules     = schedulesForThisDuty.Where(x => x.Enabled).ToList();
            var elapsedTimeInSeconds = (DateTime.Now - BattleStartTime).TotalSeconds;

            if (DService.Condition[ConditionFlag.InCombat])
            {
                // 在所有 TimeInSeconds <= 已过战斗时间中，找最大
                var bestSchedule = enabledSchedules
                                   .Where(x => x.DelayS <= elapsedTimeInSeconds)
                                   .OrderByDescending(x => x.DelayS)
                                   .FirstOrDefault();
                
                if (bestSchedule != null) 
                    TaskHelper.Enqueue(() => MovePetToLocation(bestSchedule.Position));
            }
            else
            {
                // 没在战斗，如果有 TimeInSeconds == 0 的，就执行第一条
                var scheduleForZero = enabledSchedules
                    .FirstOrDefault(x => x.DelayS == 0);

                if (scheduleForZero != null)
                    TaskHelper.Enqueue(() => MovePetToLocation(scheduleForZero.Position));
            }
        }

        // 循环执行
        TaskHelper.DelayNext(1_000);
        TaskHelper.Enqueue(SchedulePetMovements);
    }

    private unsafe void MovePetToLocation(Vector2 position)
    {
        if (!CheckIsEightPlayerDuty()) return;
        if (DService.ObjectTable.LocalPlayer is not { } player) return;
        if (!ValidJobs.Contains(player.ClassJob.RowId)) return;
        
        var pet = CharacterManager.Instance()->LookupPetByOwnerObject(player.ToStruct());
        if (pet == null) return;

        var groundY  = pet->Position.Y;
        var location = position.ToVector3(groundY);
        if (MovementManager.TryDetectGroundDownwards(position.ToVector3(groundY + 5f), out var groundPos) ?? false)
            location = groundPos.Point;

        TaskHelper.Enqueue(() => ExecuteCommandManager.ExecuteCommandComplexLocation(ExecuteCommandComplexFlag.PetAction, location, 3));
    }

    private bool CheckIsEightPlayerDuty()
    {
        var zoneID = DService.ClientState.TerritoryType;
        if (zoneID == 0) return false;
        
        var zoneData = LuminaGetter.GetRow<TerritoryType>(zoneID);
        if (zoneData == null) return false;
        if (zoneData.Value.RowId == 0) return false;

        var contentData = zoneData.Value.ContentFinderCondition.Value;
        if (contentData.RowId == 0) return false;

        return contentData.ContentMemberType.RowId == 3;
    }

    protected override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnTerritoryChanged;
        DService.DutyState.DutyRecommenced -= OnDutyRecommenced;
        DService.Condition.ConditionChange -= OnConditionChanged;
        
        base.Uninit();
    }

    private class Config : ModuleConfiguration
    {
        // Zone → Schedules
        public Dictionary<uint, List<PositionSchedule>> PositionSchedules = new();
    }

    public class PositionSchedule : IEquatable<PositionSchedule>
    {
        public bool    Enabled  { get; set; } = true;         // 是否启用
        public uint    ZoneID   { get; set; }                 // 区域 ID
        public string  Note     { get; set; } = string.Empty; // 备注
        public int     DelayS   { get; set; }                 // 战斗开始后的时间点 (秒)
        public Vector2 Position { get; set; }                 // 坐标
        public string  Guid     { get; set; }
        
        public PositionSchedule(string guid) => Guid = guid;

        public PositionSchedule Copy() =>
            new(Guid)
            {
                Enabled  = Enabled,
                ZoneID   = ZoneID,
                Note     = Note,
                DelayS   = DelayS,
                Position = Position,
            };

        public bool Equals(PositionSchedule? other)
        {
            if(other is null) return false;
            if(ReferenceEquals(this, other)) return true;
            return Guid == other.Guid;
        }

        public override string ToString() => Guid;

        public override bool Equals(object? obj)
        {
            if (obj is not PositionSchedule other) return false;
            return Equals(other);
        }

        public override int GetHashCode() => Guid.GetHashCode();

        public static bool operator ==(PositionSchedule? left, PositionSchedule? right) => Equals(left, right);

        public static bool operator !=(PositionSchedule? left, PositionSchedule? right) => !Equals(left, right);
    }
}
