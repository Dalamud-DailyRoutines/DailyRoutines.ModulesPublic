using DailyRoutines.Common.KamiToolKit.Addons.InputNumeric;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoUseItemStacks : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoUseItemStacksTitle"),
        Description = Lang.Get("AutoUseItemStacksDescription"),
        Category    = ModuleCategory.System,
        Author      = ["Cindy-Master"]
    };

    private OpenCofferMenuItem openCofferMenu = null!;
    private DRInputNumeric?    drInputNumeric;
    
    protected override void Init()
    {
        TaskHelper = new() { TimeoutMS = 5_000 };

        openCofferMenu = new(this);
        ContextMenuManager.Instance().Reg(openCofferMenu);
    }

    protected override void Uninit()
    {
        ContextMenuManager.Instance().Unreg(openCofferMenu);

        drInputNumeric?.Dispose();
        drInputNumeric = null;
    }

    protected override void ConfigUI() =>
        ImGuiOm.ConflictKeyText();

    public void EnqueueOpenCoffers
    (
        uint          itemID,
        InventoryType inventoryType,
        ushort        inventorySlot,
        uint          leftCount,
        uint          finishRound
    )
    {
        if (TaskHelper.AbortByConflictKey(this))
        {
            NotifyFinished();
            return;
        }

        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            NotifyFinished();
            return;
        }

        var container = manager->GetInventoryContainer(inventoryType);
        if (container == null)
        {
            NotifyFinished();
            return;
        }

        var slot = container->GetInventorySlot(inventorySlot);
        if (slot == null)
        {
            NotifyFinished();
            return;
        }

        var currentQuantity = slot->GetQuantity();
        if (slot->GetBaseItemId() != itemID ||
            currentQuantity       <= leftCount)
        {
            NotifyFinished();
            return;
        }

        TaskHelper.Enqueue(() => AgentInventoryContext.Instance()->UseItem(itemID, inventoryType, inventorySlot));
        TaskHelper.Enqueue
        (() =>
            {
                var containerInner = manager->GetInventoryContainer(inventoryType);
                if (containerInner == null) return true;

                var slotInner = containerInner->GetInventorySlot(inventorySlot);
                if (slotInner == null) return true;

                if (slotInner->GetBaseItemId() != itemID ||
                    slotInner->GetQuantity()   != currentQuantity)
                    return true;

                return false;
            }
        );
        TaskHelper.Enqueue(() => EnqueueOpenCoffers(itemID, inventoryType, inventorySlot, leftCount, finishRound + 1));

        return;

        void NotifyFinished()
        {
            if (finishRound == 0)
                return;
            
            var finishMessage = Lang.GetSe("AutoUseItemStacks-Notification-Finished", finishRound, SeString.CreateItemLink(itemID, false));
            NotifyHelper.Toast(finishMessage);
            NotifyHelper.Instance().Chat(finishMessage);
        }
    }

    private sealed class OpenCofferMenuItem
    (
        AutoUseItemStacks module
    ) : ContextMenuEntry
    {
        public override string Identifier =>
            nameof(AutoUseItemStacks);

        public override ContextMenuItem? Create
        (
            ContextMenuOpenedArgs args
        )
        {
            if (args.TargetInventoryItem is not { } item) 
                return null;

            var quantity = item.GetQuantity();
            if (quantity < 2) return null;

            var itemID = item.GetBaseItemId();
            if (LuminaGetter.GetRow<Item>(itemID) is not { StackSize: > 1, ItemAction.RowId: 367 or 388 or 2462 })
                return null;

            return new()
            {
                Name = Lang.Get("AutoUseItemStacks-ContextMenu"),
                OnClicked = clickedArgs =>
                {
                    if (module.drInputNumeric != null &&
                        !AddonHelper.TryGetPtrByName("DRInputNumeric", out _))
                    {
                        try
                        {
                            module.drInputNumeric?.Dispose();
                            module.drInputNumeric = null;
                        }
                        catch
                        {
                            // 谁敢猜这个时候会发生什么
                        }
                    }

                    if (module.drInputNumeric != null)
                        return;
                    
                    module.drInputNumeric = DRInputNumeric.Open
                    (
                        new()
                        {
                            Prompt = Lang.Get("AutoUseItemStacks-Popup-PleaseInput"),
                            Value  = (int)quantity,
                            Min    = 1,
                            Max    = (int)quantity,
                            Callback = (addon, result) =>
                            {
                                module.drInputNumeric = null;

                                if (result != DRInputNumericResult.Confirmed)
                                    return;

                                module.EnqueueOpenCoffers
                                (
                                    itemID,
                                    item.Container,
                                    (ushort)item.Slot,
                                    (uint)Math.Max(quantity - addon.Value, 0),
                                    0
                                );
                            },
                            Position = new
                            (
                                args.Addon->RootNode->GetNodeState().Center,
                                AddonPositionAlignment.TopCenter
                            ),
                        }
                    );
                }
            };
        }
    }
}
