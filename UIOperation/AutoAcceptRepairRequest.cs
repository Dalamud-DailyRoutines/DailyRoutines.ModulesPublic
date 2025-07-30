using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public class AutoAcceptRepairRequest : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoAcceptRepairRequestTitle"),
        Description = GetLoc("AutoAcceptRepairRequestDescription"),
        Category    = ModuleCategories.UIOptimization,
        Author      = ["ECSS11"]
    };

    private static Config ModuleConfig;

    protected override void Init()
    {
        TaskHelper   ??= new() { TimeLimitMS = 30_000 };
        ModuleConfig =   LoadConfig<Config>() ?? new();

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RepairRequest", OnRepairRequest);
    }

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(100f * GlobalFontScale);
        if (ImGui.SliderInt(GetLoc("AutoAcceptRepairRequest-Slider"), ref ModuleConfig.Delay, 500, 3_000))
            SaveConfig(ModuleConfig);
    }

    private unsafe void OnRepairRequest(AddonEvent type, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon;
        if (addon is null) return;

        var button = addon->GetComponentButtonById(33);
        TaskHelper.DelayNext(ModuleConfig.Delay);
        TaskHelper.Enqueue(() => button->ClickAddonButton(addon));
    }

    protected override void Uninit()
    {
        TaskHelper.Dispose();

        DService.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "RepairRequest");
    }

    private class Config : ModuleConfiguration
    {
        public int Delay = 500;
    }
}
