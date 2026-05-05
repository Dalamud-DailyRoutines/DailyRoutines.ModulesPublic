using System.Diagnostics;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OmenTools.Info.Game.Data;
using static FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMiragePrismMiragePlateData;
using OmenTools.Dalamud;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class UnifiedGlamourManager
{
    #region 应用

    private void ApplySelectedItemToCurrentPlateSlot(UnifiedItem item)
    {
        if (item.PreviewOnly || !item.CanUseInPlate)
            return;

        var agent = AgentMiragePrismMiragePlate.Instance();
        if (agent == null || agent->Data == null)
            return;

        var selectedSlot = agent->Data->SelectedItemIndex;
        if (filterByCurrentPlateSlot && !CanItemUseInPlateSlot(item, selectedSlot))
            return;

        try
        {
            if (item.InPrismBox)
            {
                ApplyPrismBoxItem(agent, item);
                return;
            }

            if (item.InCabinet)
                ApplyCabinetItem(agent, item);
        }
        catch (Exception ex)
        {
            DLog.Warning($"Failed: {ex}");
        }
    }

    private void ApplyPrismBoxItem(AgentMiragePrismMiragePlate* agent, UnifiedItem item)
    {
        var itemID = item.IsSetPart
            ? item.ItemID
            : item.RawItemID != 0
                ? item.RawItemID
                : item.ItemID;

        var stain0 = item.IsSetPart ? (byte)0 : SafeByte(item.Stain0ID);
        var stain1 = item.IsSetPart ? (byte)0 : SafeByte(item.Stain1ID);

        agent->SetSelectedItemData(ItemSource.PrismBox, item.PrismBoxIndex, itemID, stain0, stain1);
        agent->Data->HasChanges = true;
    }

    private void ApplyCabinetItem(AgentMiragePrismMiragePlate* agent, UnifiedItem item)
    {
        if (!TryApplyCabinetItem(agent, item))
            return;

        var itemID = item.ItemID;
        var cabinetID = item.CabinetID;

        DService.Instance().Framework.RunOnTick(
            () =>
            {
                var retryAgent = AgentMiragePrismMiragePlate.Instance();
                if (retryAgent == null || retryAgent->Data == null)
                    return;

                var retryItem = items.FirstOrDefault(x =>
                    x.ItemID == itemID &&
                    x.CabinetID == cabinetID &&
                    x.InCabinet &&
                    x.CanUseInPlate &&
                    !x.PreviewOnly);

                if (retryItem == null)
                    return;

                TryApplyCabinetItem(retryAgent, retryItem);
            },
            TimeSpan.FromMilliseconds(50));
    }

    private bool TryApplyCabinetItem(AgentMiragePrismMiragePlate* agent, UnifiedItem item)
    {
        var uiState = UIState.Instance();
        if (uiState == null)
            return false;

        var cabinet = &uiState->Cabinet;
        if (!cabinet->IsCabinetLoaded())
            return false;

        if (item.CabinetID == 0 || !cabinet->IsItemInCabinet(item.CabinetID))
            return false;

        agent->SetSelectedItemData(ItemSource.Cabinet, item.CabinetID, item.ItemID, 0, 0);
        agent->Data->HasChanges = true;

        return true;
    }

    #endregion

    #region 取出

    private void RestoreSelectedPrismBoxItem(UnifiedItem item)
    {
        if (item.IsSetPart)
            return;

        if (!CanRestorePrismBoxItem(item))
            return;

        var manager = MirageManager.Instance();
        var rawItemID = manager->PrismBoxItemIds[(int)item.PrismBoxIndex];
        if (!IsExpectedPrismBoxItem(item, rawItemID))
            return;

        isRestoringItem = true;

        try
        {
            manager->RestorePrismBoxItem(item.PrismBoxIndex);
            RefreshAll();
            ReselectRestoredItem(item);
        }
        catch (Exception ex)
        {
            DLog.Warning($"Failed: {ex}");
        }
        finally
        {
            isRestoringItem = false;
        }
    }   


    private bool CanRestorePrismBoxItem(UnifiedItem item)
    {
        if (isRestoringItem || !item.InPrismBox || item.PreviewOnly || item.IsSetPart)
            return false;

        if (item.PrismBoxIndex >= PRISM_BOX_CAPACITY)
            return false;

        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxRequested || !manager->PrismBoxLoaded)
            return false;

        return !Inventories.Player.IsFull();
    }


    private static bool IsExpectedPrismBoxItem(UnifiedItem item, uint rawItemID)
    {
        if (rawItemID == 0)
            return false;

        var itemID = rawItemID % 1_000_000;
        var expectedItemID = item is { IsSetPart: true, ParentSetItemID: not 0 }
            ? item.ParentSetItemID
            : item.ItemID;

        return itemID == expectedItemID;
    }

    private void ReselectRestoredItem(UnifiedItem item)
    {
        if (selectedItem == null)
            return;

        selectedItem = items.FirstOrDefault(x =>
            x.ItemID == item.ItemID &&
            x.PrismBoxIndex == item.PrismBoxIndex &&
            x.IsSetPart == item.IsSetPart &&
            x.ParentSetItemID == item.ParentSetItemID);
    }

    #endregion
}
