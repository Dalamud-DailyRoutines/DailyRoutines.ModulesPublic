using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

// TODO: 合并成单一投影台模块
public unsafe class AutoRemoveArmoireItemsFromDresser : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoRemoveArmoireItemsFromDresserTitle"),
        Description = GetLoc("AutoRemoveArmoireItemsFromDresserDescription"),
        Category    = ModuleCategories.UIOperation,
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static readonly HashSet<uint> ArmoireAvailableItems =
        LuminaGetter.Get<Cabinet>()
                    .Where(x => x.Item.RowId > 0)
                    .Select(x => x.Item.RowId)
                    .ToHashSet();

    protected override void Init() => 
        TaskHelper ??= new() { TimeLimitMS = 5_000 };

    protected override void ConfigUI()
    {
        using (ImRaii.Disabled(TaskHelper.IsBusy))
        {
            if (ImGui.Button(GetLoc("Start")))
                RestorePrismBoxItem();
        }
        
        ImGui.SameLine();
        if (ImGui.Button(GetLoc("Stop"))) 
            TaskHelper.Abort();
    }

    private void RestorePrismBoxItem()
    {
        var instance = MirageManager.Instance();
        if (instance == null) return;

        List<uint> validItemIndex = [];
        for (var i = 0U; i < 800; i++)
        {
            var item = instance->PrismBoxItemIds[(int)i];
            if (item == 0) continue;
            
            var itemID = item % 100_0000;
            if (ArmoireAvailableItems.Contains(itemID))
                validItemIndex.Add(i);
        }
        if (validItemIndex.Count == 0) return;
        
        validItemIndex.ForEach(x => TaskHelper.Enqueue(() => instance->RestorePrismBoxItem(x)));
        TaskHelper.Enqueue(RestorePrismBoxItem);
    }
}
