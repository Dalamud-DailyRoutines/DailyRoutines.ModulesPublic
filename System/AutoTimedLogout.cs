using System;
using System.Collections.Generic;
using System.Diagnostics;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Interface;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace DailyRoutines.ModulesPublic;

public class AutoTimedLogout : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = GetLoc("AutoTimedLogoutTitle"),
        Description         = GetLoc("AutoTimedLogoutDescription"),
        Category            = ModuleCategories.System,
        ModulesPrerequisite = ["InstantLogout"],
        Author              = ["Wotou"]
    };

    private static readonly Dictionary<OperationMode, string> ModeLoc = new()
    {
        [OperationMode.Logout]       = GetLoc("AutoTimedLogout-Mode-Logout"),
        [OperationMode.ShutdownGame] = GetLoc("AutoTimedLogout-Mode-ShutdownGame"),
        [OperationMode.ShutdownPC]   = GetLoc("AutoTimedLogout-Mode-ShutdownPC"),
    };

    private static int           CustomMinutes = 30;
    private static long?         ScheduledTime;
    private static OperationMode CurrentOperation = OperationMode.Logout;

    protected override void Init()
    {
        Abort();
        FrameworkManager.Reg(OnUpdate, throttleMS: 1_000);
    }

    protected override void ConfigUI()
    {
        if (ScheduledTime.HasValue)
        {
            var currentTime = Framework.GetServerTime();
            var remaining   = ScheduledTime.Value - currentTime;

            if (remaining > 0)
            {
                var hours   = remaining        / 3600;
                var minutes = remaining % 3600 / 60;
                var seconds = remaining        % 60;

                var operationText = ModeLoc.GetValueOrDefault(CurrentOperation, string.Empty);
                ImGui.TextColored(KnownColor.GreenYellow.ToVector4(), $"{operationText}:");
                
                ImGui.SameLine();
                ImGui.Text($" {hours:D2}:{minutes:D2}:{seconds:D2}");
            }

            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Times, GetLoc("Cancel")))
                Abort();
            
            return;
        }
        
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("Operation"));
        
        using (ImRaii.PushIndent())
        {
            var isFirst = true;
            foreach (var (operationMode, loc) in ModeLoc)
            {
                if (!isFirst)
                    ImGui.SameLine();
                isFirst = false;
                
                if (ImGui.RadioButton(loc, CurrentOperation == operationMode))
                    CurrentOperation = operationMode;
            }
        }
        
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("Time"));
        
        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(150f * GlobalFontScale);
            if (ImGui.InputInt($"{GetLoc("Minute")}##MinuteInput", ref CustomMinutes, 1, 10))
                CustomMinutes = Math.Clamp(CustomMinutes, 1, 14400);

            if (ImGui.Button($"30 {GetLoc("Minute")}"))
                CustomMinutes = 30;

            ImGui.SameLine();
            if (ImGui.Button($"1 {GetLoc("Hour")}"))
                CustomMinutes = 60;

            ImGui.SameLine();
            if (ImGui.Button($"2 {GetLoc("Hour")}"))
                CustomMinutes = 120;

            ImGui.SameLine();
            if (ImGui.Button($"3 {GetLoc("Hour")}"))
                CustomMinutes = 180;
            
            ImGui.SameLine();
            if (ImGui.Button($"6 {GetLoc("Hour")}"))
                CustomMinutes = 360;
            
            ImGui.SameLine();
            if (ImGui.Button($"12 {GetLoc("Hour")}"))
                CustomMinutes = 720;
            
            ImGui.SameLine();
            if (ImGui.Button($"24 {GetLoc("Hour")}"))
                CustomMinutes = 1440;
        }
        
        ImGui.Spacing();

        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Check, GetLoc("Confirm")))
            StartWithMinutes(CustomMinutes, CurrentOperation);
    }

    private static void OnUpdate(IFramework framework)
    {
        if (!ScheduledTime.HasValue || Framework.GetServerTime() < ScheduledTime.Value) return;
        ScheduledTime = null;

        switch (CurrentOperation)
        {
            case OperationMode.Logout:
                ChatHelper.SendMessage("/logout");
                break;
            case OperationMode.ShutdownGame:
                ChatHelper.SendMessage("/shutdown");
                break;
            case OperationMode.ShutdownPC:
                try
                {
                    Process.Start(new ProcessStartInfo()
                    {
                        FileName        = "shutdown",
                        Arguments       = "/s /t 0",
                        UseShellExecute = false,
                        CreateNoWindow  = true
                    });
                }
                catch (Exception ex)
                {
                    Error($"尝试自动关闭电脑失败: {ex.Message}", ex);
                }

                break;
        }
    }

    private static void StartWithMinutes(int minutes, OperationMode operation)
    {
        Abort();
        CurrentOperation = operation;
        ScheduledTime    = Framework.GetServerTime() + (minutes * 60);
    }

    private static void Abort() =>
        ScheduledTime = null;

    protected override void Uninit()
    {
        FrameworkManager.Unreg(OnUpdate);
        Abort();
    }
    
    public enum OperationMode
    {
        Logout,
        ShutdownGame,
        ShutdownPC
    }
}
