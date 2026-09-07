using OmenTools.Info.Game.ItemSource;
using OmenTools.Info.Game.ItemSource.Enums;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Interface.AutoShowItemNPCShopInfo;

public partial class AutoShowItemNPCShopInfo
{
    private SourceContextMenu      sourceContextMenu      = null!;
    private DestinationContextMenu destinationContextMenu = null!;

    private sealed class SourceContextMenu : ContextMenuEntry
    {
        public override string Identifier => nameof(AutoShowItemNPCShopInfo);

        public override ContextMenuItem? Create(ContextMenuOpenedArgs args)
        {
            if (!args.TargetItemRow.IsValid ||
                ItemSourceInfo.Query(args.TargetItemRow.RowId).State != ItemSourceQueryState.Ready)
                return null;

            return new()
            {
                Name = Lang.Get("AutoShowItemNPCShopInfo-ContextMenu-Source"),
                OnClicked = clicked => OpenShopInfoByItemID(clicked.Source.TargetItemRow.RowId)
            };
        }
    }

    private sealed class DestinationContextMenu : ContextMenuEntry
    {
        public override string Identifier => $"{nameof(AutoShowItemNPCShopInfo)}_Exchange";

        public override ContextMenuItem? Create(ContextMenuOpenedArgs args)
        {
            if (!args.TargetItemRow.IsValid ||
                ItemSourceInfo.QueryExchangeItems(args.TargetItemRow.RowId).State != ItemSourceQueryState.Ready)
                return null;

            return new()
            {
                Name = Lang.Get("AutoShowItemNPCShopInfo-ContextMenu-Destination"),
                OnClicked = clicked => OpenExchangeInfoByItemID(clicked.Source.TargetItemRow.RowId)
            };
        }
    }
}
