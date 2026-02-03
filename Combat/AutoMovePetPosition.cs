using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public class AutoMovePetPosition : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoMovePetPositionTitle"),
        Description = GetLoc("AutoMovePetPositionDescription"),
        Category    = ModuleCategories.Combat,
        Author      = ["Wotou"],
    };

    private static Config ModuleConfig = null!;
    
    private static readonly HashSet<uint> ValidJobs = [26, 27, 28];
    
    private static readonly ContentSelectCombo ContentSelectCombo = new("Content");

    private static DateTime BattleStartTime = DateTime.MinValue;
    
    private static bool                            IsPicking;
    private static (uint territoryKey, int index)? CurrentPickingRow;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        TaskHelper ??= new() { TimeoutMS = 30_000 };
        
        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        DService.Instance().DutyState.DutyRecommenced    += OnDutyRecommenced;
        DService.Instance().Condition.ConditionChange    += OnConditionChanged;

        TaskHelper.Enqueue(SchedulePetMovements);
    }
    
    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        DService.Instance().DutyState.DutyRecommenced    -= OnDutyRecommenced;
        DService.Instance().Condition.ConditionChange    -= OnConditionChanged;
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
        
        ImGui.TableNextColumn();
        if (ImGuiOm.ButtonIconSelectable("AddNewPreset", FontAwesomeIcon.Plus))
        {
            if (!ModuleConfig.PositionSchedules.ContainsKey(1))
                ModuleConfig.PositionSchedules[1] = [];

            ModuleConfig.PositionSchedules[1].Add(new(Guid.NewGuid().ToString())
            {
                Enabled  = true,
                ZoneID   = 1,
                DelayS   = 0,
                Position = default
            });
            SaveConfig(ModuleConfig);

            TaskHelper.Abort();
            TaskHelper.Enqueue(SchedulePetMovements);
        }
        
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
            if (scheduleList.Count == 0) continue;

            for (var i = 0; i < scheduleList.Count; i++)
            {
                var schedule = scheduleList[i];
                
                using var id = ImRaii.PushId(schedule.GUID);
                
                ImGui.TableNextRow();
                
                var enabled = schedule.Enabled;
                ImGui.TableNextColumn();
                if (ImGui.Checkbox("##启用", ref enabled))
                {
                    schedule.Enabled = enabled;
                    SaveConfig(ModuleConfig);

                    TaskHelper.Abort();
                    TaskHelper.Enqueue(SchedulePetMovements);
                }
                
                var editingZoneID  = schedule.ZoneID;
                if (!LuminaGetter.TryGetRow<TerritoryType>(editingZoneID, out var zone)) continue;

                ContentSelectCombo.SelectedID = zone.ContentFinderCondition.RowId;
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                if (ContentSelectCombo.DrawRadio())
                {
                    editingZoneID = ContentSelectCombo.SelectedItem.TerritoryType.RowId;

                    var scheduleCopy = schedule.Copy();
                    scheduleCopy.ZoneID = editingZoneID;

                    scheduleList.Remove(schedule);

                    ModuleConfig.PositionSchedules.TryAdd(editingZoneID, []);
                    ModuleConfig.PositionSchedules[editingZoneID].Add(scheduleCopy);
                    SaveConfig(ModuleConfig);

                    TaskHelper.Abort();
                    TaskHelper.Enqueue(SchedulePetMovements);
                    continue;
                }

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
                
                ImGui.TableNextColumn();
                var timeInSeconds = schedule.DelayS;
                ImGui.SetNextItemWidth(50f * GlobalFontScale);
                ImGui.InputInt("##延迟", ref timeInSeconds);
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
                
                var pos = schedule.Position;
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(125f * GlobalFontScale);
                ImGui.InputFloat2("##坐标", ref pos, format: "%.1f");
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
                    if (DService.Instance().ObjectTable.LocalPlayer is { } localPlayer)
                    {
                        schedule.Position = localPlayer.Position.ToVector2();
                        SaveConfig(ModuleConfig);

                        TaskHelper.Abort();
                        TaskHelper.Enqueue(SchedulePetMovements);
                    }
                }
                
                ImGui.SameLine();
                if (!IsPicking)
                {
                    if (ImGuiOm.ButtonIcon("鼠标位置", FontAwesomeIcon.MousePointer, GetLoc("AutoMovePetPosition-GetMouseHelp")))
                    {
                        IsPicking         = true;
                        CurrentPickingRow = (zoneID, i);
                    }
                }
                else
                {
                    if (ImGuiOm.ButtonIcon("取消鼠标位置读取", FontAwesomeIcon.Times, GetLoc("Cancel")))
                    {
                        IsPicking         = false;
                        CurrentPickingRow = null;
                    }
                }

                if (IsPicking)
                {
                    if ((ImGui.IsKeyDown(ImGuiKey.LeftAlt)  || ImGui.IsKeyDown(ImGuiKey.RightAlt)) &&
                        (ImGui.IsKeyDown(ImGuiKey.LeftCtrl) || ImGui.IsKeyDown(ImGuiKey.RightCtrl)))
                    {
                        if (DService.Instance().GameGUI.ScreenToWorld(ImGui.GetMousePos(), out var worldPos))
                        {
                            var currentPickingZone  = CurrentPickingRow?.territoryKey ?? 0;
                            var currentPickingIndex = CurrentPickingRow?.index        ?? -1;
                            if (currentPickingZone == 0 || currentPickingIndex == -1) continue;

                            ModuleConfig.PositionSchedules
                                [currentPickingZone][currentPickingIndex].Position = worldPos.ToVector2();
                            SaveConfig(ModuleConfig);

                            TaskHelper.Abort();
                            TaskHelper.Enqueue(SchedulePetMovements);

                            IsPicking         = false;
                            CurrentPickingRow = null;
                        }
                    }
                }
                
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

    private void OnZoneChanged(ushort zone)
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

    private static void StartBattleTimer() => 
        BattleStartTime = StandardTimeManager.Instance().Now;

    private static void ResetBattleTimer() => 
        BattleStartTime = DateTime.MinValue;

    private void SchedulePetMovements()
    {
        if (!CheckIsEightPlayerDuty()) return;
        
        var zoneID = GameState.TerritoryType;
        if (!ModuleConfig.PositionSchedules.TryGetValue(zoneID, out var schedulesForThisDuty)) return;

        if (DService.Instance().ObjectTable.LocalPlayer is { } localPlayer)
        {
            if (!ValidJobs.Contains(localPlayer.ClassJob.RowId)) return;
            
            var enabledSchedules     = schedulesForThisDuty.Where(x => x.Enabled).ToList();
            var elapsedTimeInSeconds = (StandardTimeManager.Instance().Now - BattleStartTime).TotalSeconds;

            if (DService.Instance().Condition[ConditionFlag.InCombat])
            {
                var bestSchedule = enabledSchedules
                                   .Where(x => x.DelayS <= elapsedTimeInSeconds)
                                   .OrderByDescending(x => x.DelayS)
                                   .FirstOrDefault();
                
                if (bestSchedule != null) 
                    TaskHelper.Enqueue(() => MovePetToLocation(bestSchedule.Position));
            }
            else
            {
                var scheduleForZero = enabledSchedules
                    .FirstOrDefault(x => x.DelayS == 0);

                if (scheduleForZero != null)
                    TaskHelper.Enqueue(() => MovePetToLocation(scheduleForZero.Position));
            }
        }


        TaskHelper.DelayNext(1_000);
        TaskHelper.Enqueue(SchedulePetMovements);
    }

    private unsafe void MovePetToLocation(Vector2 position)
    {
        if (!CheckIsEightPlayerDuty()) return;
        if (DService.Instance().ObjectTable.LocalPlayer is not { } player) return;
        if (!ValidJobs.Contains(player.ClassJob.RowId)) return;
        
        var pet = CharacterManager.Instance()->LookupPetByOwnerObject(player.ToStruct());
        if (pet == null) return;

        var groundY  = pet->Position.Y;
        var location = position.ToVector3(groundY);
        if (MovementManager.TryDetectGroundDownwards(position.ToVector3(groundY + 5f), out var groundPos) ?? false)
            location = groundPos.Point;

        TaskHelper.Enqueue(() => ExecuteCommandManager.Instance().ExecuteCommandComplexLocation(ExecuteCommandComplexFlag.PetAction, location, 3));
    }

    private static bool CheckIsEightPlayerDuty()
    {
        var zoneID = GameState.TerritoryType;
        if (zoneID == 0) return false;
        
        var zoneData = LuminaGetter.GetRow<TerritoryType>(zoneID);
        if (zoneData == null) return false;
        if (zoneData.Value.RowId == 0) return false;

        var contentData = zoneData.Value.ContentFinderCondition.Value;
        if (contentData.RowId == 0) return false;

        return contentData.ContentMemberType.RowId == 3;
    }
    
    private class Config : ModuleConfiguration
    {
        public Dictionary<uint, List<PositionSchedule>> PositionSchedules = new();
    }

    public class PositionSchedule(string guid) : IEquatable<PositionSchedule>
    {
        public bool    Enabled  { get; set; } = true;
        public uint    ZoneID   { get; set; }
        public string  Note     { get; set; } = string.Empty;
        public int     DelayS   { get; set; }
        public Vector2 Position { get; set; }
        public string  GUID     { get; set; } = guid;

        public PositionSchedule Copy() =>
            new(GUID)
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
            return GUID == other.GUID;
        }

        public override string ToString() => 
            GUID;

        public override bool Equals(object? obj)
        {
            if (obj is not PositionSchedule other) return false;
            return Equals(other);
        }

        public override int GetHashCode() => 
            GUID.GetHashCode();

        public static bool operator ==(PositionSchedule? left, PositionSchedule? right) => 
            Equals(left, right);

        public static bool operator !=(PositionSchedule? left, PositionSchedule? right) => 
            !Equals(left, right);
    }
}
