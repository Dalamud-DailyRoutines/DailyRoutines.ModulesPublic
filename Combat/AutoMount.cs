using System;
using System.Collections.Generic;
using DailyRoutines.Abstracts;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoMount : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoMountTitle"),
        Description = GetLoc("AutoMountDescription"),
        Category    = ModuleCategories.Combat,
    };

    private static Config ModuleConfig = null!;

    private static readonly MountSelectCombo MountSelectCombo = new("Mount");
    private static readonly ZoneSelectCombo  ZoneSelectCombo  = new("Zone");

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        MountSelectCombo.SelectedID = ModuleConfig.SelectedMount;
        ZoneSelectCombo.SelectedIDs = ModuleConfig.BlacklistZones;

        TaskHelper ??= new TaskHelper { TimeoutMS = 20000 };

        DService.Instance().Condition.ConditionChange += OnConditionChanged;
        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{LuminaWrapper.GetAddonText(4964)}");

        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(300f * GlobalFontScale);
            if (MountSelectCombo.DrawRadio())
            {
                ModuleConfig.SelectedMount = MountSelectCombo.SelectedID;
                SaveConfig(ModuleConfig);
            }
            
            ImGui.SameLine();
            if (ImGui.Button($"{FontAwesomeIcon.Eraser.ToIconString()} {GetLoc("Clear")}"))
            {
                ModuleConfig.SelectedMount = 0;
                SaveConfig(ModuleConfig);
            }
            
            if (ModuleConfig.SelectedMount == 0 || !LuminaGetter.TryGetRow(ModuleConfig.SelectedMount, out Mount selectedMount))
            {
                if (ImageHelper.TryGetGameIcon(118, out var texture))
                    ImGuiOm.TextImage(LuminaWrapper.GetGeneralActionName(9), texture.Handle, new(ImGui.GetTextLineHeightWithSpacing()));
            }
            else
            {
                if (ImageHelper.TryGetGameIcon(selectedMount.Icon, out var texture))
                    ImGuiOm.TextImage(selectedMount.Singular.ToString(), texture.Handle, new(ImGui.GetTextLineHeightWithSpacing()));
            }
        }
        
        ImGui.Spacing();
        
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("BlacklistZones")}");

        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(300f * GlobalFontScale);
            if (ZoneSelectCombo.DrawCheckbox())
            {
                ModuleConfig.BlacklistZones = ZoneSelectCombo.SelectedIDs;
                SaveConfig(ModuleConfig);
            }
        }
        
        ImGui.Spacing();
        
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("Delay")}");

        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(300f * GlobalFontScale);
            if (ImGui.InputInt("ms###AutoMount-Delay", ref ModuleConfig.Delay))
                ModuleConfig.Delay = Math.Max(0, ModuleConfig.Delay);
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);
        }

        ImGui.NewLine();

        if (ImGui.Checkbox(GetLoc("AutoMount-MountWhenZoneChange"), ref ModuleConfig.MountWhenZoneChange))
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox(GetLoc("AutoMount-MountWhenGatherEnd"), ref ModuleConfig.MountWhenGatherEnd))
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox(GetLoc("AutoMount-MountWhenCombatEnd"), ref ModuleConfig.MountWhenCombatEnd))
            SaveConfig(ModuleConfig);
    }

    private void OnZoneChanged(ushort zone)
    {
        if (!ModuleConfig.MountWhenZoneChange                             ||
            GameState.TerritoryType == 0                                  ||
            ModuleConfig.BlacklistZones.Contains(GameState.TerritoryType) ||
            !CanUseMountCurrentZone())
            return;

        TaskHelper.Abort();
        TaskHelper.Enqueue(UseMount);
    }

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (ModuleConfig.BlacklistZones.Contains(GameState.TerritoryType)) return;
        switch (flag)
        {
            case ConditionFlag.Gathering when !value && ModuleConfig.MountWhenGatherEnd:
            case ConditionFlag.InCombat when !value && ModuleConfig.MountWhenCombatEnd && !DService.Instance().ClientState.IsPvP &&
                                             (FateManager.Instance()->CurrentFate == null ||
                                              FateManager.Instance()->CurrentFate->Progress == 100):
                if (!CanUseMountCurrentZone()) return;

                TaskHelper.Abort();
                TaskHelper.DelayNext(500);
                TaskHelper.Enqueue(UseMount);
                break;
        }
    }

    private bool UseMount()
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
                                     ? UseActionManager.Instance().UseAction(ActionType.GeneralAction, 9)
                                     : UseActionManager.Instance().UseAction(ActionType.Mount,         ModuleConfig.SelectedMount));
        return true;
    }

    private static bool CanUseMountCurrentZone() => 
        GameState.TerritoryTypeData is { Mount: true };

    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        DService.Instance().Condition.ConditionChange -= OnConditionChanged;
    }

    private class Config : ModuleConfiguration
    {
        public bool MountWhenCombatEnd  = true;
        public bool MountWhenGatherEnd  = true;
        public bool MountWhenZoneChange = true;
        
        public uint          SelectedMount;
        public HashSet<uint> BlacklistZones = [];
        public int           Delay = 1000;
    }
}
