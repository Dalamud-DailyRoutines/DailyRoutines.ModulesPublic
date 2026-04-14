using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;
using OmenTools.Threading.TaskHelper;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoMount : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoMountTitle"),
        Description = Lang.Get("AutoMountDescription"),
        Category    = ModuleCategory.Combat
    };
    
    private Config config = null!;

    private readonly MountSelectCombo mountSelectCombo = new("Mount");
    private readonly ZoneSelectCombo  zoneSelectCombo  = new("Zone");

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        mountSelectCombo.SelectedID = config.SelectedMount;
        zoneSelectCombo.SelectedIDs = config.BlacklistZones;

        TaskHelper ??= new TaskHelper { TimeoutMS = 20000 };

        DService.Instance().Condition.ConditionChange    += OnConditionChanged;
        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{LuminaWrapper.GetAddonText(4964)}");

        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(300f * GlobalUIScale);

            if (mountSelectCombo.DrawRadio())
            {
                config.SelectedMount = mountSelectCombo.SelectedID;
                config.Save(this);
            }

            ImGui.SameLine();

            if (ImGui.Button($"{FontAwesomeIcon.Eraser.ToIconString()} {Lang.Get("Clear")}"))
            {
                config.SelectedMount = 0;
                config.Save(this);
            }

            if (config.SelectedMount == 0 || !LuminaGetter.TryGetRow(config.SelectedMount, out Mount selectedMount))
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

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("BlacklistZones")}");

        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(300f * GlobalUIScale);

            if (zoneSelectCombo.DrawCheckbox())
            {
                config.BlacklistZones = zoneSelectCombo.SelectedIDs;
                config.Save(this);
            }
        }

        ImGui.Spacing();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Delay")}");

        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(300f * GlobalUIScale);
            if (ImGui.InputInt("ms###AutoMount-Delay", ref config.Delay))
                config.Delay = Math.Max(0, config.Delay);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);
        }

        ImGui.NewLine();

        if (ImGui.Checkbox(Lang.Get("AutoMount-MountWhenZoneChange"), ref config.MountWhenZoneChange))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("AutoMount-MountWhenGatherEnd"), ref config.MountWhenGatherEnd))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("AutoMount-MountWhenCombatEnd"), ref config.MountWhenCombatEnd))
            config.Save(this);
    }

    private void OnZoneChanged(ushort zone)
    {
        if (!config.MountWhenZoneChange                             ||
            GameState.TerritoryType == 0                                  ||
            config.BlacklistZones.Contains(GameState.TerritoryType) ||
            !CanUseMountCurrentZone())
            return;

        TaskHelper.Abort();
        TaskHelper.Enqueue(UseMount);
    }

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (config.BlacklistZones.Contains(GameState.TerritoryType)) return;

        switch (flag)
        {
            case ConditionFlag.Gathering when !value && config.MountWhenGatherEnd:
            case ConditionFlag.InCombat when !value                                 &&
                                             config.MountWhenCombatEnd        &&
                                             !DService.Instance().ClientState.IsPvP &&
                                             (FateManager.Instance()->CurrentFate           == null ||
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
        if (!Throttler.Shared.Throttle("AutoMount-UseMount")) return false;
        if (DService.Instance().Condition.IsBetweenAreas) return false;
        if (AgentMap.Instance()->IsPlayerMoving) return true;
        if (DService.Instance().Condition.IsCasting) return false;
        if (DService.Instance().Condition.IsOnMount) return true;
        if (ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, 9) != 0) return false;

        if (config.Delay > 0)
            TaskHelper.DelayNext(config.Delay);

        TaskHelper.DelayNext(100);
        TaskHelper.Enqueue
        (() => config.SelectedMount == 0
                   ? UseActionManager.Instance().UseAction(ActionType.GeneralAction, 9)
                   : UseActionManager.Instance().UseAction(ActionType.Mount,         config.SelectedMount)
        );
        return true;
    }

    private static bool CanUseMountCurrentZone() =>
        GameState.TerritoryTypeData is { Mount: true };

    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        DService.Instance().Condition.ConditionChange    -= OnConditionChanged;
    }

    private class Config : ModuleConfig
    {
        public HashSet<uint> BlacklistZones      = [];
        public int           Delay               = 1000;
        public bool          MountWhenCombatEnd  = true;
        public bool          MountWhenGatherEnd  = true;
        public bool          MountWhenZoneChange = true;

        public uint SelectedMount;
    }
}
