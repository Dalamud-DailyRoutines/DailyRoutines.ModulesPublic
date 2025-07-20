using System;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.Gui.ContextMenu;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public class CopyItemNameContextMenu : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("CopyItemNameContextMenuTitle"),
        Description = GetLoc("CopyItemNameContextMenuDescription"),
        Category    = ModuleCategories.System,
        Author      = ["Nukoooo"]
    };

    private static readonly string CopyItemNameString = LuminaWrapper.GetAddonText(159);
    private static readonly string GlamoursString     = LuminaGetter.GetRow<CircleActivity>(18)!.Value.Name.ExtractText();

    private static readonly CopyItemNameMenuItem MenuItem        = new(CopyItemNameString);
    private static readonly CopyItemNameMenuItem GlamourMenuItem = new($"{CopyItemNameString} ({GlamoursString})");

    public override void Init() => 
        DService.ContextMenu.OnMenuOpened += OnContextMenuOpened;

    public override void Uninit()
    {
        DService.ContextMenu.OnMenuOpened -= OnContextMenuOpened;

        base.Uninit();
    }

    private static unsafe void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        var type = args.MenuType;

        if (type == ContextMenuType.Inventory)
        {
            if (args.Target is MenuTargetInventory { TargetItem: { ItemId: > 0 } item })
            {
                MenuItem.SetRawItemId(item.ItemId);

                args.AddMenuItem(MenuItem.Get());

                if (item.GlamourId == 0)
                    return;

                GlamourMenuItem.SetRawItemId(item.GlamourId);
                args.AddMenuItem(GlamourMenuItem.Get());
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(args.AddonName) || args.AddonName == "FreeCompanyExchange")
            return;

        var agent = (AgentContext*)args.AgentPtr;

        var contextMenu = agent->CurrentContextMenu;

        var contextMenuCounts = contextMenu->EventParams[0].Int;

        const int Start = 7;
        var       end   = Start + contextMenuCounts;

        for (var i = Start; i < end; i++)
        {
            var param = contextMenu->EventParams[i];
            var str   = param.GetValueAsString();

            if (str.Equals(CopyItemNameString, StringComparison.OrdinalIgnoreCase))
                return;
        }

        var prismBoxItem = ContextMenuItemManager.GetPrismBoxItem(args);

        var itemId = prismBoxItem?.RowId ?? ContextMenuItemManager.CurrentItemID;

        if (itemId == 0)
            return;

        MenuItem.SetRawItemId(itemId);
        args.AddMenuItem(MenuItem.Get());

        var glamourId = ContextMenuItemManager.CurrentGlamourID;

        if (glamourId == 0)
            return;

        GlamourMenuItem.SetRawItemId(glamourId);
        args.AddMenuItem(GlamourMenuItem.Get());
    }

    private sealed class CopyItemNameMenuItem(string name) : MenuItemBase
    {
        public override string Name { get; protected set; } = name;
        protected override bool WithDRPrefix { get; set; } = true;

        private uint ItemID;
        

        protected override unsafe void OnClicked(IMenuItemClickedArgs args)
        {
            var itemName = string.Empty;

            if (ItemID >= 2000000 && LuminaGetter.TryGetRow<EventItem>(ItemID, out var eventItem))
                itemName = eventItem.Singular.ExtractText();
            else
            {
                ItemID %= 500000;

                if (LuminaGetter.TryGetRow<Item>(ItemID, out var item))
                    itemName = item.Name.ExtractText();
            }

            if (string.IsNullOrWhiteSpace(itemName))
                return;

            RaptureLogModule.Instance()->ShowLogMessageUInt(1632, ItemID);

            ImGui.SetClipboardText(itemName);
            ItemID = 0;
        }

        public void SetRawItemId(uint id) => 
            ItemID = id;
    }
}
