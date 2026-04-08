using DailyRoutines.Common.Info.Abstractions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.ContextMenu;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoUseItemStacks : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoUseItemStacksTitle"),
        Description = Lang.Get("AutoUseItemStacksDescription"),
        Category    = ModuleCategory.General,
        Author      = ["Cindy-Master"]
    };

    protected override void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 5_000 };
        
        DService.Instance().ContextMenu.OnMenuOpened +=  OnContextMenuOpened;
    }
    
    protected override void Uninit() =>
        DService.Instance().ContextMenu.OnMenuOpened -= OnContextMenuOpened;

    protected override void ConfigUI() =>
        ImGuiOm.ConflictKeyText();

    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        if (args.Target is not MenuTargetInventory targetInventory) return;
        if (DService.Instance().Condition.IsOccupiedInEvent) return;

        var itemID = targetInventory.TargetItem?.ItemId ?? 0;
        if (itemID == 0) return;

        if (IsCofferItem(itemID))
            args.AddMenuItem(new OpenAllCoffersMenuItem(this, itemID).Get());
    }

    public void EnqueueOpenAllCoffers(uint itemID)
    {
        if (TaskHelper.AbortByConflictKey(this)) return;
        if (!Inventories.Player.TryGetFirstItem(x => x.ItemId == itemID, out _)) return;

        TaskHelper.Enqueue(() => AgentInventoryContext.Instance()->UseItem(itemID));
        TaskHelper.DelayNext(500);
        TaskHelper.Enqueue(() => !DService.Instance().Condition[ConditionFlag.Casting]);
        TaskHelper.DelayNext(500);
        TaskHelper.Enqueue(() => EnqueueOpenAllCoffers(itemID));
    }

    private static bool IsCofferItem(uint itemID) =>
        LuminaGetter.GetRow<Item>(itemID) is { StackSize: > 1, ItemAction.RowId: 367 or 388 or 2462 };

    private class OpenAllCoffersMenuItem
    (
        AutoUseItemStacks Module,
        uint              ItemID
    ) : MenuItemBase
    {
        public override string Name       { get; protected set; } = Lang.Get("AutoUseItemStacks-MenuItem");
        public override string Identifier { get; protected set; } = nameof(AutoUseItemStacks);

        protected override bool WithDRPrefix { get; set; } = true;

        protected override void OnClicked(IMenuItemClickedArgs args) =>
            Module.EnqueueOpenAllCoffers(ItemID);
    }
}
