global using static DailyRoutines.Infos.Widgets;
global using static OmenTools.Helpers.HelpersOm;
global using static DailyRoutines.Infos.Extensions;
global using static OmenTools.Infos.InfosOm;
global using static OmenTools.Helpers.ThrottlerHelper;
global using static DailyRoutines.Managers.Configuration;
global using static DailyRoutines.Managers.LanguageManagerExtensions;
global using static DailyRoutines.Helpers.NotifyHelper;
global using static OmenTools.Helpers.ContentsFinderHelper;
global using Dalamud.Interface.Utility.Raii;
global using OmenTools.Infos;
global using OmenTools.ImGuiOm;
global using OmenTools.Helpers;
global using OmenTools;
global using ImGuiNET;
global using ImPlotNET;
global using Dalamud.Game;
using System;
using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DailyRoutines.ModulesPublic;

public class WhiStatusOff : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = "谁点我Buff",
        Description = "看谁点掉了你的团辅或者减伤Buff",
        Category = ModuleCategories.Combat,
        Author = ["Fragile"],
        ModulesConflict = []
    };

    // 记录buff信息的字典，键为buffId，值为包含时间和来源的元组
    private readonly Dictionary<ushort, (float Duration, ulong SourceId, DateTime GainTime, uint TargetId)> _buffRecords = new();
    
    // 配置选项
    private bool _enableLogging = true;
    private float _timeThreshold = 0.2f; // 如果实际持续时间小于预期时间的20%，则判定为提前消失
    
    // 模块配置
    private Config _moduleConfig = null!;
    
    // 状态效果选择
    private string _statusSearchInput = string.Empty;
    private Status _selectedStatus;
    private bool _hasSelectedStatus = false;
    
    // 预设导入导出
    private string _presetName = "预设名称";
    private string _importData = string.Empty;
    private bool _showImportWindow = false;
    
    // 辅助变量，用于ImGui.Checkbox的ref参数
    private bool _sendNotification;
    private bool _enableStatusFilter;

    public override unsafe void Init()
    {
        _moduleConfig = LoadConfig<Config>() ?? new Config();
        
        // 初始化辅助变量
        _sendNotification = _moduleConfig.SendNotification;
        _enableStatusFilter = _moduleConfig.EnableStatusFilter;
        
        PlayerStatusManager.RegGainStatus(OnGainStatus);
        PlayerStatusManager.RegLoseStatus(OnLoseStatus);
    }
    
    private unsafe void OnGainStatus(BattleChara* player, ushort buffId, ushort param, ushort stackCount, TimeSpan remainingTime, ulong sourceId)
    {
        if (player == null || remainingTime.TotalSeconds <= 0) return;

        // 如果启用了过滤并且该buff不在监控列表中，则跳过
        if (_moduleConfig.EnableStatusFilter && !_moduleConfig.MonitoredStatuses.Contains(buffId)) return;

        uint targetId = player->EntityId;
        bool isSelfBuff = sourceId == DService.ClientState.LocalPlayer.EntityId;
            
        // 如果是自身buff，记录到字典中
        if (isSelfBuff)
        {
            string targetName = player->NameString;
            string sourceName = GetEntityName((uint)sourceId);
                
            DService.Log.Debug($"[获得buff] BuffID: {buffId}, 持续时间: {remainingTime.TotalSeconds}秒, 来源: {sourceName}, 目标: {targetName}");
            _buffRecords[buffId] = ((float)remainingTime.TotalSeconds, sourceId, DateTime.Now, targetId);
        }
    }
    
    private unsafe void OnLoseStatus(BattleChara* player, ushort buffId, ushort param, ushort stackCount, ulong sourceId)
    {
        if (player == null) return;
        
        // 如果启用了过滤并且该buff不在监控列表中，则跳过
        if (_moduleConfig.EnableStatusFilter && !_moduleConfig.MonitoredStatuses.Contains(buffId)) return;

        uint targetId = player->EntityId;
        bool isSelfBuff = sourceId == DService.ClientState.LocalPlayer.EntityId;

        if (!isSelfBuff || !_enableLogging) return;

        string targetName = player->NameString;
        string sourceName = GetEntityName((uint)sourceId);
            
        DService.Log.Debug($"[失去buff] BuffID: {buffId}, 来源: {sourceName}, 目标: {targetName}");
            
        // 检查是否是提前消失的buff
        if (_buffRecords.TryGetValue(buffId, out var buffInfo))
        {
            var expectedDuration = buffInfo.Duration;
            var actualDuration = (DateTime.Now - buffInfo.GainTime).TotalSeconds;
                
            // 如果实际持续时间小于预期时间的阈值，则判定为提前消失
            if (actualDuration < expectedDuration * _timeThreshold)
            {
                bool isPlayerDead = player->Health == 0;
                // 如果玩家死亡，则不报告buff提前消失
                if (isPlayerDead)
                {
                    _buffRecords.Remove(buffId);
                    return;
                }
                
                string recordSourceName = GetEntityName((uint)buffInfo.SourceId);
                string recordTargetName = GetEntityName(buffInfo.TargetId);
                
                string statusName = LuminaWrapper.GetStatusName(buffId);
                    
                // 输出详细信息
                DService.Log.Info($"[Buff提前消失] {statusName}({buffId}), 预期持续时间: {expectedDuration}秒, 实际持续时间: {actualDuration:F2}秒");
                DService.Log.Info($"[Buff提前消失] 来源: {recordSourceName}, 目标: {recordTargetName}");
                
                // 如果配置了发送通知
                if (_moduleConfig.SendNotification)
                {
                    NotificationWarning($"[Buff提前消失] {statusName}, 预期: {expectedDuration:F1}秒, 实际: {actualDuration:F1}秒","有人点你Buff");
                }
            }
                
            _buffRecords.Remove(buffId);
        }
    }
    
    private string GetEntityName(uint entityId)
    {
        if (entityId == 0) return "未知";
        if (entityId == 0xE0000000) return "本地对象";
            
        var entity = DService.ObjectTable.FirstOrDefault(o => o?.EntityId == entityId);
        return entity?.Name.TextValue ?? "未知";
    }
    
    private void DrawMonitoredStatusList()
    {
        using var table = ImRaii.Table("###MonitoredStatusTable", 2, ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersInner, 
                                      new Vector2(ImGui.GetContentRegionAvail().X, 200));
        if (!table) return;
        
        ImGui.TableSetupColumn("状态效果", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableHeadersRow();
        
        foreach (var statusId in _moduleConfig.MonitoredStatuses.ToArray())
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            
            string statusName = LuminaWrapper.GetStatusName(statusId);
            var sheet = DService.Data.GetExcelSheet<Status>();
            uint statusIcon = 0;
            {
                var statusRow = sheet.GetRow(statusId);
                statusIcon = statusRow.Icon;
            }
            
            if (statusIcon > 0)
            {
                var icon = ImageHelper.GetGameIcon(statusIcon);
                if (icon != null)
                {
                    ImGui.Image(icon.ImGuiHandle, new Vector2(20, 20) * GlobalFontScale);
                    ImGui.SameLine();
                }
            }
            
            ImGui.Text($"{statusName} ({statusId})");
            
            ImGui.TableNextColumn();
            if (ImGui.Button($"删除###{statusId}"))
            {
                _moduleConfig.MonitoredStatuses.Remove(statusId);
                SaveConfig(_moduleConfig);
            }
        }
    }
    
    private void DrawStatusSelector()
    {
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 100);
        ImGui.InputTextWithHint("###StatusSearch", "搜索状态效果...", ref _statusSearchInput, 100);
        
        ImGui.SameLine();
        if (ImGui.Button("添加") && _hasSelectedStatus && _selectedStatus.RowId != 0)
        {
            _moduleConfig.MonitoredStatuses.Add((ushort)_selectedStatus.RowId);
            SaveConfig(_moduleConfig);
        }
        
        // 创建状态效果选择列表
        var sheet = DService.Data.GetExcelSheet<Status>();
        
        var statusList = sheet.Where(s => s.Name.ToString() != "" && 
                                          (string.IsNullOrEmpty(_statusSearchInput) || 
                                           s.Name.ToString().ToLower().Contains(_statusSearchInput.ToLower()))&&s.StatusCategory == 1)
                              .Take(100)
                              .ToList();
        
        if (statusList.Count > 0)
        {
            using var child = ImRaii.Child("###StatusList", new Vector2(ImGui.GetContentRegionAvail().X, 200), true);
            foreach (var status in statusList)
            {
                bool isSelected = _hasSelectedStatus && _selectedStatus.RowId == status.RowId;
                
                // 起始位置
                float cursorPosX = ImGui.GetCursorPosX();
                
                // 显示图标
                if (status.Icon > 0)
                {
                    var icon = ImageHelper.GetGameIcon(status.Icon);
                    if (icon != null)
                    {
                        ImGui.Image(icon.ImGuiHandle, new Vector2(20, 20) * GlobalFontScale);
                        ImGui.SameLine(0, 5);
                    }
                }
                
                // 计算可选择区域的宽度
                float selectableWidth = ImGui.GetContentRegionAvail().X;
                
                // 显示可选择的文本
                bool selected = ImGui.Selectable($"{status.Name} ({status.RowId})###status_{status.RowId}", isSelected, ImGuiSelectableFlags.None, new Vector2(selectableWidth, 24 * GlobalFontScale));
                
                if (selected)
                {
                    _selectedStatus = status;
                    _hasSelectedStatus = true;
                }
                
                // 添加悬停提示
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    
                    // 在提示中显示更大的图标
                    if (status.Icon > 0)
                    {
                        var icon = ImageHelper.GetGameIcon(status.Icon);
                        if (icon != null)
                        {
                            ImGui.Image(icon.ImGuiHandle, new Vector2(32, 32) * GlobalFontScale);
                            ImGui.SameLine();
                        }
                    }
                    
                    ImGui.Text($"{status.Name}");
                    ImGui.Text($"ID: {status.RowId}");
                    
                    // 显示状态效果描述
                    string description = status.Description.ToString() ?? "";
                    if (!string.IsNullOrEmpty(description))
                    {
                        ImGui.Separator();
                        ImGui.PushTextWrapPos(300 * GlobalFontScale);
                        ImGui.TextColored(new Vector4(0.9f, 0.9f, 0.7f, 1.0f), description);
                        ImGui.PopTextWrapPos();
                    }
                    
                    // 如果已在监控列表中，显示提示
                    if (_moduleConfig.MonitoredStatuses.Contains((ushort)status.RowId))
                    {
                        ImGui.Separator();
                        ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.2f, 1.0f), "已在监控列表中");
                    }
                    
                    ImGui.EndTooltip();
                }
            }
        }
        else
        {
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "未找到匹配的状态效果");
        }
    }
    
    // 导出预设
    private void ExportPreset()
    {
        try
        {
            // 创建预设数据对象
            var presetData = new StatusPreset
            {
                Name = _presetName,
                StatusIds = _moduleConfig.MonitoredStatuses.ToList(),
                Description = $"状态效果预设 - {_presetName}",
                CreatedAt = DateTime.Now
            };
            
            // 序列化为JSON
            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            
            string jsonData = JsonSerializer.Serialize(presetData, jsonOptions);
            
            // 复制到剪贴板
            ImGui.SetClipboardText(jsonData);
            
            // 通知用户
            NotificationSuccess("预设已导出到剪贴板", "导出成功");
        }
        catch (Exception ex)
        {
            DService.Log.Error($"导出预设失败: {ex.Message}");
            NotificationError("导出预设失败", "错误");
        }
    }
    
    // 导入预设
    private void ImportPreset()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_importData))
            {
                NotificationError("导入数据为空", "导入失败");
                return;
            }
            
            // 尝试反序列化
            var preset = JsonSerializer.Deserialize<StatusPreset>(_importData);
            
            if (preset == null || preset.StatusIds == null || preset.StatusIds.Count == 0)
            {
                NotificationError("无效的预设数据", "导入失败");
                return;
            }
            
            // 更新预设名称
            if (!string.IsNullOrWhiteSpace(preset.Name))
            {
                _presetName = preset.Name;
            }
            
            // 添加导入的状态ID到监控列表
            foreach (var statusId in preset.StatusIds)
            {
                _moduleConfig.MonitoredStatuses.Add(statusId);
            }
            
            // 保存配置
            SaveConfig(_moduleConfig);
            
            // 通知用户
            NotificationSuccess($"成功导入 {preset.StatusIds.Count} 个状态效果", "导入成功");
            
            // 关闭导入窗口
            _importData = string.Empty;
            _showImportWindow = false;
        }
        catch (Exception ex)
        {
            DService.Log.Error($"导入预设失败: {ex.Message}");
            NotificationError("导入格式错误，请确认是否为有效的预设数据", "导入失败");
        }
    }
    
    // 绘制导入/导出UI
    private void DrawImportExportUI()
    {
        ImGui.Separator();
        ImGui.Text("预设管理:");
        
        // 预设名称
        ImGui.SetNextItemWidth(200 * GlobalFontScale);
        ImGui.InputText("###PresetName", ref _presetName, 50);
        ImGui.SameLine();
        
        // 导出按钮
        if (ImGui.Button("导出预设"))
        {
            ExportPreset();
        }
        ImGuiOm.TooltipHover("将当前监控的状态效果列表导出到剪贴板");
        
        ImGui.SameLine();
        
        // 导入按钮
        if (ImGui.Button("导入预设"))
        {
            _showImportWindow = true;
        }
        ImGuiOm.TooltipHover("从JSON数据导入状态效果预设");
        
        // 导入窗口
        if (_showImportWindow)
        {
            ImGui.SetNextWindowSize(new Vector2(500, 300) * GlobalFontScale);
            if (ImGui.Begin("导入状态效果预设", ref _showImportWindow))
            {
                ImGui.Text("请粘贴预设JSON数据:");
                
                // 多行文本输入框
                ImGui.InputTextMultiline("###ImportData", ref _importData, 10000, 
                                         new Vector2(ImGui.GetContentRegionAvail().X, 200));
                
                if (ImGui.Button("导入"))
                {
                    ImportPreset();
                }
                
                ImGui.SameLine();
                
                if (ImGui.Button("从剪贴板粘贴"))
                {
                    _importData = ImGui.GetClipboardText();
                }
                
                ImGui.SameLine();
                
                if (ImGui.Button("取消"))
                {
                    _showImportWindow = false;
                    _importData = string.Empty;
                }
                
                ImGui.End();
            }
        }
    }
    
    public override void ConfigUI()
    {
        ImGui.Checkbox("启用Buff提前消失检测", ref _enableLogging);
        
        if (_enableLogging)
        {
            ImGui.SliderFloat("提前消失阈值 (实际/预期)", ref _timeThreshold, 0.05f, 0.95f, "%.2f");
            ImGui.Text($"当实际持续时间小于预期时间的{_timeThreshold * 100}%时判定为提前消失");
            
            // 使用辅助变量进行Checkbox
            if (ImGui.Checkbox("发送通知", ref _sendNotification))
            {
                _moduleConfig.SendNotification = _sendNotification;
                SaveConfig(_moduleConfig);
            }
            
            ImGui.Separator();
            
            // 状态效果过滤
            if (ImGui.Checkbox("启用状态效果过滤", ref _enableStatusFilter))
            {
                _moduleConfig.EnableStatusFilter = _enableStatusFilter;
                SaveConfig(_moduleConfig);
            }
            ImGuiOm.HelpMarker("启用后，仅监控选定的状态效果");
            
            if (_moduleConfig.EnableStatusFilter)
            {
                ImGui.Text("监控的状态效果:");
                DrawMonitoredStatusList();
                
                ImGui.Separator();
                ImGui.Text("添加状态效果:");
                DrawStatusSelector();
                
                // 绘制导入导出UI
                DrawImportExportUI();
            }
        }
    }
    
    public override unsafe void Uninit()
    {
        // 注销PlayerStatusManager的状态效果获得和失去事件
        PlayerStatusManager.UnregGainStatus(OnGainStatus);
        PlayerStatusManager.UnregLoseStatus(OnLoseStatus);
        
        _buffRecords.Clear();
        base.Uninit();
    }
    
    private class Config : ModuleConfiguration
    {
        public bool SendNotification { get; set; } = true;
        public bool EnableStatusFilter { get; set; } = false;
        public HashSet<ushort> MonitoredStatuses { get; set; } = new();
    }
    
    // 预设数据类
    private class StatusPreset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "默认预设";
        
        [JsonPropertyName("description")]
        public string Description { get; set; } = "";
        
        [JsonPropertyName("statusIds")]
        public List<ushort> StatusIds { get; set; } = new();
        
        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
