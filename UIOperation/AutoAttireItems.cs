using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

// TODO: 合并成单一投影台模块
public unsafe class AutoAttireItems : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoAttireItemsTitle"),
        Description = GetLoc("AutoAttireItemsDescription"),
        Category    = ModuleCategories.UIOperation
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static readonly List<MirageItemSet> MirageItemSets =
        LuminaGetter.Get<MirageStoreSetItem>()
                    .Select(MirageItemSet.Parse)
                    .Where(x => x != null)
                    .OfType<MirageItemSet>()
                    .ToList();

    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        TaskHelper   ??= new() { TimeLimitMS = 5_000 };
        ModuleConfig =   LoadConfig<Config>() ?? new();

        LogMessageManager.Register(OnReceiveLogMessage);
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "MiragePrismPrismSetConvert", OnAddonMiragePrismPrismSetConvert);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "MiragePrismPrismSetConvert", OnAddonMiragePrismPrismSetConvert);
        if (IsAddonAndNodesReady(MiragePrismPrismSetConvert)) 
            OnAddonMiragePrismPrismSetConvert(AddonEvent.PostRefresh, null);
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "MiragePrismPrismSetConvertC", OnAddonMiragePrismPrismSetConvertC);
        if (IsAddonAndNodesReady(MiragePrismPrismSetConvertC)) 
            OnAddonMiragePrismPrismSetConvertC(AddonEvent.PostSetup, null);
    }

    protected override void ConfigUI()
    {
        using (ImRaii.Disabled(TaskHelper.IsBusy || !IsAddonAndNodesReady(MiragePrismPrismBox)))
        {
            if (ImGui.Button(GetLoc("Start")))
                RestoreItemsFromPrismBox();
        }
        
        ImGui.SameLine();
        if (ImGui.Button(GetLoc("Stop")))
            TaskHelper.Abort();
        
        ImGui.Spacing();
        
        if (ImGui.Checkbox(GetLoc("AutoAttireItems-OnlyTakeOutCompleteSet"), ref ModuleConfig.OnlyRemoveWhenComplete))
            SaveConfig(ModuleConfig);
        
        ImGui.SameLine();
        if (ImGui.Checkbox(GetLoc("AutoAttireItems-SkipWhenDyed"), ref ModuleConfig.SkipItemsWithStain))
            SaveConfig(ModuleConfig);
    }

    protected override void Uninit()
    {
        LogMessageManager.Unregister(OnReceiveLogMessage);
        
        DService.AddonLifecycle.UnregisterListener(OnAddonMiragePrismPrismSetConvert);
        DService.AddonLifecycle.UnregisterListener(OnAddonMiragePrismPrismSetConvertC);
    }

    private void OnReceiveLogMessage(uint logMessageID)
    {
        if (logMessageID != 4280) return;
        TaskHelper.Abort();
    }

    private void RestoreItemsFromPrismBox()
    {
        Dictionary<MirageItemSet, List<int>> validItemSets = [];

        foreach (var mirageItemSet in MirageItemSets)
        {
            List<int> itemIndexes = [];

            if (ModuleConfig.OnlyRemoveWhenComplete)
            {
                if (!mirageItemSet.IsAbleToRemoveAll(ModuleConfig.SkipItemsWithStain, out itemIndexes)) continue;
            }
            else
            {
                if (!mirageItemSet.IsAbleToRemoveAny(ModuleConfig.SkipItemsWithStain, out itemIndexes)) continue;
            }
            
            validItemSets[mirageItemSet] = itemIndexes;
        }
        if (validItemSets.Count == 0) return;

        validItemSets.ForEach(x =>
        {
            x.Value.ForEach(index =>
            {
                TaskHelper.Enqueue(() =>
                {
                    if (!IsInventoryFull([
                            InventoryType.Inventory1, InventoryType.Inventory2,
                            InventoryType.Inventory3, InventoryType.Inventory4
                        ]))
                        return;
                    TaskHelper.Abort();
                });
                TaskHelper.Enqueue(() => MirageManager.Instance()->RestorePrismBoxItem((uint)index));
            });
            TaskHelper.Enqueue(() => Chat(GetSLoc("AutoAttireItems-Message", new ItemPayload(x.Key.Set.ID), x.Value.Count, x.Key.SetItems.Count)));
        });
        TaskHelper.Enqueue(RestoreItemsFromPrismBox);
    }

    private void OnAddonMiragePrismPrismSetConvert(AddonEvent type, AddonArgs? args)
    {
        if (MiragePrismPrismSetConvert == null) return;
        FillMiragePrismBoxSet();
    }
    
    private static void OnAddonMiragePrismPrismSetConvertC(AddonEvent type, AddonArgs? args)
    {
        if (MiragePrismPrismSetConvertC == null) return;
        Callback(MiragePrismPrismSetConvertC, true, 0);
    }

    private void FillMiragePrismBoxSet()
    {
        if (!IsAddonAndNodesReady(MiragePrismPrismSetConvert) || TaskHelper.IsBusy) return;
        
        var slotCount = MiragePrismPrismSetConvert->AtkValues[20].UInt;
        if (slotCount == 0) return;

        List<int> slotsToFill = [];
        for (var i = 0; i < slotCount; i++)
        {
            var inventoryType = MiragePrismPrismSetConvert->AtkValues[25 + (i * 7)].UInt;
            if (inventoryType != 9999) continue;
            
            var unkParam0 = MiragePrismPrismSetConvert->AtkValues[26 + (i * 7)].UInt;
            var unkParam1 = MiragePrismPrismSetConvert->AtkValues[27 + (i * 7)].UInt;
            if (unkParam0 == unkParam1 && unkParam1 == 0) continue;
            
            slotsToFill.Add(i);
        }
        if (slotsToFill.Count == 0) return;

        foreach (var i in slotsToFill)
        {
            var index = i;
            TaskHelper.Enqueue(() =>
            {
                if (!IsAddonAndNodesReady(ContextIconMenu))
                {
                    Callback(MiragePrismPrismSetConvert, true, 13, index);
                    return false;
                }
                else
                {
                    Callback(ContextIconMenu, true, 0, 0, 1021003u, 0u, 0);
                    return true;
                }
            });
        }
        
        TaskHelper.DelayNext(100);
        TaskHelper.Enqueue(() => SendEvent(AgentId.MiragePrismPrismSetConvert, 1, 14));
    }
    

    private class Config : ModuleConfiguration
    {
        public bool SkipItemsWithStain     = true;
        public bool OnlyRemoveWhenComplete = true;
    }

    private record MirageItemSet((uint ID, string Name) Set, List<(uint ID, string Name)> SetItems)
    {
        public static MirageItemSet? Parse(MirageStoreSetItem row)
        {
            if (!LuminaGetter.TryGetRow<Item>(row.RowId, out var setItemRow)) return null;
            
            var setName = setItemRow.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(setName)) return null;

            List<uint> setItemsID =
            [
                row.Body.RowId, row.Bracelets.RowId, row.Earrings.RowId, row.Feet.RowId, row.Hands.RowId,
                row.Head.RowId, row.Legs.RowId, row.Necklace.RowId, row.Ring.RowId, row.MainHand.RowId, row.OffHand.RowId,
            ];

            var filitered = setItemsID
                            .Where(x => x > 1 && LuminaGetter.TryGetRow<Item>(x, out _))
                            .Select(x => (x, LuminaGetter.GetRow<Item>(x)!.Value.Name.ExtractText()))
                            .ToList();
            if (filitered.Count == 0) return null;
            
            return new(new(row.RowId, setName), filitered);
        }

        public static bool IsInMirageStore(uint itemID, out int index, out uint stain0ID, out uint stain1ID)
        {
            index    = -1;
            stain0ID = stain1ID = 0;
            
            itemID = itemID % 100_0000;
            
            var instance = MirageManager.Instance();
            if (instance == null) return false;
            if (!instance->PrismBoxRequested || !instance->PrismBoxLoaded) return false;

            for (var i = 0; i < instance->PrismBoxItemIds.Length; i++)
            {
                var itemIDRaw = instance->PrismBoxItemIds[i];
                if (itemIDRaw == 0) continue;

                var itemIDMirage = itemIDRaw % 100_0000;
                if (itemIDMirage != itemID) continue;

                stain0ID = instance->PrismBoxStain0Ids[i];
                stain1ID = instance->PrismBoxStain1Ids[i];
                index = i;
                return true;
            }

            return false;
        }

        public bool IsAbleToRemoveAll(bool skipWhenStained, out List<int> itemIndexes)
        {
            itemIndexes = [];

            foreach (var x in SetItems)
            {
                if (!IsInMirageStore(x.ID, out var index, out var stain0ID, out var stain1ID)) return false;
                if (skipWhenStained && (stain0ID != 0 || stain1ID != 0)) return false;
                
                itemIndexes.Add(index);
            }
            
            return true;
        }

        public bool IsAbleToRemoveAny(bool skipWhenStained, out List<int> itemIndexes)
        {
            itemIndexes = [];
            
            foreach (var x in SetItems)
            {
                if (!IsInMirageStore(x.ID, out var index, out var stain0ID, out var stain1ID)) continue;
                if (skipWhenStained && (stain0ID != 0 || stain1ID != 0)) continue;
                
                itemIndexes.Add(index);
            }
            
            return itemIndexes.Count > 0;
        }
    }
}
