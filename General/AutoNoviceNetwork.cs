using System.Timers;
using DailyRoutines.Abstracts;
using Dalamud.Interface.Colors;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoNoviceNetwork : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoNoviceNetworkTitle"),
        Description = GetLoc("AutoNoviceNetworkDescription"),
        Category    = ModuleCategories.General,
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };
    
    private static Config ModuleConfig = null!;
    
    private static Timer? AfkTimer;
    
    private static int  TryTimes;
    private static bool IsJoined;
    private static bool IsMentor;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        TaskHelper ??= new() { TimeLimitMS = 5_000 };
        
        AfkTimer           ??= new(10_000);
        AfkTimer.Elapsed   +=  OnAfkStateCheck;
        AfkTimer.AutoReset =   true;
        AfkTimer.Enabled   =   true;
    }

    protected override void ConfigUI()
    {
        if (Throttler.Throttle("AutoNoviceNetwork-UpdateInfo", 1000))
        {
            IsMentor = PlayerState.Instance()->IsMentor();
            IsJoined = IsInNoviceNetwork();
        }
        
        ImGui.Text($"{GetLoc("AutoNoviceNetwork-JoinState")}:");
        
        ImGui.SameLine();
        ImGui.TextColored(IsJoined ? ImGuiColors.HealerGreen : ImGuiColors.DPSRed,
                          IsJoined ? "√" : "×");
        
        ImGui.Text($"{GetLoc("AutoNoviceNetwork-AttemptedTimes")}:");

        ImGui.SameLine();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{TryTimes}");
        
        ImGui.NewLine();

        using (ImRaii.Disabled(TaskHelper.IsBusy || !IsMentor))
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Play, GetLoc("Start")))
            {
                TryTimes = 0;
                TaskHelper.Enqueue(EnqueueARound);
            }
        }

        ImGui.SameLine();
        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Stop, GetLoc("Stop")))
            TaskHelper.Abort();
        
        ImGui.NewLine();

        if (ImGui.Checkbox(GetLoc("AutoNoviceNetwork-TryJoinWhenInactive"), ref ModuleConfig.IsTryJoinWhenInactive))
            SaveConfig(ModuleConfig);

        ImGuiOm.HelpMarker(GetLoc("AutoNoviceNetwork-TryJoinWhenInactiveHelp"), 20f * GlobalFontScale);
    }

    private void EnqueueARound()
    {
        if (!(IsMentor = PlayerState.Instance()->IsMentor())) return;

        TaskHelper.Enqueue(() =>
        {
            if (PlayerState.Instance()->IsPlayerStateFlagSet(PlayerStateFlag.IsNoviceNetworkAutoJoinEnabled)) return;
            ChatManager.SendMessage("/beginnerchannel on");
        });

        TaskHelper.Enqueue(TryJoin);

        TaskHelper.DelayNext(250);
        TaskHelper.Enqueue(() => TryTimes++);

        TaskHelper.Enqueue(() =>
        {
            if (IsInNoviceNetwork())
            {
                TaskHelper.Abort();
                return;
            }

            EnqueueARound();
        });
    }

    private static void TryJoin() =>
        InfoProxyNoviceNetwork.Instance()->SendJoinRequest();

    private static bool IsInNoviceNetwork()
    {
        var infoProxy = InfoModule.Instance()->GetInfoProxyById(InfoProxyId.NoviceNetwork);
        return ((int)infoProxy[1].VirtualTable & 1) != 0;
    }

    private void OnAfkStateCheck(object? sender, ElapsedEventArgs e)
    {
        if (!(IsMentor = PlayerState.Instance()->IsMentor())) return;

        IsJoined = IsInNoviceNetwork();
        if (IsJoined) return;
        
        if (!ModuleConfig.IsTryJoinWhenInactive || TaskHelper.IsBusy) return;
        if (BoundByDuty || OccupiedInEvent) return;

        if (LastInputInfo.GetIdleTimeTick() > 10_000 || Framework.Instance()->WindowInactive)
            TryJoin();
    }

    protected override void Uninit()
    {
        AfkTimer?.Stop();
        if (AfkTimer != null) 
            AfkTimer.Elapsed -= OnAfkStateCheck;
        AfkTimer?.Dispose();
        AfkTimer = null;

        TryTimes = 0;
    }

    private class Config : ModuleConfiguration
    {
        public bool IsTryJoinWhenInactive;
    }
}
