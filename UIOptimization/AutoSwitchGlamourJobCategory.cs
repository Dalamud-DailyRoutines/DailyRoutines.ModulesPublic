using System.Text.RegularExpressions;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace DailyRoutines.ModulesPublic;

public class AutoSwitchGlamourJobCategory : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoSwitchGlamourJobCategoryTitle"),
        Description = GetLoc("AutoSwitchGlamourJobCategoryDescription"),
        Category    = ModuleCategories.UIOptimization,
        Author      = ["ECSS11"]
    };

    protected override void Init() => 
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "MiragePrismPrismBox", OnMiragePrismPrismBox);

    private static unsafe void OnMiragePrismPrismBox(AddonEvent type, AddonArgs args)
    {
        var addon      = (AddonMiragePrismPrismBox*)args.Addon;
        var currentJob = LocalPlayerState.ClassJobData.Name.ExtractText();

        var jobDropdownLength = addon->JobDropdown->List->ListLength;
        var pattern           = new Regex(@"\b" + currentJob + @"\b", RegexOptions.IgnoreCase);
        for (var i = 1; i < jobDropdownLength; i++)
        {
            var currentLabel = addon->JobDropdown->List->GetItemLabel(i).ToString();
            if (pattern.IsMatch(currentLabel))
                addon->JobDropdown->SelectItem(i);
        }
    }

    protected override void Uninit() =>
        DService.AddonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, "MiragePrismPrismBox");
}
