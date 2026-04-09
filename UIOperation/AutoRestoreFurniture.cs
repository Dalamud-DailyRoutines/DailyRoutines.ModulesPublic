using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.Info.Game.Enums;
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

    protected override void Init()
    {
        TaskHelper ??= new();
        Overlay    ??= new(this);

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "HousingGoods", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "HousingGoods", OnAddon);
        if (HousingGoods != null)
            OnAddon(AddonEvent.PostSetup, null);
    }
    
    protected override void Uninit() =>
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);

    protected override void OverlayUI()
    {
        if (HousingGoods == null)
        {
            Overlay.IsOpen = false;
            return;
        }

        var pos = new Vector2(HousingGoods->GetX() - ImGui.GetWindowSize().X, HousingGoods->GetY() + 6);
        ImGui.SetWindowPos(pos);

        using (FontManager.Instance().UIFont80.Push())
        {
            var isOutdoor = HousingGoods->AtkValues[9].UInt != 6U;

            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("AutoRestoreFurnitureTitle"));

            ImGui.SameLine();
            ImGui.TextUnformatted($"({Lang.Get(isOutdoor ? "Outdoors" : "Indoors")})");

            ImGui.SameLine();
            ImGui.Spacing();

            ImGui.SameLine();
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Stop, $" {Lang.Get("Stop")}"))
                TaskHelper.Abort();

            using (ImRaii.Disabled(TaskHelper.IsBusy))
            {
                if (ImGui.Selectable($"    {Lang.Get("AutoRestoreFurniture-PlacedToStoreRoom")}"))
                    EnqueueRestore(isOutdoor ? 25001U : 25003U, isOutdoor ? 25001U : 25010U, !isOutdoor, 65536);

                if (ImGui.Selectable($"    {Lang.Get("AutoRestoreFurniture-PlacedToInventory")}"))
                    EnqueueRestore(isOutdoor ? 25001U : 25003U, isOutdoor ? 25001U : 25010U, !isOutdoor);

                if (ImGui.Selectable($"    {Lang.Get("AutoRestoreFurniture-StoredToInventory")}"))
                    EnqueueRestore(isOutdoor ? 27000U : 27001U, isOutdoor ? 27000U : 27008U, !isOutdoor);
            }
        }
    }

    private bool EnqueueRestore(uint startInventory, uint endInventory, bool isIndoor, int extraSlotParam = 0)
    {
        var houseManager     = HousingManager.Instance();
        var inventoryManager = InventoryManager.Instance();

        if (houseManager     == null ||
            inventoryManager == null ||
            houseManager->GetCurrentIndoorHouseId() <= 0 &&
            houseManager->GetCurrentPlot()          < 0)
        {
            TaskHelper.Abort();
            return true;
        }

        var param1 = isIndoor
                         ? *(long*)((nint)houseManager->IndoorTerritory  + 38560) >> 32
                         : *(long*)((nint)houseManager->OutdoorTerritory + 38560) >> 32;
        var param2 = isIndoor ? houseManager->IndoorTerritory->HouseId : houseManager->OutdoorTerritory->HouseId;

        for (var i = startInventory; i <= endInventory; i++)
        {
            var type       = (InventoryType)i;
            var contaniner = inventoryManager->GetInventoryContainer(type);
            if (contaniner == null) continue;

            for (var d = 0; d < contaniner->Size; d++)
            {
                var slot = contaniner->GetInventorySlot(d);
                if (slot == null || slot->ItemId == 0) continue;

                var inventoryTypeFinal = (int)i;
                var slotFinal          = d;

                TaskHelper.Enqueue
                (() => ExecuteCommandManager.Instance().ExecuteCommand
                 (
                     ExecuteCommandFlag.RestoreFurniture,
                     (uint)param1,
                     (uint)param2,
                     (uint)inventoryTypeFinal,
                     (uint)(slotFinal + extraSlotParam)
                 )
                );
                TaskHelper.Enqueue(() => EnqueueRestore(startInventory, endInventory, isIndoor, extraSlotParam));
                return true;
            }
        }

        TaskHelper.Abort();
        return true;
    }

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        if (!HousingGoods->IsAddonAndNodesReady()) return;

        Overlay.IsOpen = type switch
        {
            AddonEvent.PostSetup   => true,
            AddonEvent.PreFinalize => true,
            _                      => Overlay.IsOpen
        };

        if (type == AddonEvent.PreFinalize)
            TaskHelper.Abort();
    }
}
