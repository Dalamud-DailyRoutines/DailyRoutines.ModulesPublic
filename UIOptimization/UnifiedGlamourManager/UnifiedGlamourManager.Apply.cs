using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OmenTools.Dalamud;
using static FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMiragePrismMiragePlateData;

namespace DailyRoutines.ModulesPublic.UnifiedGlamourManager;

public unsafe partial class UnifiedGlamourManager
{
    private void ApplySelectedItemToCurrentPlateSlot(UnifiedItem item)
    {
        if (!item.CanUseInPlate || !TryGetReadyPlateEditor(out var agent)) return;

        var selectedSlot = agent->Data->SelectedItemIndex;
        if (!item.IsCompatibleWithSlot(selectedSlot)) return;

        try
        {
            if (item.InPrismBox)
            {
                if (TryApplyPrismBoxItem(agent, item))
                    QueueApplyRetry(item, ItemSource.PrismBox);
            }
            else if (item.InCabinet)
            {
                if (TryApplyCabinetItem(agent, item))
                    QueueApplyRetry(item, ItemSource.Cabinet);
            }
        }
        catch (Exception ex)
        {
            DLog.Warning("UnifiedGlamourManager apply failed", ex);
        }
    }

    private static bool TryApplyPrismBoxItem(AgentMiragePrismMiragePlate* agent, UnifiedItem item)
    {
        if (!TryGetLoadedMirageManager(out var manager)) return false;
        if (item.PrismBoxIndex >= PRISM_BOX_CAPACITY) return false;

        var rawItemID = manager->PrismBoxItemIds[(int)item.PrismBoxIndex];
        if (rawItemID == 0) return false;

        var expectedItemID = item is { IsSetPart: true, ParentSetItemID: not 0 }
                                 ? item.ParentSetItemID
                                 : item.ItemID;
        if (ItemUtil.GetBaseId(rawItemID).ItemId != expectedItemID) return false;

        var itemID = !item.IsSetPart && item.RawItemID != 0 ? item.RawItemID : item.ItemID;
        var stain0 = (byte)(item.IsSetPart ? 0 : item.Stain0ID);
        var stain1 = (byte)(item.IsSetPart ? 0 : item.Stain1ID);

        agent->SetSelectedItemData(ItemSource.PrismBox, item.PrismBoxIndex, itemID, stain0, stain1);
        MarkPlateSelectionDirty(agent);
        return true;
    }

    private static bool TryApplyCabinetItem(AgentMiragePrismMiragePlate* agent, UnifiedItem item)
    {
        var cabinet = GetLoadedCabinet();
        if (cabinet == null) return false;
        if (item.CabinetID == 0 || !cabinet->IsItemInCabinet(item.CabinetID)) return false;

        agent->SetSelectedItemData(ItemSource.Cabinet, item.CabinetID, item.ItemID, 0, 0);
        MarkPlateSelectionDirty(agent);
        return true;
    }

    private void QueueApplyRetry(UnifiedItem item, ItemSource source)
    {
        var itemID          = item.ItemID;
        var prismBoxIndex   = item.PrismBoxIndex;
        var cabinetID       = item.CabinetID;
        var isSetPart       = item.IsSetPart;
        var parentSetItemID = item.ParentSetItemID;

        DService.Instance().Framework.RunOnTick
        (
            () =>
            {
                if (IsDisposed || !TryGetReadyPlateEditor(out var retryAgent)) return;

                var retryItem = items.FirstOrDefault
                (x =>
                     x.ItemID          == itemID          &&
                     x.IsSetPart       == isSetPart       &&
                     x.ParentSetItemID == parentSetItemID &&
                     x.CanUseInPlate                      &&
                     (source == ItemSource.PrismBox
                          ? x.InPrismBox && x.PrismBoxIndex == prismBoxIndex
                          : x.InCabinet  && x.CabinetID     == cabinetID)
                );

                if (retryItem == null) return;
                if (!retryItem.IsCompatibleWithSlot(retryAgent->Data->SelectedItemIndex)) return;

                if (source == ItemSource.PrismBox)
                    TryApplyPrismBoxItem(retryAgent, retryItem);
                else
                    TryApplyCabinetItem(retryAgent, retryItem);
            },
            TimeSpan.FromMilliseconds(APPLY_RETRY_DELAY_MS)
        );
    }

    private static void MarkPlateSelectionDirty(AgentMiragePrismMiragePlate* agent)
    {
        if (agent == null || agent->Data == null) return;

        agent->Data->HasChanges          = true;
        agent->CharaView.IsUpdatePending = true;
    }
}
