using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;

namespace DailyRoutines.Modules;

public unsafe class AutoMount : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("AutoMountTitle"),
        Description = GetLoc("AutoMountDescription"),
        Category = ModuleCategories.Combat,
    };

    private static Config ModuleConfig = null!;

    private static Mount? SelectedMountRow;
    private static string MountSearchInput = string.Empty;
    private static string ZoneSearchInput = string.Empty;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        if (ModuleConfig.SelectedMount != 0)
            SelectedMountRow = LuminaGetter.GetRow<Mount>(ModuleConfig.SelectedMount);

        TaskHelper ??= new TaskHelper { AbortOnTimeout = true, TimeLimitMS = 20000, ShowDebug = false };

        DService.Condition.ConditionChange += OnConditionChanged;
        DService.ClientState.TerritoryChanged += OnZoneChanged;
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("AutoMount-CurrentMount")}:");

        ImGui.SameLine();
        ImGui.Text(ModuleConfig.SelectedMount == 0
                       ? GetLoc("AutoMount-RandomMount")
                       : LuminaGetter.GetRow<Mount>(ModuleConfig.SelectedMount)!.Value.Singular.ExtractText());

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("AutoMount-SelecteMount")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(250f * GlobalFontScale);
        if (MountSelectCombo(ref SelectedMountRow, ref MountSearchInput))
        {
            ModuleConfig.SelectedMount = SelectedMountRow!.Value.RowId;
            SaveConfig(ModuleConfig);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton(GetLoc("AutoMount-RandomMount")))
        {
            ModuleConfig.SelectedMount = 0;
            SaveConfig(ModuleConfig);
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("BlacklistZones")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(250f * GlobalFontScale);
        if (ZoneSelectCombo(ref ModuleConfig.BlacklistZones, ref ZoneSearchInput))
            SaveConfig(ModuleConfig);
        
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("Delay")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        if (ImGui.InputInt("(ms)###AutoMount-Delay", ref ModuleConfig.Delay))
            ModuleConfig.Delay = Math.Max(0, ModuleConfig.Delay);
        if (ImGui.IsItemDeactivatedAfterEdit())
            ModuleConfig.Save(this);

        ImGui.Spacing();

        if (ImGui.Checkbox(GetLoc("AutoMount-MountWhenZoneChange"), ref ModuleConfig.MountWhenZoneChange))
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox(GetLoc("AutoMount-MountWhenGatherEnd"), ref ModuleConfig.MountWhenGatherEnd))
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox(GetLoc("AutoMount-MountWhenCombatEnd"), ref ModuleConfig.MountWhenCombatEnd))
            SaveConfig(ModuleConfig);
    }

    private void OnZoneChanged(ushort zone)
    {
        if (!ModuleConfig.MountWhenZoneChange || zone == 0 || ModuleConfig.BlacklistZones.Contains(zone)) return;
        if (!CanUseMountCurrentZone(zone)) return;

        TaskHelper.Abort();
        TaskHelper.Enqueue(UseMount);
    }

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (ModuleConfig.BlacklistZones.Contains(DService.ClientState.TerritoryType)) return;
        switch (flag)
        {
            case ConditionFlag.Gathering when !value && ModuleConfig.MountWhenGatherEnd:
            case ConditionFlag.InCombat when !value && ModuleConfig.MountWhenCombatEnd && !DService.ClientState.IsPvP &&
                                             (FateManager.Instance()->CurrentFate == null ||
                                              FateManager.Instance()->CurrentFate->Progress == 100):
                if (!CanUseMountCurrentZone()) return;

                TaskHelper.Abort();
                TaskHelper.DelayNext(500);
                TaskHelper.Enqueue(UseMount);
                break;
        }
    }

    private bool? UseMount()
    {
        if (!Throttler.Throttle("AutoMount-UseMount")) return false;
        if (BetweenAreas) return false;
        if (AgentMap.Instance()->IsPlayerMoving) return true;
        if (IsCasting) return false;
        if (IsOnMount) return true;
        if (ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, 9) != 0) return false;

        if (ModuleConfig.Delay > 0)
            TaskHelper.DelayNext(ModuleConfig.Delay);

        TaskHelper.DelayNext(100);
        TaskHelper.Enqueue(() => ModuleConfig.SelectedMount == 0
                                     ? UseActionManager.UseAction(ActionType.GeneralAction, 9)
                                     : UseActionManager.UseAction(ActionType.Mount, ModuleConfig.SelectedMount));
        return true;
    }

    private static bool CanUseMountCurrentZone(ushort zone = 0)
    {
        if (zone == 0) 
            zone = DService.ClientState.TerritoryType;
        if (zone == 0) return false;

        var zoneData = LuminaGetter.GetRow<TerritoryType>(zone);
        return zoneData is { Mount: true };
    }

    protected override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        DService.Condition.ConditionChange -= OnConditionChanged;

        base.Uninit();
    }

    private class Config : ModuleConfiguration
    {
        public bool          MountWhenCombatEnd  = true;
        public bool          MountWhenGatherEnd  = true;
        public bool          MountWhenZoneChange = true;
        public uint          SelectedMount;
        public HashSet<uint> BlacklistZones = [];
        public int           Delay = 1000;
    }
}
