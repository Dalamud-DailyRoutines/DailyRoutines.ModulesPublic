using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.Interop.Game.ExecuteCommand.Implementations;
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

    private readonly ContentSelectCombo contentSelectCombo = new("Blacklist");
    private readonly ContentSelectCombo immediateLeaveCombo = new("ImmediateLeave");

    protected override void Init()
    {
        config     =   Config.Load(this) ?? new();
        TaskHelper ??= new();

        contentSelectCombo.SelectedIDs = config.BlacklistContent;
        immediateLeaveCombo.SelectedIDs = config.ImmediateLeaveContent;

        LogMessageManager.Instance().RegPre(OnPreReceiveLogmessage);

        DService.Instance().DutyState.DutyCompleted      += OnDutyComplete;
        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
    }
    
    protected override void Uninit()
    {
        DService.Instance().DutyState.DutyCompleted      -= OnDutyComplete;
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;

        LogMessageManager.Instance().Unreg(OnPreReceiveLogmessage);
    }

    protected override void ConfigUI()
    {
        using var itemWidth = ImRaii.ItemWidth(250f * GlobalUIScale);
        
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

    private void OnDutyComplete(IDutyStateEventArgs args)
    {
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
            TaskHelper.Enqueue(() => !DService.Instance().Condition[ConditionFlag.InCombat]);
            TaskHelper.Enqueue(() => DutyCommand.Leave());
        }
        else
            TaskHelper.Enqueue(() => DutyCommand.Leave(DutyCommand.LeaveDutyKind.Inactive));
    }

    private void OnZoneChanged(uint u) =>
        TaskHelper.Abort();

    // 拦截一下那个信息
    private static void OnPreReceiveLogmessage(ref bool isPrevented, ref uint logMessageID, ref LogMessageQueueItem values)
    {
        if (logMessageID != 914) return;
        isPrevented = true;
    }
    
    private class Config : ModuleConfig
    {
        public HashSet<uint> BlacklistContent = [];
        public HashSet<uint> ImmediateLeaveContent = [];
        public int           Delay;
        public bool          ForceToLeave;

        public bool NoLeaveHighEndDuties = true;
    }
}
