using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using OmenTools.Dalamud;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Interface.UnifiedGlamourManager;

public unsafe partial class UnifiedGlamourManager
{
    private HashSet<uint> ownedEquipment = [];
    private bool          ownedEquipmentComplete;
    private long          nextOwnedEquipmentRefresh;

    private HashSet<uint> GetOwnedEquipment
    (
        out bool complete
    )
    {
        if (!GameState.IsLoggedIn || ICondition.Instance().IsBetweenAreas)
        {
            complete = false;
            return CacheOwnedEquipment([], complete);
        }

        if (Environment.TickCount64 < nextOwnedEquipmentRefresh)
        {
            complete = ownedEquipmentComplete;
            return ownedEquipment;
        }

        HashSet<uint> owned   = [];
        var           manager = InventoryManager.Instance();
        complete = manager != null;

        if (manager != null)
        {
            foreach (var type in OwnedEquipmentContainers)
            {
                var container = manager->GetInventoryContainer(type);
                if (container == null || !container->IsLoaded)
                {
                    complete = false;
                    continue;
                }

                for (var slot = 0; slot < container->Size; slot++)
                {
                    var item = container->GetInventorySlot(slot);
                    if (item == null || item->ItemId == 0)
                        continue;

                    owned.Add(item->GetBaseItemId());
                }
            }
        }

        var storageComplete = AddStorageOwnership(LocalPlayerState.ContentID, owned);
        complete &= storageComplete;
        return CacheOwnedEquipment(owned, complete);
    }

    private static bool AddStorageOwnership
    (
        ulong         contentID,
        HashSet<uint> owned
    )
    {
        var dresserItems = GetPrismBoxItems(contentID);
        var cabinet      = GetCabinetItems(contentID);
        if (dresserItems == null || cabinet == null)
            return false;

        foreach (var (rawItemID, _, missingSlots) in dresserItems)
        {
            var itemID = ItemUtil.GetBaseId(rawItemID).ItemId;
            if (itemID == 0)
                continue;

            if (!GlamourSetItems.TryGetValue(itemID, out var parts))
            {
                owned.Add(itemID);
                continue;
            }

            for (var slot = 0; slot < parts.Length; slot++)
            {
                if (parts[slot] != 0 && IsSetSlotUnlocked(missingSlots, slot))
                    owned.Add(parts[slot]);
            }
        }

        foreach (var (_, itemID) in cabinet)
            owned.Add(itemID);

        var status = GetStorageStatus(contentID);
        return status.PrismBox && status.Cabinet;
    }

    private void InvalidateOwnedEquipment() => nextOwnedEquipmentRefresh = 0;

    private HashSet<uint> CacheOwnedEquipment
    (
        HashSet<uint> owned,
        bool          complete
    )
    {
        if (!ownedEquipment.SetEquals(owned) || ownedEquipmentComplete != complete)
            presetEditorItemsDirty = true;

        ownedEquipment            = owned;
        ownedEquipmentComplete    = complete;
        nextOwnedEquipmentRefresh = Environment.TickCount64 + 500;
        return owned;
    }

    #region 常量

    private static readonly InventoryType[] OwnedEquipmentContainers =
        [.. Inventories.PlayerWithArmory.Where(x => x != InventoryType.ArmoryWaist)];

    private static readonly Dictionary<uint, uint[]> GlamourSetItems =
        LuminaGetter.Get<MirageStoreSetItem>()
                    .ToDictionary
                    (
                        x => x.RowId,
                        x => new[]
                        {
                            x.MainHand.RowId, x.OffHand.RowId, x.Head.RowId, x.Body.RowId, x.Hands.RowId,
                            x.Legs.RowId, x.Feet.RowId, x.Earrings.RowId, x.Necklace.RowId, x.Bracelets.RowId, x.Ring.RowId
                        }
                    );

    #endregion
}
