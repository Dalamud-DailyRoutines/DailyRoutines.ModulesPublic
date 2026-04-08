using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using OmenTools.Info.Game;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading.TaskHelper;

namespace DailyRoutines.ModulesPublic;

public class AutoMessageScheduler : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoMessageSchedulerTitle"),
        Description = Lang.Get("AutoMessageSchedulerDescription"),
        Category    = ModuleCategory.General,
        Author      = ["Wotou"]
    };

    private Config?       config;
    private EditingState? editingData;
    
    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        foreach (var sched in config.Presets)
        {
            sched.IsActive  = false;
            sched.Remaining = sched.Repeat;
        }

        TaskHelper ??= new TaskHelper();

        FrameworkManager.Instance().Reg(OnUpdate, 500);
    }

    protected override void Uninit() =>
        FrameworkManager.Instance().Unreg(OnUpdate);

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
            editingData = new EditingState(new());
            ImGui.OpenPopup("EditPresetPopup");
        }

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(Lang.Get("Name"));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(Lang.Get("StartTime"));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted($"{Lang.Get("Interval")} (s)");

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(Lang.Get("AutoMessageScheduler-RepeatTimes"));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(Lang.Get("State"));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(Lang.Get("AutoMessageScheduler-TimeMode"));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(string.Empty);

        var isOpenPopup = false;

        for (var i = 0; i < config.Presets.Count; i++)
        {
            using var id = ImRaii.PushId(i);

            var sched = config.Presets[i];

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted((i + 1).ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(sched.Name);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{sched.StartHour:D2}:{sched.StartMinute:D2}");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{sched.IntervalSeconds}");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{sched.Remaining}/{sched.Repeat}");

            ImGui.TableNextColumn();
            ImGui.TextColored(sched.IsActive ? KnownColor.GreenYellow.ToVector4() : KnownColor.Pink.ToVector4(), sched.IsActive ? "O" : "X");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted
            (
                sched.Mode switch
                {
                    TimeMode.LocalTime  => LuminaWrapper.GetAddonText(1127),
                    TimeMode.ServerTime => LuminaWrapper.GetAddonText(1128),
                    TimeMode.EorzeaTime => LuminaWrapper.GetAddonText(1129),
                    _                   => string.Empty
                }
            );

            ImGui.TableNextColumn();

            if (ImGuiOm.ButtonIcon
                (
                    "Toggle",
                    sched.IsActive ? FontAwesomeIcon.Stop : FontAwesomeIcon.Play,
                    sched.IsActive ? Lang.Get("Stop") : Lang.Get("Start")
                ))
            {
                sched.IsActive  = !sched.IsActive;
                sched.Remaining = sched.Repeat;
                if (sched.IsActive)
                    sched.NextTriggerTime = CalculateStartTime(sched);
            }

            ImGui.SameLine();

            if (ImGuiOm.ButtonIcon("Edit", FontAwesomeIcon.Pen, Lang.Get("Edit")))
            {
                editingData = new EditingState(sched);
                isOpenPopup = true;
            }

            ImGui.SameLine();

            if (ImGuiOm.ButtonIcon("Delete", FontAwesomeIcon.Trash, $"{Lang.Get("Delete")} (Ctrl)") && ImGui.IsKeyDown(ImGuiKey.LeftCtrl))
            {
                config.Presets.RemoveAt(i);
                config.Save(this);
            }
        }

        if (isOpenPopup)
            ImGui.OpenPopup("EditPresetPopup");

        using var popup = ImRaii.Popup("EditPresetPopup");
        if (!popup) return;

        if (editingData == null) return;

        ImGui.TextUnformatted($"{Lang.Get("Name")}:");

        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(200f * GlobalUIScale);
            ImGui.InputText("##Name", ref editingData.Name, 64);
        }

        ImGui.TextUnformatted($"{Lang.Get("StartTime")}:");

        using (ImRaii.PushIndent())
        {
            var hourStr = editingData.StartHour.ToString("D2");
            var minStr  = editingData.StartMinute.ToString("D2");

            ImGui.SetNextItemWidth(25f * GlobalUIScale);
            if (ImGui.InputText("##StartHour", ref hourStr, 2, ImGuiInputTextFlags.CharsDecimal) &&
                int.TryParse(hourStr, out var parsedHour)                                        &&
                parsedHour is >= 0 and <= 23)
                editingData.StartHour = parsedHour;

            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(":");
            ImGui.SameLine();

            ImGui.SetNextItemWidth(25f * GlobalUIScale);
            if (ImGui.InputText("##StartMinute", ref minStr, 2, ImGuiInputTextFlags.CharsDecimal) &&
                int.TryParse(minStr, out var parsedMin)                                           &&
                parsedMin is >= 0 and <= 59)
                editingData.StartMinute = parsedMin;
        }

        ImGui.TextUnformatted($"{Lang.Get("Interval")} (s):");

        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(200f * GlobalUIScale);
            if (ImGui.InputInt("##Interval", ref editingData.Interval))
                editingData.Interval = Math.Clamp(editingData.Interval, 1, 864000);
        }

        ImGui.TextUnformatted($"{Lang.Get("AutoMessageScheduler-RepeatTimes")}:");

        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(200f * GlobalUIScale);
            if (ImGui.InputInt("##Repeat", ref editingData.Repeat))
                editingData.Repeat = Math.Clamp(editingData.Repeat, 1, 14400);
        }

        ImGui.TextUnformatted($"{Lang.Get("AutoMessageScheduler-TimeMode")}:");

        using (ImRaii.PushIndent())
        {
            var mode = (int)editingData.Mode;

            ImGui.SetNextItemWidth(200f * GlobalUIScale);
            if (ImGui.Combo
                (
                    "##TimeMode",
                    ref mode,
                    [
                        $"{LuminaWrapper.GetAddonText(1127)}",
                        $"{LuminaWrapper.GetAddonText(1129)}" +
                        $"{LuminaWrapper.GetAddonText(1128)}"
                    ]
                ))
                editingData.Mode = (TimeMode)mode;
        }

        ImGui.TextUnformatted($"{LuminaWrapper.GetAddonText(2581)}:");
        using (ImRaii.PushIndent())
            ImGui.InputTextMultiline("##Messages", ref editingData.Message, 1024, new(-1, 100f * GlobalUIScale));

        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Check, Lang.Get("Confirm")) && editingData.Editing != null)
        {
            editingData.Editing.Name            = editingData.Name;
            editingData.Editing.StartHour       = Math.Clamp(editingData.StartHour,   0, 23);
            editingData.Editing.StartMinute     = Math.Clamp(editingData.StartMinute, 0, 59);
            editingData.Editing.IntervalSeconds = Math.Max(1, editingData.Interval);
            editingData.Editing.Repeat          = Math.Max(1, editingData.Repeat);
            editingData.Editing.Remaining       = editingData.Editing.Repeat;
            editingData.Editing.MessageText     = editingData.Message;
            editingData.Editing.IsActive        = false;
            editingData.Editing.Mode            = editingData.Mode;

            if (!config.Presets.Contains(editingData.Editing))
                config.Presets.Add(editingData.Editing);

            editingData = null;

            config.Save(this);

            ImGui.CloseCurrentPopup();
        }

        return;

        long CalculateStartTime(ScheduledMessage sched)
        {
            var now = GetNow(sched.Mode);
            var todayStart = sched.Mode switch
            {
                TimeMode.LocalTime                         => new DateTimeOffset(StandardTimeManager.Instance().Today).ToUnixTimeSeconds(),
                TimeMode.ServerTime or TimeMode.EorzeaTime => now - now % 86400,
                _                                          => throw new ArgumentOutOfRangeException()
            };

            var targetTime = todayStart + sched.StartHour * 3600 + sched.StartMinute * 60;
            if (targetTime <= now)
                targetTime += 86400;

            return targetTime;
        }
    }

    private void OnUpdate(IFramework _)
    {
        foreach (var sched in config.Presets)
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
            TaskHelper.Enqueue(() => ChatManager.Instance().SendMessage(line));
            TaskHelper.DelayNext(20);
        }
    }

    private static long GetNow(TimeMode mode) => mode switch
    {
        TimeMode.LocalTime  => new DateTimeOffset(StandardTimeManager.Instance().Now).ToUnixTimeSeconds(),
        TimeMode.ServerTime => Framework.GetServerTime(),
        TimeMode.EorzeaTime => EorzeaDate.GetTime().EorzeaTimeStamp,
        _                   => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };

    private class Config : ModuleConfig
    {
        public List<ScheduledMessage> Presets = [];
    }

    private class ScheduledMessage
    {
        public Guid     ID              = Guid.NewGuid();
        public int      IntervalSeconds = 300;
        public bool     IsActive;
        public string   MessageText = string.Empty;
        public TimeMode Mode        = TimeMode.LocalTime;
        public string   Name        = string.Empty;
        public long     NextTriggerTime;
        public int      Remaining = 3;
        public int      Repeat    = 3;
        public int      StartHour;
        public int      StartMinute;
    }

    private class EditingState
    (
        ScheduledMessage scheduledMessage
    )
    {
        public ScheduledMessage Editing     = scheduledMessage;
        public int              Interval    = scheduledMessage.IntervalSeconds;
        public string           Message     = scheduledMessage.MessageText;
        public TimeMode         Mode        = scheduledMessage.Mode;
        public string           Name        = scheduledMessage.Name;
        public int              Repeat      = scheduledMessage.Repeat;
        public int              StartHour   = scheduledMessage.StartHour;
        public int              StartMinute = scheduledMessage.StartMinute;
    }
    
    private enum TimeMode
    {
        LocalTime,
        EorzeaTime,
        ServerTime
    }
}
