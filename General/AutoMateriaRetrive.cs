using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DailyRoutines.Modules;

public unsafe class AutoMateriaRetrive : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("AutoMateriaRetriveTitle"),
        Description = GetLoc("AutoMateriaRetriveDescription"),
        Category = ModuleCategories.General,
    };

    private static readonly CompSig RetriveMateriaSig = new("E8 ?? ?? ?? ?? 88 87 08 01 00 00");
    private delegate bool RetriveMateriaDelegate(
        EventFramework* framework, int eventID, InventoryType inventoryType, short inventorySlot, int extraParam);
    private static Hook<RetriveMateriaDelegate>? RetriveMateriaHook;

    private static readonly InventoryType[] Inventories =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
        InventoryType.EquippedItems, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead, InventoryType.ArmoryHands,
        InventoryType.ArmoryWaist, InventoryType.ArmoryLegs, InventoryType.ArmoryFeets, InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings, InventoryType.ArmoryMainHand
    ];

    private static Dictionary<string, Item>? ItemNames;
    private static Dictionary<string, Item> ItemNamesAnother = [];

    private static string ItemSearchInput = string.Empty;
    private static Item? SelectedItem;

    protected override void Init()
    {
        ItemNames ??= LuminaGetter.Get<Item>()
                                 .Where(x => x.MateriaSlotCount > 0 && !string.IsNullOrEmpty(x.Name.ExtractText()))
                                 .GroupBy(x => x.Name.ExtractText())
                                 .ToDictionary(x => x.Key, x => x.First());

        ItemNamesAnother = ItemNames.Take(10).ToDictionary(x => x.Key, x => x.Value);

        RetriveMateriaHook ??= 
            DService.Hook.HookFromSignature<RetriveMateriaDelegate>(RetriveMateriaSig.Get(), RetriveMateriaDetour);
        RetriveMateriaHook.Enable();
        TaskHelper ??= new() { TimeLimitMS = 5_000 };
    }

    protected override void ConfigUI()
    {
        ConflictKeyText();

        ImGui.Spacing();

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, $"{Lang.Get("AutoMateriaRetrive-ManuallySelect")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(300f * GlobalFontScale);
        if (ImGui.BeginCombo("###ItemSelectCombo",
                             SelectedItem == null ? string.Empty : SelectedItem.Value.Name.ExtractText(),
                             ImGuiComboFlags.HeightLargest))
        {
            ImGui.InputTextWithHint("###GameItemSearchInput", Lang.Get("PleaseSearch"), ref ItemSearchInput, 128);
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                if (!string.IsNullOrWhiteSpace(ItemSearchInput))
                {
                    ItemNamesAnother = ItemNames
                                 .Where(x => x.Key.Contains(ItemSearchInput, StringComparison.OrdinalIgnoreCase))
                                 .OrderBy(x => !x.Key.StartsWith(ItemSearchInput))
                                 .Take(100)
                                 .ToDictionary(x => x.Key, x => x.Value);
                }
            }

            ImGui.Separator();
            foreach (var (itemName, item) in ItemNamesAnother)
                if (ImGuiOm.SelectableImageWithText(ImageHelper.GetGameIcon(item.Icon).ImGuiHandle,
                                                    ScaledVector2(24f), itemName,
                                                    (SelectedItem?.RowId ?? 0) == item.RowId,
                                                    ImGuiSelectableFlags.DontClosePopups))
                    SelectedItem = (SelectedItem?.RowId ?? 0) == item.RowId ? null : item;
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        using (ImRaii.Disabled((SelectedItem?.RowId ?? 0) == 0))
        {
            if (ImGui.Button(Lang.Get("Start")))
            {
                TaskHelper.Abort();
                EnqueueRetriveTaskByItemID(SelectedItem?.RowId ?? 0);
            }
        }
    }

    private void EnqueueRetriveTaskByItemID(uint itemID)
    {
        TaskHelper.Abort();

        TaskHelper.Enqueue(() =>
        {
            if (InterruptByConflictKey(TaskHelper, this))
            {
                TaskHelper.Abort();
                return;
            }

            var instance = InventoryManager.Instance();
            foreach (var inventoryType in Inventories)
            {
                var container = instance->GetInventoryContainer(inventoryType);
                if (container == null || !container->IsLoaded) continue;
                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || slot->ItemId == 0 || slot->ItemId != itemID || slot->Materia.ToArray().All(x => x == 0)) continue;
                    EnqueueRetriveTask(inventoryType, (short)i);
                    return;
                }
            }

            TaskHelper.Abort();
            NotificationWarning(Lang.Get("AutoMateriaRetrive-NoItemFound"));
        });

        TaskHelper.Enqueue(() =>
        {
            if (InterruptByConflictKey(TaskHelper, this))
            {
                TaskHelper.Abort();
                return;
            }

            EnqueueRetriveTaskByItemID(itemID);
        });
    }

    private void EnqueueRetriveTask(InventoryType inventoryType, short inventorySlot)
    {
        TaskHelper.Abort();

        TaskHelper.Enqueue(() =>
        {
            if (InterruptByConflictKey(TaskHelper, this))
            {
                TaskHelper.Abort();
                return true;
            }
            return !OccupiedInEvent;
        }, "WaitEventEndBefore", null, null, 1);

        TaskHelper.Enqueue(() =>
        {
            if (InterruptByConflictKey(TaskHelper, this) || 
                IsInventoryFull([InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4]))
            {
                TaskHelper.Abort();
                return;
            }

            Retrive(inventoryType, inventorySlot);
        }, "RetriveWork", null, null, 1);

        TaskHelper.Enqueue(() =>
        {
            if (InterruptByConflictKey(TaskHelper, this))
            {
                TaskHelper.Abort();
                return true;
            }
            return !OccupiedInEvent;
        }, "WaitEventEndAfter", null, null, 1);

        TaskHelper.Enqueue(() =>
        {
            if (InterruptByConflictKey(TaskHelper, this))
            {
                TaskHelper.Abort();
                return;
            }

            var manager = InventoryManager.Instance();
            var slot = manager->GetInventorySlot(inventoryType, inventorySlot);
            if (slot == null || slot->ItemId == 0 || slot->Materia.ToArray().All(x => x == 0)) return;
            EnqueueRetriveTask(inventoryType, inventorySlot);
        }, "EnqueueNewRound_SingleSlot", null, null, 1);
    }

    private static void Retrive(InventoryType type, short slot)
    {
        var instance = EventFramework.Instance();
        const int eventID = 0x390001;

        RetriveMateriaHook.Original(instance, eventID, type, slot, 0);
    }

    private bool RetriveMateriaDetour(EventFramework* framework, int eventID, InventoryType inventoryType, short inventorySlot, int extraParam)
    {
        var original = RetriveMateriaHook.Original(framework, eventID, inventoryType, inventorySlot, extraParam);
        if (eventID == 0x390001 && original && !TaskHelper.IsBusy)
            EnqueueRetriveTask(inventoryType, inventorySlot);

        return original;
    }
}
