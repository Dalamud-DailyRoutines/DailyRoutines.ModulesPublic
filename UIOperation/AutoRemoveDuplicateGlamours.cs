using System.Collections.Generic;
using DailyRoutines.Abstracts;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DailyRoutines.ModulesPublic;

public class AutoRemoveDuplicateGlamours : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoRemoveDuplicateGlamoursTitle"),
        Description = GetLoc("AutoRemoveDuplicateGlamoursDescription"),
        Author      = ["ECSS11"]
    };

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

        List<uint>    itemIndexToRemove = [];
        HashSet<uint> itemIndexHash     = [];
        for (var i = 0U; i < 800; i++)
        {
            var item = instance->PrismBoxItemIds[(int)i];
            if (item == 0) continue;

            var itemId = item % 100_0000;
            if (!itemIndexHash.Add(itemId))
                itemIndexToRemove.Add(i);
        }

        if (itemIndexToRemove.Count == 0) return;

        itemIndexToRemove.ForEach(x => TaskHelper.Enqueue(() => instance->RestorePrismBoxItem(x)));
        TaskHelper.Enqueue(Enqueue);
    }
}
