using Dalamud.Utility;
using OmenTools.Dalamud.Abstractions;
using OmenTools.Dalamud.Attributes;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Interface.UnifiedGlamourManager;

public partial class UnifiedGlamourManager
{
    private uint                       contextMenuItemID;
    private bool                       openItemSearchMenu;
    private Dictionary<string, string> itemSearchOptions = [];

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

    #region IPC

    [IPCSubscriber("DailyRoutines.Modules.ExpandItemMenuSearch.GetItemSearchOptions")]
    private IPCSubscriber<uint, Dictionary<string, string>> GetItemSearchOptionsIPC;

    [IPCSubscriber("DailyRoutines.Modules.ExpandItemMenuSearch.SearchItem")]
    private IPCSubscriber<uint, string, bool> SearchItemIPC;

    #endregion
}
