using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe class AutoTryOnPlayerOutfit : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoTryOnPlayerOutfitTitle"),
        Description = Lang.Get("AutoTryOnPlayerOutfitDescription"),
        Category    = ModuleCategory.Interface,
        Author      = ["ErxCharlotte"]
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private TryOnContextMenu? menuEntry;

    protected override void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 10_000 };

        menuEntry = new(this);
        ContextMenuManager.Instance().Reg(menuEntry);
    }

    protected override void Uninit() =>
        ContextMenuManager.Instance().Unreg(menuEntry);

    private bool StartTryOn()
    {
        if (ICondition.Instance().IsOccupiedInEvent   ||
            !CharacterInspect->IsAddonAndNodesReady() ||
            AgentTryon.Instance() == null)
            return false;

        // 判断当前玩家是否有可以试穿的装备
        var entries = ReadInspectItems();

        if (entries == null || entries.Count == 0)
        {
            TaskHelper.Abort();
            return true;
        }

        // 保存试穿内容
        AgentTryon.Instance()->SaveDeleteOutfit = true;

        foreach (var entry in entries)
        {
            TaskHelper.Enqueue
            (() =>
                {
                    // 种族/性别导致不能试穿的时候跳过继续下一步
                    AgentTryon.TryOn(0, entry.TryOnItemID, entry.Stain0ID, entry.Stain1ID);
                    return true;
                }
            );

            TaskHelper.DelayNext(50);
        }

        return true;
    }

    private static List<TryOnEntry>? ReadInspectItems()
    {
        if (!InventoryType.Examine.TryGetItems
            (
                x => x.ItemId != 0 && TryOnSlots.Contains(x.Slot),
                out var items
            ))
            return null;

        List<TryOnEntry> entries = [];

        foreach (var item in items)
        {
            var itemID        = item.ItemId;
            var glamourItemID = item.GlamourId;

            // 有幻化→试穿幻化，无幻化→试穿原装备
            var tryOnItemID = glamourItemID != 0 ?
                                  glamourItemID :
                                  itemID;

            if (tryOnItemID == 0) continue;

            entries.Add
            (
                new()
                {
                    Slot        = item.Slot,
                    TryOnItemID = tryOnItemID,
                    Stain0ID    = item.Stains[0],
                    Stain1ID    = item.Stains[1]
                }
            );
        }

        return entries;
    }

    private struct TryOnEntry
    {
        public int  Slot;
        public uint TryOnItemID;
        public byte Stain0ID;
        public byte Stain1ID;
    }

    private class TryOnContextMenu(AutoTryOnPlayerOutfit module) : ContextMenuEntry
    {
        public override string Identifier =>
            nameof(AutoTryOnPlayerOutfit);

        public override ContextMenuItem? Create
        (
            ContextMenuOpenedArgs args
        )
        {
            if (args.AddonName != "CharacterInspect")
                return null;
            if (args.TargetItemID == 0)
                return null;

            return new()
            {
                Name = Lang.Get("AutoTryOnPlayerOutfit-TryOnAll"),
                OnClicked = _ =>
                {
                    if (module.TaskHelper.IsBusy)
                        return;
                    module.TaskHelper.Enqueue(module.StartTryOn);
                }
            };
        }
    }
    
    #region 常量
    
    // 5 是腰带，13 是职业水晶
    private static readonly int[] TryOnSlots = [0, 1, 2, 3, 4, 6, 7, 8, 9, 10, 11, 12];
    
    #endregion
}
