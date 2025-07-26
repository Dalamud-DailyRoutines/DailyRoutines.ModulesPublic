using System.Text.RegularExpressions;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace DailyRoutines.ModulesPublic;

public class GlamourSwitchToCurrentJob : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("GlamourSwitchToCurrentJobTitle"),
        Description = GetLoc("GlamourSwitchToCurrentJobDescription"),
        Category = ModuleCategories.UIOptimization,
        Author = ["ECSS11"]
    };

    protected override void Init() => DService.AddonLifecycle.RegisterListener(
        AddonEvent.PostRequestedUpdate, "MiragePrismPrismBox",
        OnMiragePrismPrismBox);

    private unsafe void OnMiragePrismPrismBox(AddonEvent type, AddonArgs args)
    {
        // 获取当前职业和幻化台 Addon
        LuminaGetter.TryGetRow<Lumina.Excel.Sheets.ClassJob>(DService.ObjectTable.LocalPlayer.ClassJob.RowId,
                                                             out var job);
        var addon = (AddonMiragePrismPrismBox*)args.Addon;
        var currentJob = job.Name.ToString();

        // 一种基于暴力循环实现的匹配
        var jobDropdownLength = addon->JobDropdown->List->ListLength;
        var pattern = new Regex(@"\b" + currentJob + @"\b");
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
