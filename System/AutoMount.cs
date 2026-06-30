using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoMount : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoMountTitle"),
        Description = Lang.Get("AutoMountDescription"),
        Category    = ModuleCategory.System
    };

    private Config config = null!;

    private readonly MountSelectCombo mountSelectCombo = new("Mount");
    private readonly ZoneSelectCombo  zoneSelectCombo  = new("Zone");

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        mountSelectCombo.SelectedID = config.SelectedMount;
        zoneSelectCombo.SelectedIDs = config.BlacklistZones;

        TaskHelper = new() { TimeoutMS = 10_000 };

        DService.Instance().Condition.ConditionChange    += OnConditionChanged;
        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
    }

    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        DService.Instance().Condition.ConditionChange    -= OnConditionChanged;
    }

    protected override void ConfigUI()
    {
        using var itemWidth = ImRaii.ItemWidth(300f * GlobalUIScale);

        using (ImRaii.Heading1(LuminaWrapper.GetAddonText(4964)))
        {
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

            if (config.SelectedMount == 0 ||
                !LuminaGetter.TryGetRow(config.SelectedMount, out Mount selectedMount))
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

        ImGui.NewLine();

        if (ImGui.Checkbox(Lang.Get("AutoMount-MountWhenZoneChange"), ref config.MountWhenZoneChange))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("AutoMount-MountWhenGatherEnd"), ref config.MountWhenGatherEnd))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("AutoMount-MountWhenCombatEnd"), ref config.MountWhenCombatEnd))
            config.Save(this);

        ImGui.NewLine();

        if (zoneSelectCombo.DrawCheckbox())
        {
            config.BlacklistZones = zoneSelectCombo.SelectedIDs;
            config.Save(this);
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(Lang.Get("BlacklistZones"));

        if (ImGui.InputInt($"{Lang.Get("Delay")} (ms)###AutoMount-Delay", ref config.Delay))
            config.Delay = Math.Max(0, config.Delay);
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("AutoMount-JumpAfterMount"), ref config.JumpAfterMount))
            config.Save(this);
    }

    private void OnZoneChanged(uint u)
    {
        if (!config.MountWhenZoneChange                             ||
            GameState.TerritoryType == 0                            ||
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
                                             config.MountWhenCombatEnd              &&
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
        if (!Throttler.Shared.Throttle("AutoMount.UseMount"))
            return false;

        if (ICondition.Instance().IsOnMount ||
            LocalPlayerState.Instance().IsMoving)
            return true;

        if (ICondition.Instance().IsCasting      ||
            ICondition.Instance().IsBetweenAreas ||
            ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, 9) != 0)
            return false;

        if (config.Delay > 0)
            TaskHelper.DelayNext(config.Delay);

        TaskHelper.DelayNext(100);
        TaskHelper.Enqueue
        (() => config.SelectedMount == 0
                   ? UseActionManager.Instance().UseAction(ActionType.GeneralAction, 9)
                   : UseActionManager.Instance().UseAction(ActionType.Mount,         config.SelectedMount)
        );

        if (config.JumpAfterMount)
        {
            TaskHelper.Enqueue(() =>
            {
                if (!PlayerState.Instance()->CanFly) return;
                TaskHelper.Enqueue(() => ICondition.Instance().IsOnMount,                                    "WaitForMount", 5_000);
                TaskHelper.Enqueue(() => UseActionManager.Instance().UseAction(ActionType.GeneralAction, 2), "使用跳跃");
                TaskHelper.DelayNext(50);
                TaskHelper.Enqueue(() => UseActionManager.Instance().UseAction(ActionType.GeneralAction, 2), "再次使用跳跃");
            });
        }

        return true;
    }

    private static bool CanUseMountCurrentZone() =>
        GameState.TerritoryTypeData is { Mount: true };

    private class Config : ModuleConfig
    {
        public HashSet<uint> BlacklistZones = [];

        public int Delay = 1000;

        public bool MountWhenCombatEnd  = true;
        public bool MountWhenGatherEnd  = true;
        public bool MountWhenZoneChange = true;

        public bool JumpAfterMount;

        public uint SelectedMount;
    }
}
