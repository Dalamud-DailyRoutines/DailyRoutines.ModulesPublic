using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoLeaveDuty : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoLeaveDutyTitle"),
        Description = Lang.Get("AutoLeaveDutyDescription"),
        Category    = ModuleCategory.Combat
    };
    
    private Config config = null!;

    private readonly ContentSelectCombo contentSelectCombo = new("Blacklist");

    protected override void Init()
    {
        config     =   Config.Load(this) ?? new();
        TaskHelper ??= new();

        contentSelectCombo.SelectedIDs = config.BlacklistContent;

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
        if (ImGui.Checkbox($"{Lang.Get("AutoLeaveDuty-ForceToLeave")}###ForceToLeave", ref config.ForceToLeave))
            config.Save(this);

        ImGui.SetNextItemWidth(100f * GlobalUIScale);
        if (ImGui.InputInt($"{Lang.Get("Delay")} (ms)###DelayInput", ref config.Delay))
            config.Delay = Math.Max(0, config.Delay);
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);

        ImGui.NewLine();

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoLeaveDuty-BlacklistContents")}");

        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(250f * GlobalUIScale);

            if (contentSelectCombo.DrawCheckbox())
            {
                config.BlacklistContent = contentSelectCombo.SelectedIDs;
                config.Save(this);
            }

            if (ImGui.Checkbox($"{Lang.Get("AutoLeaveDuty-NoLeaveHighEndDuties")}###NoLeaveHighEndDuties", ref config.NoLeaveHighEndDuties))
                config.Save(this);
            ImGuiOm.HelpMarker(Lang.Get("AutoLeaveDuty-NoLeaveHighEndDutiesHelp"));
        }
    }

    private void OnDutyComplete(IDutyStateEventArgs args)
    {
        if (config.BlacklistContent.Contains(GameState.ContentFinderCondition))
            return;

        if (config.NoLeaveHighEndDuties &&
            args.ContentFinderCondition.RowId != 0)
            return;

        if (config.Delay > 0)
            TaskHelper.DelayNext(config.Delay);

        if (!config.ForceToLeave)
        {
            TaskHelper.Enqueue(() => !DService.Instance().Condition[ConditionFlag.InCombat]);
            TaskHelper.Enqueue(() => ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.LeaveDuty));
        }
        else
            TaskHelper.Enqueue(() => ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.LeaveDuty, 1U));
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
        public int           Delay;
        public bool          ForceToLeave;

        public bool NoLeaveHighEndDuties = true;
    }
}
