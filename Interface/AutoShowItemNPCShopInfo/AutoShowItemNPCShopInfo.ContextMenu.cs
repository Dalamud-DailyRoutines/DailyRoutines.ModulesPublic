using DailyRoutines.Common.Info.Abstractions;
using Dalamud.Game.Gui.ContextMenu;
using OmenTools.Info.Game.ItemSource;
using OmenTools.Info.Game.ItemSource.Enums;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Interface.AutoShowItemNPCShopInfo;

public partial class AutoShowItemNPCShopInfo
{
    private readonly SourceContextMenu      sourceContextMenu      = new();
    private readonly DestinationContextMenu destinationContextMenu = new();

    private void OnMenuOpen(IMenuOpenedArgs args)
    {
        if (sourceContextMenu.IsDisplay(args))
            args.AddMenuItem(sourceContextMenu.Get());

        if (destinationContextMenu.IsDisplay(args))
            args.AddMenuItem(destinationContextMenu.Get());
    }

    private class SourceContextMenu : MenuItemBase
    {
        public override string Name { get; protected set; } = Lang.Get("AutoShowItemNPCShopInfo-ContextMenu-Source");

        public override string Identifier { get; protected set; } = nameof(AutoShowItemNPCShopInfo);

        protected override bool WithDRPrefix { get; set; } = true;

        protected override void OnClicked(IMenuItemClickedArgs args) =>
            OpenShopInfoByItemID(ContextMenuItemManager.Instance().CurrentItemID);

        public override bool IsDisplay(IMenuOpenedArgs args) =>
            ItemSourceInfo.Query(ContextMenuItemManager.Instance().CurrentItemID).State == ItemSourceQueryState.Ready;
    }

    private class DestinationContextMenu : MenuItemBase
    {
        public override string Name { get; protected set; } = Lang.Get("AutoShowItemNPCShopInfo-ContextMenu-Destination");

        public override string Identifier { get; protected set; } = $"{nameof(AutoShowItemNPCShopInfo)}_Exchange";

        protected override bool WithDRPrefix { get; set; } = true;

        protected override void OnClicked(IMenuItemClickedArgs args) =>
            OpenExchangeInfoByItemID(ContextMenuItemManager.Instance().CurrentItemID);

        public override bool IsDisplay(IMenuOpenedArgs args) =>
            ItemSourceInfo.QueryExchangeItems(ContextMenuItemManager.Instance().CurrentItemID).State == ItemSourceQueryState.Ready;
    }
}
