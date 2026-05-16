using System.Numerics;
using DailyRoutines.Common.KamiToolKit.Addons;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using OmenTools.Interop.Game.ExecuteCommand.Implementations;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoRestoreFurniture : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoRestoreFurnitureTitle"),
        Description = Lang.Get("AutoRestoreFurnitureDescription"),
        Category    = ModuleCategory.UIOperation
    };
    
    private AutoRestoreFurnitureAddon? addon;

    protected override void Init()
    {
        TaskHelper ??= new();

        addon ??= new(this)
        {
            InternalName          = "DRAutoRestoreFurniture",
            Title                 = Info.Title,
            Size                  = new(280f, 170f),
            RememberClosePosition = false
        };

        LogMessageManager.Instance().RegPre(OnLogMessage);
    }

    protected override void Uninit()
    {
        addon?.Dispose();
        addon = null;
        
        LogMessageManager.Instance().Unreg(OnLogMessage);
    }
    
    private void OnLogMessage(ref bool isPrevented, ref uint logMessageID, ref LogMessageQueueItem item)
    {
        if (logMessageID != 3338 || !TaskHelper.IsBusy)
            return;

        isPrevented = true;
    }

    private bool EnqueueRestore(uint startInventory, uint endInventory, bool toStoreRoom)
    {
        var inventoryManager = InventoryManager.Instance();

        for (var i = startInventory; i <= endInventory; i++)
        {
            var type       = (InventoryType)i;
            var contaniner = inventoryManager->GetInventoryContainer(type);
            if (contaniner == null) continue;

            for (var d = 0; d < contaniner->Size; d++)
            {
                var slot = contaniner->GetInventorySlot(d);
                if (slot == null || slot->ItemId == 0) continue;

                TaskHelper.Enqueue(() => HousingCommand.Restore(slot->Container, slot->GetSlot(), toStoreRoom));
                TaskHelper.Enqueue(() => EnqueueRestore(startInventory, endInventory, toStoreRoom));
                return true;
            }
        }

        TaskHelper.Abort();
        return true;
    }

    private sealed class AutoRestoreFurnitureAddon
    (
        AutoRestoreFurniture module
    ) : AttachedAddon("HousingGoods")
    {
        private TextButtonNode? stopButton;
        private TextButtonNode? placedToStoreRoomButton;
        private TextButtonNode? placedToInventoryButton;
        private TextButtonNode? storedToInventoryButton;

        protected override AttachedAddonPosition AttachPosition =>
            AttachedAddonPosition.LeftTop;

        protected override Vector2 PositionOffset =>
            new(0f, 6f);

        protected override bool CanOpenAddon =>
            HousingGoods != null && HousingGoods->IsAddonAndNodesReady();

        protected override bool CanCloseHostAddon(AtkUnitBase* hostAddon) =>
            false;

        protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
        {
            if (WindowNode is WindowNode windowNode)
                windowNode.CloseButtonNode.IsVisible = false;
            
            FlagHelper.UpdateFlag(ref addon->Flags1A1, 0x4,  true);
            FlagHelper.UpdateFlag(ref addon->Flags1A0, 0x80, true);
            FlagHelper.UpdateFlag(ref addon->Flags1A1, 0x40, true);
            FlagHelper.UpdateFlag(ref addon->Flags1A3, 0x1,  true);
            
            var layout = new VerticalListNode
            {
                IsVisible   = true,
                Position    = ContentStartPosition,
                ItemSpacing = 4f,
                Size        = ContentSize,
                FitContents = true,
            };

            stopButton = new()
            {
                IsVisible = true,
                IsEnabled = true,
                Size      = new(ContentSize.X - 8f, 32f),
                String    = Lang.Get("Stop"),
                OnClick   = module.TaskHelper.Abort
            };

            placedToStoreRoomButton = CreateActionButton
            (
                Lang.Get("AutoRestoreFurniture-PlacedToStoreRoom"),
                true,
                false
            );
            placedToInventoryButton = CreateActionButton
            (
                Lang.Get("AutoRestoreFurniture-PlacedToInventory"),
                false,
                false
            );
            storedToInventoryButton = CreateActionButton
            (
                Lang.Get("AutoRestoreFurniture-StoredToInventory"),
                false,
                true
            );

            layout.AddNode(stopButton);
            layout.AddNode(placedToStoreRoomButton);
            layout.AddNode(placedToInventoryButton);
            layout.AddNode(storedToInventoryButton);
            
            layout.AttachNode(this);

            layout.RecalculateLayout();
            
            SetWindowSize(Size.X, ContentStartPosition.Y + layout.Height + 16f);
            layout.Position = ContentStartPosition;
            layout.Height   = layout.Height;
        }

        protected override void OnAttachedAddonUpdate(AtkUnitBase* addon, AtkUnitBase* hostAddon) =>
            RefreshState();

        protected override void OnHostAddon(AddonEvent type, AddonArgs? args)
        {
            if (type == AddonEvent.PreFinalize)
                module.TaskHelper.Abort();
        }

        private TextButtonNode CreateActionButton(string label, bool toStoreRoom, bool isStoredItem)
        {
            var button = new TextButtonNode
            {
                IsVisible = true,
                IsEnabled = true,
                Size      = new(ContentSize.X - 8f, 32f),
                String    = label
            };

            button.OnClick = () =>
            {
                var isOutdoor = HousingManager.Instance()->OutdoorTerritory != null;
                var startInventory = isStoredItem
                                         ? isOutdoor ? 27000U : 27001U
                                         : isOutdoor
                                             ? 25001U
                                             : 25003U;
                var endInventory = isStoredItem
                                       ? isOutdoor ? 27000U : 27008U
                                       : isOutdoor
                                           ? 25001U
                                           : 25010U;

                module.EnqueueRestore(startInventory, endInventory, toStoreRoom);
            };

            return button;
        }

        private void RefreshState()
        {
            var isBusy = module.TaskHelper.IsBusy;

            if (placedToStoreRoomButton != null)
                placedToStoreRoomButton.IsEnabled = !isBusy;

            if (placedToInventoryButton != null)
                placedToInventoryButton.IsEnabled = !isBusy;

            if (storedToInventoryButton != null)
                storedToInventoryButton.IsEnabled = !isBusy;

            if (stopButton != null)
                stopButton.IsEnabled = isBusy;
        }
    }
}
