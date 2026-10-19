using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using AgentTryon = FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent.AgentTryon;

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

    private bool TryOnAll()
    {
        var agent = AgentTryon.Instance();
        if (agent == null)
            return false;
        
        if (GetInspectItems() is not { Count: > 0 } entries)
        {
            TaskHelper.Abort();
            return true;
        }

        var tryOnItems = agent->TryOnItems;

        if (!agent->IsAddonShown())
            agent->Show();

        agent->ClearTryOnItems();

        var itemIndex = 0;
        var glassesID = GetInspectGlassesID();

        if (glassesID != 0)
        {
            ref var glassesItem = ref tryOnItems[itemIndex++];
            glassesItem.EquipSlotCategory = 13;
            glassesItem.Id                = glassesID;
            glassesItem.IconId            = (uint)LuminaGetter.GetRowOrDefault<Glasses>(glassesID).Icon;
        }

        for (var i = 0; i < entries.Count && itemIndex < tryOnItems.Length; i++)
        {
            ref var tryOnItem = ref tryOnItems[itemIndex++];
            var     entry     = entries[i];

            tryOnItem.EquipSlotCategory = (byte)entry.Slot;
            tryOnItem.Id                = entry.ItemID;
            tryOnItem.GlamourId         = entry.GlamourID;
            tryOnItem.Stain0Id          = entry.Stain0ID;
            tryOnItem.Stain1Id          = entry.Stain1ID;
        }

        agent->TryOnItemsChanged = true;
        agent->RefreshAddon();
        agent->CharaView.DoUpdate = true;

        return true;
    }

    private static List<TryOnEntry>? GetInspectItems()
    {
        var inspect = AgentInspect.Instance();
        if (inspect == null)
            return null;

        InventoryType.Examine.TryGetItems(_ => true, out var examineItems);
        Dictionary<int, InventoryItem> examineSlots = [];

        foreach (var examineItem in examineItems)
            examineSlots[examineItem.Slot] = examineItem;

        List<TryOnEntry> entries = [];

        for (var i = 0; i < inspect->Items.Length; i++)
        {
            if (!TryOnSlots.Contains(i))
                continue;

            ref var data      = ref inspect->Items[i];
            var     itemID    = data.ItemId;
            var     glamourID = data.GlamourItemId;

            if (itemID == 0 && glamourID == 0)
                continue;

            var hasExamine = examineSlots.TryGetValue(i, out var examineItem);

            var entryItemID = itemID != 0 ?
                                  itemID :
                                  glamourID;
            var entryGlamourID = itemID != 0 ?
                                     glamourID :
                                     0;

            if (hasExamine && examineItem.ItemId != 0)
            {
                entryItemID    = examineItem.GetItemId();
                entryGlamourID = examineItem.GlamourId;
            }

            entries.Add
            (
                new()
                {
                    Slot      = i,
                    ItemID    = entryItemID,
                    GlamourID = entryGlamourID,
                    Stain0ID = hasExamine ?
                                   examineItem.Stains[0] :
                                   (byte)0,
                    Stain1ID = hasExamine ?
                                   examineItem.Stains[1] :
                                   (byte)0
                }
            );
        }

        return entries;
    }

    private static ushort GetInspectGlassesID()
    {
        var inspect = AgentInspect.Instance();
        if (inspect == null)
            return 0;

        var glassesID = inspect->CharaView.ModelData.GlassesIds[0];
        if (glassesID != 0)
            return glassesID;

        var objectManager = ClientObjectManager.Instance();
        if (objectManager == null)
            return 0;

        foreach (var entry in objectManager->BattleCharas)
        {
            var character = entry.BattleChara;
            if (character == null || character->EntityId != inspect->CurrentEntityId)
                continue;
            return character->DrawData.GlassesIds[0];
        }

        return 0;
    }

    private struct TryOnEntry
    {
        public int  Slot;
        public uint ItemID;
        public uint GlamourID;
        public byte Stain0ID;
        public byte Stain1ID;
    }

    private class TryOnContextMenu
    (
        AutoTryOnPlayerOutfit module
    ) : ContextMenuEntry
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
                    module.TaskHelper.Enqueue(module.TryOnAll);
                }
            };
        }
    }

    #region 常量

    private static readonly int[] TryOnSlots = [0, 1, 2, 3, 4, 6, 7, 8, 9, 10, 11, 12];

    #endregion
}
