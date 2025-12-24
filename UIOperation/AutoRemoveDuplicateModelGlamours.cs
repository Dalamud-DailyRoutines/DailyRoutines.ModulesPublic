using System.Collections.Generic;
using DailyRoutines.Abstracts;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

// TODO: 合并成单一投影台模块
public class AutoRemoveDuplicateModelGlamours : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoRemoveDuplicateModelGlamoursTitle"),
        Description = GetLoc("AutoRemoveDuplicateModelGlamoursDescription"),
        Category    = ModuleCategories.UIOperation,
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    protected override void Init() => 
        TaskHelper ??= new();
    
    protected override void ConfigUI()
    {
        if (ImGui.Button(GetLoc("Start")))
            Enqueue();
        
        ImGui.SameLine();
        if (ImGui.Button(GetLoc("Stop")))
            TaskHelper.Abort();
    }

    private unsafe void Enqueue()
    {
        var instance = MirageManager.Instance();
        if (instance == null) return;

        List<uint>     itemIndexToRemove = [];
        HashSet<uint>  itemIDHash        = [];
        HashSet<ulong> itemModelHash     = [];
        for (var i = 0U; i < 800; i++)
        {
            var item = instance->PrismBoxItemIds[(int)i];
            if (item == 0) continue;

            var itemID = item % 100_0000;
            if (!LuminaGetter.TryGetRow(itemID, out Item row)) continue;
            
            if (!itemIDHash.Add(itemID) || !itemModelHash.Add(row.ModelMain)) continue;
                itemIndexToRemove.Add(i);
        }

        if (itemIndexToRemove.Count == 0) return;

        itemIndexToRemove.ForEach(x => TaskHelper.Enqueue(() => instance->RestorePrismBoxItem(x)));
        TaskHelper.Enqueue(Enqueue);
    }
}
