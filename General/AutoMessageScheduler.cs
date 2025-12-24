using System;
using System.Collections.Generic;
using DailyRoutines.Abstracts;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace DailyRoutines.ModulesPublic;

public class AutoMessageScheduler : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoMessageSchedulerTitle"),
        Description = GetLoc("AutoMessageSchedulerDescription"),
        Category    = ModuleCategories.General,
        Author      = ["Wotou"]
    };

    private static Config? ModuleConfig;
    private static EditingState? EditingData;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        foreach (var sched in ModuleConfig.Presets)
        {
            sched.IsActive  = false;
            sched.Remaining = sched.Repeat;
        }
        
        TaskHelper ??= new TaskHelper();
        
        FrameworkManager.Reg(OnUpdate, throttleMS: 500);
    }

    protected override void Uninit() => 
        FrameworkManager.Unreg(OnUpdate);

    protected override void ConfigUI()
    {
        using var table = ImRaii.Table("MessageTable", 8, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg);
        if (!table) return;

        ImGui.TableSetupColumn("序号",   ImGuiTableColumnFlags.WidthFixed,   ImGui.CalcTextSize("1234").X);
        ImGui.TableSetupColumn("名称",   ImGuiTableColumnFlags.WidthStretch, 25);
        ImGui.TableSetupColumn("开始时间", ImGuiTableColumnFlags.WidthStretch, 20);
        ImGui.TableSetupColumn("间隔时间", ImGuiTableColumnFlags.WidthStretch, 20);
        ImGui.TableSetupColumn("重复次数", ImGuiTableColumnFlags.WidthStretch, 15);
        ImGui.TableSetupColumn("是否激活", ImGuiTableColumnFlags.WidthStretch, 15);
        ImGui.TableSetupColumn("计时模式", ImGuiTableColumnFlags.WidthStretch, 15);
        ImGui.TableSetupColumn("操作",   ImGuiTableColumnFlags.WidthStretch, 30);
        
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        
        ImGui.TableNextColumn();
        if (ImGuiOm.ButtonIconSelectable("AddScheduleButton", FontAwesomeIcon.Plus))
        {
            EditingData = new EditingState(new());
            ImGui.OpenPopup("EditPresetPopup");
        }

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(GetLoc("Name"));
        
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(GetLoc("StartTime"));
        
        ImGui.TableNextColumn();
        ImGui.TextUnformatted($"{GetLoc("Interval")} (s)");
        
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(GetLoc("AutoMessageScheduler-RepeatTimes"));
        
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(GetLoc("State"));
        
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(GetLoc("AutoMessageScheduler-TimeMode"));
        
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(string.Empty);

        var isOpenPopup = false;
        for (var i = 0; i < ModuleConfig.Presets.Count; i++)
        {
            using var id = ImRaii.PushId(i);

            var sched = ModuleConfig.Presets[i];
            
            ImGui.TableNextRow();
            
            ImGui.TableNextColumn();
            ImGui.TextUnformatted((i + 1).ToString());
            
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(sched.Name);
            
            ImGui.TableNextColumn();
            ImGui.Text($"{sched.StartHour:D2}:{sched.StartMinute:D2}");
            
            ImGui.TableNextColumn();
            ImGui.Text($"{sched.IntervalSeconds}");
            
            ImGui.TableNextColumn();
            ImGui.Text($"{sched.Remaining}/{sched.Repeat}");
            
            ImGui.TableNextColumn();
            ImGui.TextColored(sched.IsActive ? KnownColor.GreenYellow.ToVector4() : KnownColor.Pink.ToVector4(), sched.IsActive ? "O" : "X");
            
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(sched.Mode switch
            {
                TimeMode.LocalTime  => LuminaWrapper.GetAddonText(1127),
                TimeMode.ServerTime => LuminaWrapper.GetAddonText(1128),
                TimeMode.EorzeaTime => LuminaWrapper.GetAddonText(1129),
                _                   => string.Empty
            });
            
            ImGui.TableNextColumn();
            if (ImGuiOm.ButtonIcon("Toggle", 
                                   sched.IsActive ? FontAwesomeIcon.Stop : FontAwesomeIcon.Play, sched.IsActive ? GetLoc("Stop") : GetLoc("Start")))
            {
                sched.IsActive  = !sched.IsActive;
                sched.Remaining = sched.Repeat;
                if (sched.IsActive)
                    sched.NextTriggerTime = CalculateStartTime(sched);
            }

            ImGui.SameLine();
            if (ImGuiOm.ButtonIcon("Edit", FontAwesomeIcon.Pen, GetLoc("Edit")))
            {
                EditingData = new EditingState(sched);
                isOpenPopup = true;
            }
            
            ImGui.SameLine();
            if (ImGuiOm.ButtonIcon("Delete", FontAwesomeIcon.Trash, $"{GetLoc("Delete")} (Ctrl)") && ImGui.IsKeyDown(ImGuiKey.LeftCtrl))
            {
                ModuleConfig.Presets.RemoveAt(i);
                ModuleConfig.Save(this);
            }
        }
        
        if (isOpenPopup)
            ImGui.OpenPopup("EditPresetPopup");

        using var popup = ImRaii.Popup("EditPresetPopup");
        if (!popup) return;
        
        if (EditingData == null) return;
                
        ImGui.Text($"{GetLoc("Name")}:");
        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(200f * GlobalFontScale);
            ImGui.InputText("##Name", ref EditingData.Name, 64);
        }
                
        ImGui.Text($"{GetLoc("StartTime")}:");
        using (ImRaii.PushIndent())
        {
            var hourStr = EditingData.StartHour.ToString("D2");
            var minStr  = EditingData.StartMinute.ToString("D2");

            ImGui.SetNextItemWidth(25f * GlobalFontScale);
            if (ImGui.InputText("##StartHour", ref hourStr, 2, ImGuiInputTextFlags.CharsDecimal) &&
                int.TryParse(hourStr, out var parsedHour)                                        &&
                parsedHour is >= 0 and <= 23)
                EditingData.StartHour = parsedHour;

            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.Text(":");
            ImGui.SameLine();

            ImGui.SetNextItemWidth(25f * GlobalFontScale);
            if (ImGui.InputText("##StartMinute", ref minStr, 2, ImGuiInputTextFlags.CharsDecimal) &&
                int.TryParse(minStr, out var parsedMin)                                           && parsedMin is >= 0 and <= 59)
                EditingData.StartMinute = parsedMin;
        }
                
        ImGui.Text($"{GetLoc("Interval")} (s):");
        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(200f * GlobalFontScale);
            if (ImGui.InputInt("##Interval", ref EditingData.Interval))
                EditingData.Interval = Math.Clamp(EditingData.Interval, 1, 864000);
        }
                
        ImGui.Text($"{GetLoc("AutoMessageScheduler-RepeatTimes")}:");
        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(200f * GlobalFontScale);
            if (ImGui.InputInt("##Repeat", ref EditingData.Repeat))
                EditingData.Repeat = Math.Clamp(EditingData.Repeat, 1, 14400);
        }
                
        ImGui.Text($"{GetLoc("AutoMessageScheduler-TimeMode")}:");
        using (ImRaii.PushIndent())
        {
            var mode = (int)EditingData.Mode;
                    
            ImGui.SetNextItemWidth(200f * GlobalFontScale);
            if (ImGui.Combo("##TimeMode", ref mode,
                [
                    $"{LuminaWrapper.GetAddonText(1127)}",
                    $"{LuminaWrapper.GetAddonText(1129)}" +
                    $"{LuminaWrapper.GetAddonText(1128)}"
                ]))
                EditingData.Mode = (TimeMode)mode;
        }
                
        ImGui.Text($"{LuminaWrapper.GetAddonText(2581)}:");
        using (ImRaii.PushIndent())
            ImGui.InputTextMultiline("##Messages", ref EditingData.Message, 1024, new(-1, 100f * GlobalFontScale));

        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Check, GetLoc("Confirm")) && EditingData.Editing != null)
        {
            EditingData.Editing.Name            = EditingData.Name;
            EditingData.Editing.StartHour       = Math.Clamp(EditingData.StartHour,   0, 23);
            EditingData.Editing.StartMinute     = Math.Clamp(EditingData.StartMinute, 0, 59);
            EditingData.Editing.IntervalSeconds = Math.Max(1, EditingData.Interval);
            EditingData.Editing.Repeat          = Math.Max(1, EditingData.Repeat);
            EditingData.Editing.Remaining       = EditingData.Editing.Repeat;
            EditingData.Editing.MessageText     = EditingData.Message;
            EditingData.Editing.IsActive        = false;
            EditingData.Editing.Mode            = EditingData.Mode;
                    
            if (!ModuleConfig.Presets.Contains(EditingData.Editing))
                ModuleConfig.Presets.Add(EditingData.Editing);

            EditingData = null;
                    
            ModuleConfig.Save(this);

            ImGui.CloseCurrentPopup();
        }

        return;

        long CalculateStartTime(ScheduledMessage sched)
        {
            var now = GetNow(sched.Mode);
            var todayStart = sched.Mode switch
            {
                TimeMode.LocalTime                         => new DateTimeOffset(DateTime.Today).ToUnixTimeSeconds(),
                TimeMode.ServerTime or TimeMode.EorzeaTime => now - (now % 86400),
                _                                          => throw new ArgumentOutOfRangeException()
            };
        
            var targetTime = todayStart + (sched.StartHour * 3600) + (sched.StartMinute * 60);
            if (targetTime <= now)
                targetTime += 86400;
        
            return targetTime;
        }
    }

    private void OnUpdate(IFramework _)
    {
        foreach (var sched in ModuleConfig.Presets)
        {
            if (!sched.IsActive || sched.Remaining <= 0) continue;
            
            var now = GetNow(sched.Mode);
            if (now >= sched.NextTriggerTime)
            {
                sched.Remaining--;
                sched.NextTriggerTime = now + sched.IntervalSeconds;
                EneuqueMessagesSending(sched);
                if (sched.Remaining <= 0)
                    sched.IsActive = false;
            }
        }
    }

    private void EneuqueMessagesSending(ScheduledMessage sched)
    {
        foreach (var line in sched.MessageText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            TaskHelper.Enqueue(() => ChatManager.SendMessage(line));
            TaskHelper.DelayNext(20);
        }
    }

    private static long GetNow(TimeMode mode) => mode switch
    {
        TimeMode.LocalTime  => new DateTimeOffset(DateTime.Now).ToUnixTimeSeconds(),
        TimeMode.ServerTime => Framework.GetServerTime(),
        TimeMode.EorzeaTime => EorzeaDate.GetTime().EorzeaTimeStamp,
        _                   => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };

    public class Config : ModuleConfiguration
    {
        public List<ScheduledMessage> Presets = [];
    }

    public enum TimeMode
    {
        LocalTime,
        EorzeaTime,
        ServerTime
    }

    public class ScheduledMessage
    {
        public Guid     ID   = Guid.NewGuid();
        public string   Name = string.Empty;
        public int      StartHour;
        public int      StartMinute;
        public int      IntervalSeconds = 300;
        public int      Repeat          = 3;
        public int      Remaining       = 3;
        public string   MessageText     = string.Empty;
        public bool     IsActive;
        public long     NextTriggerTime;
        public TimeMode Mode = TimeMode.LocalTime;
    }

    private class EditingState(ScheduledMessage scheduledMessage)
    {
        public ScheduledMessage Editing     = scheduledMessage;
        public string           Name        = scheduledMessage.Name;
        public int              StartHour   = scheduledMessage.StartHour;
        public int              StartMinute = scheduledMessage.StartMinute;
        public int              Interval    = scheduledMessage.IntervalSeconds;
        public int              Repeat      = scheduledMessage.Repeat;
        public string           Message     = scheduledMessage.MessageText;
        public TimeMode         Mode        = scheduledMessage.Mode;
    }
}
