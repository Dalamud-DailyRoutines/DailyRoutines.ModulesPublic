using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.Interop.Game.ExecuteCommand.Implementations;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Duty;

public class AutoLeaveDuty : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoLeaveDutyTitle"),
        Description = Lang.Get("AutoLeaveDutyDescription"),
        Category    = ModuleCategory.Duty
    };

    private Config config = null!;

    private ContentSelectCombo contentSelectCombo  = null!;
    private ContentSelectCombo immediateLeaveCombo = null!;

    private LeaveDutyKind manualLeaveKind = LeaveDutyKind.None;

    protected override void Init()
    {
        contentSelectCombo  =   new("Blacklist");
        immediateLeaveCombo =   new("ImmediateLeave");
        config              =   Config.Load(this) ?? new();
        TaskHelper          ??= new();

        contentSelectCombo.SelectedIDs  = config.BlacklistContent;
        immediateLeaveCombo.SelectedIDs = config.ImmediateLeaveContent;

        LogMessageManager.Instance().RegPre(OnPreReceiveLogmessage);

        IDutyState.Instance().DutyCompleted      += OnDutyComplete;
        IClientState.Instance().TerritoryChanged += OnZoneChanged;

        CommandManager.Instance().AddSubCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("AutoLeaveDuty-Command-Set") });
    }

    protected override void Uninit()
    {
        CommandManager.Instance().RemoveSubCommand(COMMAND);

        IDutyState.Instance().DutyCompleted      -= OnDutyComplete;
        IClientState.Instance().TerritoryChanged -= OnZoneChanged;

        LogMessageManager.Instance().Unreg(OnPreReceiveLogmessage);
    }

    protected override void ConfigUI()
    {
        using var itemWidth = ImRaii.ItemWidth(250f * GlobalUIScale);

        using (ImRaii.Heading1(Lang.Get("Command")))
        {
            foreach (var dutyKind in Enum.GetValues<LeaveDutyKind>())
            {
                var command = $"/pdr {COMMAND} {dutyKind}";

                ImGui.TextUnformatted(command);
                if (ImGui.IsItemHovered())
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

                if (ImGui.IsItemClicked())
                {
                    ImGui.SetClipboardText(command);
                    NotifyHelper.Instance().NotificationSuccess(command, Lang.Get("CopiedToClipboard"));
                }

                ImGui.TextUnformatted(Lang.Get($"AutoLeaveDuty-Command-Set-{dutyKind}"));

                ImGui.Spacing();
            }
        }

        ImGui.NewLine();

        // 延迟
        if (ImGui.InputInt($"{Lang.Get("Delay")} (ms)###DelayInput", ref config.Delay))
            config.Delay = Math.Max(0, config.Delay);
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);

        // 不退高难
        if (ImGui.Checkbox($"{Lang.Get("AutoLeaveDuty-NoLeaveHighEndDuties")}###NoLeaveHighEndDuties", ref config.NoLeaveHighEndDuties))
            config.Save(this);
        ImGuiOm.HelpMarker(Lang.Get("AutoLeaveDuty-NoLeaveHighEndDuties-Help"));

        // 强制退本
        if (ImGui.Checkbox($"{Lang.Get("AutoLeaveDuty-ForceToLeave")}###ForceToLeave", ref config.ForceToLeave))
            config.Save(this);

        ImGui.NewLine();

        // 黑名单副本
        {
            if (contentSelectCombo.DrawCheckbox())
            {
                config.BlacklistContent = contentSelectCombo.SelectedIDs;
                config.Save(this);
            }

            ImGui.SameLine();
            ImGui.TextUnformatted(Lang.Get("AutoLeaveDuty-BlacklistContents"));

            ImGuiOm.HelpMarker(Lang.Get("AutoLeaveDuty-BlacklistContents-Help"));
        }

        // 立刻退出
        {
            if (immediateLeaveCombo.DrawCheckbox())
            {
                config.ImmediateLeaveContent = immediateLeaveCombo.SelectedIDs;
                config.Save(this);
            }

            ImGui.SameLine();
            ImGui.TextUnformatted(Lang.Get("AutoLeaveDuty-ImmediateLeaveContents"));

            ImGuiOm.HelpMarker(Lang.Get("AutoLeaveDuty-ImmediateLeaveContents-Help"));
        }
    }

    private unsafe void OnCommand
    (
        string command,
        string args
    )
    {
        if (GameState.ContentFinderCondition == 0)
        {
            using var utf8String = new Utf8String($"/pdr {command}");
            RaptureLogModule.Instance()->ShowLogMessageString(726, &utf8String);
            return;
        }

        if (!Enum.TryParse<LeaveDutyKind>(args, true, out var kind))
        {
            NotifyHelper.Instance().ChatError
            (
                ISeStringEvaluator.Instance().EvaluateFromLogMessage
                (
                    3802,
                    [
                        1,
                        LuminaWrapper.GetAddonText(9448),
                        args
                    ]
                )
            );

            return;
        }

        manualLeaveKind = kind;

        var message = Lang.Get($"AutoLeaveDuty-Notification-Set-{kind}");
        NotifyHelper.Instance().Chat(message);
        NotifyHelper.Toast(message);
    }

    private void OnDutyComplete
    (
        IDutyStateEventArgs args
    )
    {
        TaskHelper.Abort();
        
        if (manualLeaveKind != LeaveDutyKind.None)
        {
            switch (manualLeaveKind)
            {
                case LeaveDutyKind.InstantLeave:
                    TaskHelper.Enqueue(() => DutyCommand.Leave(DutyCommand.LeaveDutyKind.Inactive));
                    break;

                case LeaveDutyKind.NoLeave:
                    return;
            }
        }

        if (config.BlacklistContent.Contains(GameState.ContentFinderCondition))
            return;

        if (config.ImmediateLeaveContent.Contains(GameState.ContentFinderCondition))
        {
            TaskHelper.Enqueue(() => DutyCommand.Leave(DutyCommand.LeaveDutyKind.Inactive));
            return;
        }

        if (config.NoLeaveHighEndDuties &&
            args.ContentFinderCondition.Value.HighEndDuty)
            return;

        if (config.Delay > 0)
            TaskHelper.DelayNext(config.Delay);

        if (!config.ForceToLeave)
        {
            TaskHelper.Enqueue(() => !ICondition.Instance()[ConditionFlag.InCombat]);
            TaskHelper.Enqueue(() => DutyCommand.Leave());
        }
        else
            TaskHelper.Enqueue(() => DutyCommand.Leave(DutyCommand.LeaveDutyKind.Inactive));
    }

    private void OnZoneChanged
    (
        uint u
    )
    {
        manualLeaveKind = LeaveDutyKind.None;
        TaskHelper.Abort();
    }

    private static void OnPreReceiveLogmessage
    (
        ref bool                isPrevented,
        ref uint                logMessageID,
        ref LogMessageQueueItem values
    )
    {
        if (logMessageID != 914) return;
        isPrevented = true;
    }

    private class Config : ModuleConfig
    {
        public HashSet<uint> BlacklistContent      = [];
        public HashSet<uint> ImmediateLeaveContent = [];
        public int           Delay;
        public bool          ForceToLeave;

        public bool NoLeaveHighEndDuties = true;
    }

    private enum LeaveDutyKind
    {
        None,

        InstantLeave,

        NoLeave
    }

    #region 常量

    private const string COMMAND = "autoleaveduty";

    #endregion
}
