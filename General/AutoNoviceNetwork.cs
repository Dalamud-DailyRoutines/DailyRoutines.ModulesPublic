using System.Timers;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Interface.Colors;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using OmenTools.Interop.Windows.Models;
using OmenTools.OmenService;
using OmenTools.Threading;
using Timer = System.Timers.Timer;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoNoviceNetwork : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoNoviceNetworkTitle"),
        Description = Lang.Get("AutoNoviceNetworkDescription"),
        Category    = ModuleCategory.General
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };
    
    private Config config = null!;

    private Timer? afkTimer;

    private int  tryTimes;
    private bool isJoined;
    private bool isMentor;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        TaskHelper ??= new() { TimeoutMS = 5_000 };

        afkTimer           ??= new(10_000);
        afkTimer.Elapsed   +=  OnAfkStateCheck;
        afkTimer.AutoReset =   true;
        afkTimer.Enabled   =   true;
    }
    
    protected override void Uninit()
    {
        if (afkTimer != null)
        {
            afkTimer?.Stop();
            afkTimer.Elapsed -= OnAfkStateCheck;
            afkTimer?.Dispose();
            afkTimer = null;
        }

        tryTimes = 0;
    }

    protected override void ConfigUI()
    {
        if (Throttler.Shared.Throttle("AutoNoviceNetwork-UpdateInfo", 1000))
        {
            isMentor = PlayerState.Instance()->IsMentor();
            isJoined = IsInNoviceNetwork();
        }

        ImGui.TextUnformatted($"{Lang.Get("AutoNoviceNetwork-JoinState")}:");

        ImGui.SameLine();
        ImGui.TextColored
        (
            isJoined ? ImGuiColors.HealerGreen : ImGuiColors.DPSRed,
            isJoined ? "√" : "×"
        );

        ImGui.TextUnformatted($"{Lang.Get("AutoNoviceNetwork-AttemptedTimes")}:");

        ImGui.SameLine();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{tryTimes}");

        ImGui.NewLine();

        using (ImRaii.Disabled(TaskHelper.IsBusy || !isMentor))
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Play, Lang.Get("Start")))
            {
                tryTimes = 0;
                TaskHelper.Enqueue(EnqueueARound);
            }
        }

        ImGui.SameLine();
        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Stop, Lang.Get("Stop")))
            TaskHelper.Abort();

        ImGui.NewLine();

        if (ImGui.Checkbox(Lang.Get("AutoNoviceNetwork-TryJoinWhenInactive"), ref config.IsTryJoinWhenInactive))
            config.Save(this);

        ImGuiOm.HelpMarker(Lang.Get("AutoNoviceNetwork-TryJoinWhenInactiveHelp"), 20f * GlobalUIScale);
    }

    private void EnqueueARound()
    {
        if (!(isMentor = PlayerState.Instance()->IsMentor())) return;

        TaskHelper.Enqueue
        (() =>
            {
                if (PlayerState.Instance()->IsPlayerStateFlagSet(PlayerStateFlag.IsNoviceNetworkAutoJoinEnabled)) return;
                ChatManager.Instance().SendMessage("/beginnerchannel on");
            }
        );

        TaskHelper.Enqueue(TryJoin);

        TaskHelper.DelayNext(250);
        TaskHelper.Enqueue(() => tryTimes++);

        TaskHelper.Enqueue
        (() =>
            {
                if (IsInNoviceNetwork())
                {
                    TaskHelper.Abort();
                    return;
                }

                EnqueueARound();
            }
        );
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
        if (!(isMentor = PlayerState.Instance()->IsMentor())) return;

        isJoined = IsInNoviceNetwork();
        if (isJoined) return;

        if (!config.IsTryJoinWhenInactive         || TaskHelper.IsBusy) return;
        if (DService.Instance().Condition.IsBoundByDuty || DService.Instance().Condition.IsOccupiedInEvent) return;

        if (LastInputInfo.GetIdleTimeTick() > 10_000 || Framework.Instance()->WindowInactive)
            TryJoin();
    }
    
    private class Config : ModuleConfig
    {
        public bool IsTryJoinWhenInactive;
    }
}
