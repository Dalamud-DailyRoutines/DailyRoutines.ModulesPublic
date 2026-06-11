using DailyRoutines.Extensions;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using OmenTools.Dalamud;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using static FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMiragePrismMiragePlateData;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace DailyRoutines.ModulesPublic.Interface.UnifiedGlamourManager;

public unsafe partial class UnifiedGlamourManager
{
    // 用于记录用户当前没有的幻化
    private readonly List<PlateItemInfo> missingApplyItems = [];

    private bool openMissingApplyItemsPopup;

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

    private void SaveCurrentPlateAsPreset(string title, string note, PresetSource source)
    {
        var isInspect = source == PresetSource.OtherPlayer;
        var items     = isInspect ? GetInspectPlateItems() : GetCurrentPlateItems();
        if (items.Count == 0) return;

        // get目标对象的种族/性别
        if (isInspect)
        {
            sourceRace = (byte)AgentInspect.Instance()->CharaView.Race;
            sourceSex  = (byte)AgentInspect.Instance()->CharaView.Sex;
        }
        else
        {
            sourceRace = Control.GetLocalPlayer()->DrawData.CustomizeData.Race;
            sourceSex  = Control.GetLocalPlayer()->DrawData.CustomizeData.Sex;
        }

        // 根据玩家设置的内容/时间生成一个新的预设
        PlatePreset preset = new()
        {
            Title     = string.IsNullOrWhiteSpace(title) ? GetDefaultPresetTitle(source) : title.Trim(),
            Note      = note.Trim(),
            Race      = GetRaceName(sourceRace, sourceSex),
            Sex       = GetSexName(sourceSex),
            CreatedAt = DateTime.Now
        };

        preset.Items.AddRange(items);

        // 保存预设，清空输入框
        config.Presets.Add(preset);
        selectedPreset = preset;

        config.Save(this);
        NotifyHelper.Instance().NotificationSuccess(Lang.Get("SavedSuccessfully"));
    }

    private bool StartApplyPreset()
    {
        if (!TryGetReadyPlateEditor(out _)    ||
            !TryGetLoadedMirageManager(out _) ||
            selectedPreset == null)
            return false;

        var entries = selectedPreset.Items
                                    .Where(x => x.SlotIndex < PlateSlotAddonTextIDs.Length)
                                    .OrderBy(x => x.SlotIndex)
                                    .ToList();

        missingApplyItems.Clear();

        foreach (var entry in entries)
        {
            var itemID = ItemUtil.GetBaseId(entry.ItemID).ItemId;

            var target = items
                         .Where
                         (x =>
                              x.ItemID == itemID &&
                              x.CanUseInPlate    &&
                              x.IsCompatibleWithSlot(entry.SlotIndex)
                         )
                         .OrderBy(x => x.IsSetPart)
                         .ThenByDescending(x => x.InPrismBox)
                         .FirstOrDefault();

            if (target == null)
            {
                missingApplyItems.Add(entry);
                continue;
            }

            var sentApply      = false;
            var originStain0ID = 0U;
            var originStain1ID = 0U;

            TaskHelper.Enqueue
            (() =>
                {
                    if (!TryGetReadyPlateEditor(out var applyAgent))
                        return false;

                    var slot = (int)entry.SlotIndex;

                    if (!sentApply)
                    {
                        applyAgent->Data->SelectedItemIndex = entry.SlotIndex;

                        if (target.InPrismBox && TryApplyPrismBoxItem(applyAgent, target))
                        {
                            if (!target.IsSetPart)
                            {
                                originStain0ID = target.Stain0ID;
                                originStain1ID = target.Stain1ID;
                            }

                            sentApply = true;
                            return false;
                        }

                        if (target.InCabinet && TryApplyCabinetItem(applyAgent, target))
                        {
                            sentApply = true;
                            return false;
                        }

                        missingApplyItems.Add(entry);
                        return true;
                    }

                    ref var currentItem = ref applyAgent->Data->CurrentItems[slot];

                    if (currentItem.ItemId == 0 || ItemUtil.GetBaseId(currentItem.ItemId).ItemId != itemID)
                    {
                        sentApply = false;
                        return false;
                    }

                    if (entry.Stain0ID != (byte)originStain0ID)
                    {
                        currentItem.Flags |= ItemFlag.HasStain0;

                        var dye0ID = LuminaGetter.TryGetRow<Stain>(entry.Stain0ID, out var dye0Row)
                                         ? dye0Row.Item[0].RowId
                                         : 0;

                        Inventories.Player.TryGetFirstItem(x => x.ItemId == dye0ID, out var dye0);
                        applyAgent->SetItemStain(entry.SlotIndex, entry.Stain0ID, dye0, dye0ID, 0);
                    }

                    if (entry.Stain1ID != (byte)originStain1ID)
                    {
                        currentItem.Flags |= ItemFlag.HasStain1;

                        var dye1ID = LuminaGetter.TryGetRow<Stain>(entry.Stain1ID, out var dye1Row)
                                         ? dye1Row.Item[0].RowId
                                         : 0;

                        Inventories.Player.TryGetFirstItem(x => x.ItemId == dye1ID, out var dye1);
                        applyAgent->SetItemStain(entry.SlotIndex, entry.Stain1ID, dye1, dye1ID, 1);
                    }

                    MarkPlateSelectionDirty(applyAgent);
                    return true;
                }
            );

            TaskHelper.DelayNext(150);
        }

        TaskHelper.Enqueue
        (() =>
            {
                openMissingApplyItemsPopup = missingApplyItems.Count > 0;
                return true;
            }
        );

        return true;
    }

    private bool StartTryOnPreset(PlatePreset preset)
    {
        if (DService.Instance().Condition.IsOccupiedInEvent ||
            AgentTryon.Instance() == null)
            return false;

        var entries = preset.Items
                            .Where
                            (x =>
                                 x.ItemID    > 0                                &&
                                 x.SlotIndex < PlateSlotAddonTextIDs.Length &&
                                 LuminaGetter.TryGetRow<Item>(ItemUtil.GetBaseId(x.ItemID).ItemId, out _)
                            )
                            .OrderBy(x => x.SlotIndex)
                            .ToList();

        if (entries is not { Count: > 0 })
        {
            TaskHelper.Abort();
            return true;
        }

        AgentTryon.Instance()->SaveDeleteOutfit = true;

        foreach (var entry in entries)
        {
            TaskHelper.Enqueue
            (() =>
                {
                    AgentTryon.TryOn(0, entry.ItemID, entry.Stain0ID, entry.Stain1ID);
                    return true;
                }
            );
            TaskHelper.DelayNext(50);
        }

        return true;
    }
}
