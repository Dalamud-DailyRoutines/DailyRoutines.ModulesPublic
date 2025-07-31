global using static DailyRoutines.Infos.Widgets;
global using static OmenTools.Helpers.HelpersOm;
global using static DailyRoutines.Infos.Extensions;
global using static OmenTools.Infos.InfosOm;
global using static OmenTools.Helpers.ThrottlerHelper;
global using static DailyRoutines.Managers.Configuration;
global using static DailyRoutines.Managers.LanguageManagerExtensions;
global using static DailyRoutines.Helpers.NotifyHelper;
global using static OmenTools.Helpers.ContentsFinderHelper;
global using Dalamud.Interface.Utility.Raii;
global using OmenTools.Infos;
global using OmenTools.ImGuiOm;
global using OmenTools.Helpers;
global using OmenTools;
global using ImGuiNET;
global using ImPlotNET;
global using Dalamud.Game;

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

    protected override void Init()
    {
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RepairRequest", OnRepairRequest);
    }
    
    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "RepairRequest");
    }

    private unsafe void OnRepairRequest(AddonEvent type, AddonArgs args)
    {
        // TODO: Determine who open the request window
        
        var addon = (AtkUnitBase*)args.Addon;
        if (!IsAddonAndNodesReady(addon)) return;

        var button = addon->GetComponentButtonById(33);
        if (button is null) return;
        button->ClickAddonButton(addon);
    }
}
