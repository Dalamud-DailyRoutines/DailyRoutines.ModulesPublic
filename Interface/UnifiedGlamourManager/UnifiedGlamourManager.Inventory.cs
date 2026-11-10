using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using OmenTools.Dalamud;
using OmenTools.Info.Game.Data;
using CabinetEx = FFXIVClientStructs.Expansion.FFXIV.Client.Game.UI.Cabinet;
using GameCabinet = FFXIVClientStructs.FFXIV.Client.Game.UI.Cabinet;

namespace DailyRoutines.ModulesPublic;

internal unsafe partial class UnifiedGlamourManager
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
        complete = true;

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

        var storageComplete = AddStorageOwnership(owned);
        complete &= storageComplete;
        return CacheOwnedEquipment(owned, complete);
    }

    private bool AddStorageOwnership
    (
        HashSet<uint> owned
    )
    {
        if (!TryGetStorageFinder(out var finder))
            return false;

        var cabinet = (CabinetEx*)&UIState.Instance()->Cabinet;
        foreach (var item in items)
        {
            if (!item.IsSetContainer &&
                (!item.IsSetPart ||
                 !item.InPrismBox ||
                 item.InCabinet ||
                 finder->IsGlamourDresserCached))
                owned.Add(item.ItemID);
        }

        return !isRefreshingItems                                               &&
               !isStoredItemsRefreshPending                                     &&
               finder->IsGlamourDresserCached                                   &&
               cabinet->State                == GameCabinet.CabinetState.Loaded &&
               cabinet->AdditionalItemsState == GameCabinet.CabinetState.Loaded;
    }

    private HashSet<uint> CacheOwnedEquipment
    (
        HashSet<uint> owned,
        bool          complete
    )
    {
        if (!ownedEquipment.SetEquals(owned) || ownedEquipmentComplete != complete)
            presetEditor.ItemsDirty = true;

        ownedEquipment            = owned;
        ownedEquipmentComplete    = complete;
        nextOwnedEquipmentRefresh = Environment.TickCount64 + 500;
        return owned;
    }

    #region 常量

    private static readonly InventoryType[] OwnedEquipmentContainers =
        [.. Inventories.PlayerWithArmory.Where(x => x != InventoryType.ArmoryWaist)];

    #endregion
}
