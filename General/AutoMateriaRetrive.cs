using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using Lumina.Excel.Sheets;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoMateriaRetrive : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoMateriaRetriveTitle"),
        Description = Lang.Get("AutoMateriaRetriveDescription"),
        Category    = ModuleCategory.General
    };
    
    private Hook<EventFramework.Delegates.MaterializeItem>? RetriveMateriaHook;

    private readonly ItemSelectCombo itemSelectCombo = new
    (
        "Item",
        LuminaGetter.Get<Item>()
                    .Where(x => x.MateriaSlotCount > 0 && !string.IsNullOrEmpty(x.Name.ToString()))
                    .GroupBy(x => x.Name.ToString())
                    .Select(x => x.First())
                    .ToList()
    );

    protected override void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 5_000 };
        
        RetriveMateriaHook = DService.Instance().Hook.HookFromMemberFunction
        (
            typeof(EventFramework.MemberFunctionPointers),
            "MaterializeItem",
            (EventFramework.Delegates.MaterializeItem)RetriveMateriaDetour
        );
        RetriveMateriaHook.Enable();
    }

    protected override void ConfigUI()
    {
        ImGuiOm.ConflictKeyText();

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoMateriaRetrive-ManuallySelect")}");

        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(300f * GlobalUIScale);
            itemSelectCombo.DrawRadio();

            ImGui.SameLine();

            using (ImRaii.Disabled(itemSelectCombo.SelectedID == 0))
            {
                if (ImGui.Button(Lang.Get("Start")))
                {
                    TaskHelper.Abort();
                    EnqueueRetriveTaskByItemID(itemSelectCombo.SelectedID);
                }
            }
        }
    }

    private void EnqueueRetriveTaskByItemID(uint itemID)
    {
        TaskHelper.Abort();

        TaskHelper.Enqueue
        (() =>
            {
                if (TaskHelper.AbortByConflictKey(this))
                {
                    TaskHelper.Abort();
                    return;
                }

                var instance = InventoryManager.Instance();

                foreach (var inventoryType in Inventories.PlayerWithArmory)
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
                NotifyHelper.Instance().NotificationWarning(Lang.Get("AutoMateriaRetrive-NoItemFound"));
            }
        );

        TaskHelper.Enqueue
        (() =>
            {
                if (TaskHelper.AbortByConflictKey(this))
                {
                    TaskHelper.Abort();
                    return;
                }

                EnqueueRetriveTaskByItemID(itemID);
            }
        );
    }

    private void EnqueueRetriveTask(InventoryType inventoryType, short inventorySlot)
    {
        TaskHelper.Abort();

        TaskHelper.Enqueue
        (
            () =>
            {
                if (TaskHelper.AbortByConflictKey(this))
                {
                    TaskHelper.Abort();
                    return true;
                }

                return !DService.Instance().Condition.IsOccupiedInEvent;
            },
            "WaitEventEndBefore",
            weight: 1
        );

        TaskHelper.Enqueue
        (
            () =>
            {
                if (TaskHelper.AbortByConflictKey(this) || Inventories.Player.IsFull())
                {
                    TaskHelper.Abort();
                    return;
                }

                Retrive(inventoryType, inventorySlot);
            },
            "RetriveWork",
            weight: 1
        );

        TaskHelper.Enqueue
        (
            () =>
            {
                if (TaskHelper.AbortByConflictKey(this))
                {
                    TaskHelper.Abort();
                    return true;
                }

                return !DService.Instance().Condition.IsOccupiedInEvent;
            },
            "WaitEventEndAfter",
            weight: 1
        );

        TaskHelper.Enqueue
        (
            () =>
            {
                if (TaskHelper.AbortByConflictKey(this))
                {
                    TaskHelper.Abort();
                    return;
                }

                var manager = InventoryManager.Instance();
                var slot    = manager->GetInventorySlot(inventoryType, inventorySlot);
                if (slot == null || slot->ItemId == 0 || slot->Materia.ToArray().All(x => x == 0)) return;
                EnqueueRetriveTask(inventoryType, inventorySlot);
            },
            "EnqueueNewRound_SingleSlot",
            weight: 1
        );
    }

    private void Retrive(InventoryType type, short slot)
    {
        const uint EVENT_ID = 0x390001;
        RetriveMateriaHook.Original(EventFramework.Instance(), EVENT_ID, type, slot, 0);
    }

    private void RetriveMateriaDetour(EventFramework* framework, EventId eventID, InventoryType inventoryType, short inventorySlot, int extraParam)
    {
        RetriveMateriaHook.Original(framework, eventID, inventoryType, inventorySlot, extraParam);
        if (eventID == 0x390001 && !TaskHelper.IsBusy)
            EnqueueRetriveTask(inventoryType, inventorySlot);
    }
}
