using System;
using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using Dalamud.Game.ClientState.Conditions;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public class AutoLeaveDuty : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoLeaveDutyTitle"),
        Description = GetLoc("AutoLeaveDutyDescription"),
        Category    = ModuleCategories.Combat,
    };

    private static Config ModuleConfig = null!;
    
    private static string ContentSearchInput = string.Empty;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        TaskHelper ??= new();
        
        LogMessageManager.Register(OnPreReceiveLogmessage);

        DService.DutyState.DutyCompleted      += OnDutyComplete;
        DService.ClientState.TerritoryChanged += OnZoneChanged;
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox($"{GetLoc("AutoLeaveDuty-ForceToLeave")}###ForceToLeave", ref ModuleConfig.ForceToLeave))
            SaveConfig(ModuleConfig);
        
        ImGui.SetNextItemWidth(100f * GlobalFontScale);
        if (ImGui.InputInt($"{GetLoc("Delay")}###DelayInput", ref ModuleConfig.Delay))
            ModuleConfig.Delay = Math.Max(0, ModuleConfig.Delay);
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(ModuleConfig);

        ImGui.NewLine();

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("AutoLeaveDuty-BlacklistContents")}:");

        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(250f * GlobalFontScale);
            if (ContentSelectCombo(ref ModuleConfig.BlacklistContents, ref ContentSearchInput))
                SaveConfig(ModuleConfig);
            
            if (ImGui.Checkbox($"{GetLoc("AutoLeaveDuty-NoLeaveHighEndDuties")}###NoLeaveHighEndDuties", ref ModuleConfig.NoLeaveHighEndDuties))
                SaveConfig(ModuleConfig);
            ImGuiOm.HelpMarker(GetLoc("AutoLeaveDuty-NoLeaveHighEndDutiesHelp"));
        }
    }

    private void OnDutyComplete(object? sender, ushort zone)
    {
        if (ModuleConfig.BlacklistContents.Contains(zone)) 
            return;
        
        if (ModuleConfig.NoLeaveHighEndDuties &&
            LuminaGetter.Get<ContentFinderCondition>()
                       .FirstOrDefault(x => x.HighEndDuty && x.TerritoryType.RowId == zone).RowId != 0) 
            return;

        if (ModuleConfig.Delay > 0)
            TaskHelper.DelayNext(ModuleConfig.Delay);

        if (!ModuleConfig.ForceToLeave)
        {
            TaskHelper.Enqueue(() => !DService.Condition[ConditionFlag.InCombat]);
            TaskHelper.Enqueue(() => ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.LeaveDuty));
        }
        else
            TaskHelper.Enqueue(() => ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.LeaveDuty, 1U));
    }
    
    private void OnZoneChanged(ushort obj) => 
        TaskHelper.Abort();
    
    // 拦截一下那个信息
    private static void OnPreReceiveLogmessage(ref bool isPrevented, ref uint logMessageID)
    {
        if (logMessageID != 914) return;
        isPrevented = true;
    }

    protected override void Uninit()
    {
        DService.DutyState.DutyCompleted      -= OnDutyComplete;
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        
        LogMessageManager.Unregister(OnPreReceiveLogmessage);
    }

    private class Config : ModuleConfiguration
    {
        public HashSet<uint> BlacklistContents    = [];
        public bool          NoLeaveHighEndDuties = true;
        public bool          ForceToLeave;
        public int           Delay;
    }
}
