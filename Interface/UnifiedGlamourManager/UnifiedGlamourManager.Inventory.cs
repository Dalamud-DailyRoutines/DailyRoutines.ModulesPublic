using System.Numerics;
using Dalamud.Interface.Colors;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.Dalamud;
using OmenTools.Dalamud.Abstractions;
using OmenTools.Dalamud.Attributes;
using OmenTools.Info.Game.Data;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Interface.UnifiedGlamourManager;

public unsafe partial class UnifiedGlamourManager
{
    private HashSet<uint> ownedEquipment = [];
    private bool          ownedEquipmentComplete;
    private long          nextOwnedEquipmentRefresh;

    private uint                       contextMenuItemID;
    private bool                       openItemSearchMenu;
    private Dictionary<string, string> itemSearchOptions = [];

    private static readonly InventoryType[] OwnedEquipmentContainers =
        [.. Inventories.PlayerWithArmory.Where(x => x != InventoryType.ArmoryWaist)];

    private HashSet<uint> GetOwnedEquipment
    (
        out bool complete
    )
    {
        if (Environment.TickCount64 < nextOwnedEquipmentRefresh)
        {
            complete = ownedEquipmentComplete;
            return ownedEquipment;
        }

        if (!IClientState.Instance().IsLoggedIn)
        {
            complete = false;
            return CacheOwnedEquipment([], false);
        }

        var owned = items.Where(x => !x.IsSetContainer &&
                                     ((x.InPrismBox && hasReadPrismBoxItems) || (x.InCabinet && hasReadCabinetItems)))
                         .Select(x => x.ItemID)
                         .ToHashSet();
        complete    = hasReadPrismBoxItems && hasReadCabinetItems && !isRefreshingItems;
        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            complete = false;
            return CacheOwnedEquipment(owned, complete);
        }

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

        return CacheOwnedEquipment(owned, complete);
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

    private void ResetStoredItemsSession()
    {
        refreshTaskHelper?.Abort();
        TaskHelper?.Abort();
        InvalidatePresetPreview();
        openItemSearchMenu       = false;
        itemSearchOptions        = [];
        pendingStorageRefresh    = false;
        pendingReselectItem      = null;
        isRefreshingItems        = false;
        hasReadPrismBoxItems     = false;
        hasReadCabinetItems      = false;
        lastLoadedStorageSources = StoredItemSource.None;
        items.Clear();
        filteredItems.Clear();
        selectedItem = null;
        ownedEquipment.Clear();
        ownedEquipmentComplete = false;
        InvalidateOwnedEquipment();
        presetEditorItemsDirty = true;
        MarkFilteredItemsDirty(true, true);
    }

    private void OnStoredItemsLogout
    (
        int code,
        int type
    ) => ResetStoredItemsSession();

    private void HandlePresetItemContextMenu
    (
        uint itemID
    )
    {
        if (itemID != 0 && ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            contextMenuItemID  = ItemUtil.GetBaseId(itemID).ItemId;
            openItemSearchMenu = true;
        }
    }

    private void DrawPresetItemSearchMenu()
    {
        const string popupID = "##EditorItemContextMenu";
        if (openItemSearchMenu)
        {
            openItemSearchMenu = false;
            itemSearchOptions  = GetItemSearchOptionsIPC.TryInvokeFunc(contextMenuItemID) ?? [];

            ImGui.OpenPopup(popupID);
        }

        using var popup = ImRaii.Popup(popupID);
        if (!popup)
        {
            itemSearchOptions = [];
            return;
        }

        if (itemSearchOptions.Count == 0)
            ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-Editor-EnableSearchMenu"));

        foreach (var (key, label) in itemSearchOptions)
        {
            if (ImGui.MenuItem(label) && !SearchItemIPC.TryInvokeFunc(contextMenuItemID, key))
                NotifyHelper.Instance().NotificationError(Lang.Get("UnifiedGlamourManager-Editor-EnableSearchMenu"));
        }
    }

    private void DrawPresetItemIcon
    (
        uint          iconID,
        uint          itemID,
        HashSet<uint> owned,
        bool          complete
    )
    {
        DrawItemIcon(iconID, ImGui.GetFrameHeight());
        HandlePresetItemContextMenu(itemID);
        if (owned.Contains(ItemUtil.GetBaseId(itemID).ItemId))
            return;

        var min    = ImGui.GetItemRectMin();
        var max    = ImGui.GetItemRectMax();
        var radius = 5 * GlobalUIScale;
        var center = new Vector2(max.X - radius, min.Y + radius);
        var draw   = ImGui.GetWindowDrawList();
        draw.AddCircleFilled(center, radius, ImGui.GetColorU32(ImGuiCol.WindowBg));
        if (complete)
        {
            var offset = new Vector2(radius * 0.5f);
            draw.AddLine
            (
                center - offset,
                center + offset,
                ImGui.GetColorU32(ImGuiColors.DalamudRed),
                2 * GlobalUIScale
            );
            draw.AddLine
            (
                center + new Vector2(-offset.X, offset.Y),
                center + new Vector2(offset.X, -offset.Y),
                ImGui.GetColorU32(ImGuiColors.DalamudRed),
                2 * GlobalUIScale
            );
        }
        else
        {
            var textSize = ImGui.CalcTextSize("?");
            draw.AddText(center - (textSize / 2), ImGui.GetColorU32(ImGuiColors.DalamudYellow), "?");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(Lang.Get(complete ?
                                          "UnifiedGlamourManager-Editor-NotOwnedHint" :
                                          "UnifiedGlamourManager-Editor-OwnershipUnknownHint"));
        }
    }

    #region IPC

    [IPCSubscriber("DailyRoutines.Modules.ExpandItemMenuSearch.GetItemSearchOptions")]
    private IPCSubscriber<uint, Dictionary<string, string>> GetItemSearchOptionsIPC;

    [IPCSubscriber("DailyRoutines.Modules.ExpandItemMenuSearch.SearchItem")]
    private IPCSubscriber<uint, string, bool> SearchItemIPC;

    #endregion
}
