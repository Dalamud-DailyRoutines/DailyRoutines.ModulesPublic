using System.Collections.Frozen;
using System.Diagnostics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using OmenTools.Dalamud;
using OmenTools.OmenService;
using Task = System.Threading.Tasks.Task;

namespace DailyRoutines.ModulesPublic;

public class AutoTimedLogout : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = Lang.Get("AutoTimedLogoutTitle"),
        Description         = Lang.Get("AutoTimedLogoutDescription"),
        Category            = ModuleCategory.General,
        ModulesPrerequisite = ["InstantLogout"],
        Author              = ["Wotou"]
    };

    private int                      customMinutes = 30;
    private long?                    scheduledTime;
    private OperationMode            currentOperation = OperationMode.Logout;
    private CancellationTokenSource? cancelSource;

    protected override void Uninit() =>
        Abort();

    protected override void ConfigUI()
    {
        if (scheduledTime.HasValue)
        {
            var currentTime = Framework.GetServerTime();
            var remaining   = scheduledTime.Value - currentTime;

            if (remaining > 0)
            {
                var hours   = remaining        / 3600;
                var minutes = remaining % 3600 / 60;
                var seconds = remaining        % 60;

                var operationText = ModeLoc.GetValueOrDefault(currentOperation, string.Empty);
                ImGui.TextColored(KnownColor.GreenYellow.ToVector4(), $"{operationText}:");

                ImGui.SameLine();
                ImGui.TextUnformatted($" {hours:D2}:{minutes:D2}:{seconds:D2}");
            }

            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Times, Lang.Get("Cancel")))
                Abort();

            return;
        }

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Operation"));

        using (ImRaii.PushIndent())
        {
            var isFirst = true;

            foreach (var (operationMode, loc) in ModeLoc)
            {
                if (!isFirst)
                    ImGui.SameLine();
                isFirst = false;

                if (ImGui.RadioButton(loc, currentOperation == operationMode))
                    currentOperation = operationMode;
            }
        }

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Time"));

        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(150f * GlobalUIScale);
            if (ImGui.InputInt($"{Lang.Get("Minute")}##MinuteInput", ref customMinutes, 1, 10))
                customMinutes = Math.Clamp(customMinutes, 1, 14400);

            if (ImGui.Button($"30 {Lang.Get("Minute")}"))
                customMinutes = 30;

            ImGui.SameLine();
            if (ImGui.Button($"1 {Lang.Get("Hour")}"))
                customMinutes = 60;

            ImGui.SameLine();
            if (ImGui.Button($"2 {Lang.Get("Hour")}"))
                customMinutes = 120;

            ImGui.SameLine();
            if (ImGui.Button($"3 {Lang.Get("Hour")}"))
                customMinutes = 180;

            ImGui.SameLine();
            if (ImGui.Button($"6 {Lang.Get("Hour")}"))
                customMinutes = 360;

            ImGui.SameLine();
            if (ImGui.Button($"12 {Lang.Get("Hour")}"))
                customMinutes = 720;

            ImGui.SameLine();
            if (ImGui.Button($"24 {Lang.Get("Hour")}"))
                customMinutes = 1440;
        }

        ImGui.Spacing();

        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Check, Lang.Get("Confirm")))
            StartWithMinutes(customMinutes, currentOperation);
    }

    private void StartWithMinutes(int minutes, OperationMode operation)
    {
        Abort();
        currentOperation = operation;
        scheduledTime    = Framework.GetServerTime() + minutes * 60;

        cancelSource = new();
        _            = WaitForTimer(minutes * 60 * 1000, cancelSource.Token);
    }

    private async Task WaitForTimer(int delayMs, CancellationToken token)
    {
        try
        {
            await Task.Delay(delayMs, token);
            if (token.IsCancellationRequested) return;

            scheduledTime = null;

            switch (currentOperation)
            {
                case OperationMode.ShutdownPC:
                    ExecuteShutdownPC();
                    break;

                case OperationMode.Logout:
                    await DService.Instance().Framework.RunOnFrameworkThread(() => ChatManager.Instance().SendMessage("/logout"));
                    break;

                case OperationMode.ShutdownGame:
                    await DService.Instance().Framework.RunOnFrameworkThread(() => ChatManager.Instance().SendMessage("/shutdown"));
                    break;
            }
        }
        catch (TaskCanceledException)
        {
            // ignored
        }
    }

    private static void ExecuteShutdownPC()
    {
        try
        {
            Process.Start
            (
                new ProcessStartInfo
                {
                    FileName        = "shutdown",
                    Arguments       = "/s /t 0",
                    UseShellExecute = false,
                    CreateNoWindow  = true
                }
            );
        }
        catch (Exception ex)
        {
            DLog.Error($"尝试自动关闭电脑失败: {ex.Message}", ex);
        }
    }

    private void Abort()
    {
        scheduledTime = null;

        if (cancelSource != null)
        {
            if (!cancelSource.IsCancellationRequested)
                cancelSource.Cancel();

            cancelSource.Dispose();
            cancelSource = null;
        }
    }
    
    private enum OperationMode
    {
        Logout,
        ShutdownGame,
        ShutdownPC
    }

    #region 常量

    private static readonly FrozenDictionary<OperationMode, string> ModeLoc = new Dictionary<OperationMode, string>()
    {
        [OperationMode.Logout]       = Lang.Get("AutoTimedLogout-Mode-Logout"),
        [OperationMode.ShutdownGame] = Lang.Get("AutoTimedLogout-Mode-ShutdownGame"),
        [OperationMode.ShutdownPC]   = Lang.Get("AutoTimedLogout-Mode-ShutdownPC")
    }.ToFrozenDictionary();

    #endregion
}
