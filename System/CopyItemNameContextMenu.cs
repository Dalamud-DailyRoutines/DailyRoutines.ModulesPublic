using System;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
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

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static readonly string CopyItemNameString = LuminaWrapper.GetAddonText(159);
    private static readonly string GlamoursString     = LuminaGetter.GetRow<CircleActivity>(18)!.Value.Name.ToString();

    private static readonly CopyItemNameMenuItem MenuItem        = new(CopyItemNameString);
    private static readonly CopyItemNameMenuItem GlamourMenuItem = new($"{CopyItemNameString} ({GlamoursString})");

    protected override void Init() =>
        DService.Instance().ContextMenu.OnMenuOpened += OnContextMenuOpened;

    protected override void Uninit() =>
        DService.Instance().ContextMenu.OnMenuOpened -= OnContextMenuOpened;

    private static unsafe void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        var type = args.MenuType;

        if (type == ContextMenuType.Inventory)
        {
            if (args.Target is MenuTargetInventory { TargetItem: { ItemId: > 0 } item })
            {
                MenuItem.SetRawItemID(item.ItemId);

                args.AddMenuItem(MenuItem.Get());

                if (item.GlamourId == 0)
                    return;

                GlamourMenuItem.SetRawItemID(item.GlamourId);
                args.AddMenuItem(GlamourMenuItem.Get());
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(args.AddonName) || args.AddonName == "FreeCompanyExchange")
            return;

        var agent = (AgentContext*)args.AgentPtr;

        var contextMenu = agent->CurrentContextMenu;

        var contextMenuCounts = contextMenu->EventParams[0].Int;

        const int START = 8;
        var       end   = START + contextMenuCounts;

        for (var i = START; i < end; i++)
        {
            var param = contextMenu->EventParams[i];
            var str   = param.GetValueAsString();

            if (str.Equals(CopyItemNameString, StringComparison.OrdinalIgnoreCase))
                return;
        }

        var prismBoxItem = ContextMenuItemManager.Instance().GetPrismBoxItem(args);

        var itemID = prismBoxItem?.RowId ?? ContextMenuItemManager.Instance().CurrentItemID;
        if (itemID == 0) return;

        MenuItem.SetRawItemID(itemID);
        args.AddMenuItem(MenuItem.Get());

        var glamourID = ContextMenuItemManager.Instance().CurrentGlamourID;
        if (glamourID == 0) return;

        GlamourMenuItem.SetRawItemID(glamourID);
        args.AddMenuItem(GlamourMenuItem.Get());
    }

    private sealed class CopyItemNameMenuItem
    (
        string name
    ) : MenuItemBase
    {
        public override string Name       { get; protected set; } = name;
        public override string Identifier { get; protected set; } = nameof(CopyItemNameContextMenu);

        protected override bool WithDRPrefix { get; set; } = true;

        private uint itemID;

        protected override unsafe void OnClicked(IMenuItemClickedArgs args)
        {
            var itemName = string.Empty;

            if (itemID >= 2000000 && LuminaGetter.TryGetRow<EventItem>(itemID, out var eventItem))
                itemName = eventItem.Singular.ToString();
            else
            {
                itemID %= 500000;

                if (LuminaGetter.TryGetRow<Item>(itemID, out var item))
                    itemName = item.Name.ToString();
            }

            if (string.IsNullOrWhiteSpace(itemName))
                return;

            RaptureLogModule.Instance()->ShowLogMessageUInt(1632, itemID);

            ImGui.SetClipboardText(itemName);
            itemID = 0;
        }

        public void SetRawItemID(uint id) =>
            itemID = id;
    }
}
