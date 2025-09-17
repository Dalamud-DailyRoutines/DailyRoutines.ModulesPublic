using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoUseCrafterGathererManual : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoUseCrafterGathererManualTitle"),
        Description = GetLoc("AutoUseCrafterGathererManualDescription"),
        Category    = ModuleCategories.General,
        Author      = ["Shiyuvi", "AtmoOmen"]
    };

    private static readonly HashSet<uint> Gatherers =
        LuminaGetter.Get<ClassJob>()
                    .Where(x => x.ClassJobCategory.RowId == 32)
                    .Select(x => x.RowId)
                    .ToHashSet();

    private static readonly HashSet<uint> Crafters =
        LuminaGetter.Get<ClassJob>()
                    .Where(x => x.ClassJobCategory.RowId == 33)
                    .Select(x => x.RowId)
                    .ToHashSet();

    private static readonly uint[] GathererManuals = [26553, 12668, 4635, 4633];
    private static readonly uint[] CrafterManuals  = [26554, 12667, 4634, 4632];

    private static readonly HashSet<ConditionFlag> ValidConditions =
    [
        ConditionFlag.Crafting, ConditionFlag.Gathering, ConditionFlag.Mounted,
    ];

    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        TaskHelper ??= new() { TimeLimitMS = 15_000 };

        DService.Condition.ConditionChange    += OnConditionChanged;
        DService.ClientState.TerritoryChanged += OnZoneChanged;
        DService.ClientState.ClassJobChanged  += OnClassJobChanged;
        DService.ClientState.LevelChanged     += OnLevelChanged;
        
        EnqueueCheck();
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("SendNotification"), ref ModuleConfig.SendNotification))
            SaveConfig(ModuleConfig);
    }

    protected override void Uninit()
    {
        DService.Condition.ConditionChange    -= OnConditionChanged;
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        DService.ClientState.ClassJobChanged  -= OnClassJobChanged;
        DService.ClientState.LevelChanged     -= OnLevelChanged;
    }

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (value || !ValidConditions.Contains(flag)) return;
        EnqueueCheck();
    }
    
    private void OnZoneChanged(ushort zone) => 
        EnqueueCheck();
    
    private void OnLevelChanged(uint classJobID, uint level) => 
        EnqueueCheck();

    private void OnClassJobChanged(uint classJobID) => 
        EnqueueCheck();
    
    private void EnqueueCheck()
    {
        TaskHelper.Abort();
        TaskHelper.Enqueue(() =>
        {
            if (DService.ObjectTable.LocalPlayer is not { } localPlayer) return false;
            if (localPlayer.Level >= PlayerState.Instance()->MaxLevel) return true;
            if (BetweenAreas || OccupiedInEvent || IsCasting || !IsScreenReady() ||
                ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, 2) != 0)
                return false;

            var isGatherer = Gatherers.Contains(localPlayer.ClassJob.RowId);
            var isCrafter  = Crafters.Contains(localPlayer.ClassJob.RowId);
            if (!isGatherer && !isCrafter) return true;

            var statusManager = localPlayer.ToStruct()->StatusManager;
            var statusIndex   = statusManager.GetStatusIndex(isGatherer ? 46U : 45U);
            if (statusIndex != -1) return true;

            var itemID = 0U;
            if (isGatherer && TryGetFirstValidItem(GathererManuals, out var gathererManual))
                itemID = gathererManual;
            if (isCrafter && TryGetFirstValidItem(CrafterManuals, out var crafterManual))
                itemID = crafterManual;
            if (itemID == 0 || !LuminaGetter.TryGetRow<Item>(itemID, out var itemRow)) return true;
            
            UseActionManager.UseActionLocation(ActionType.Item, itemID, 0xE0000000, default, 0xFFFF);
            if (ModuleConfig.SendNotification)
                NotificationInfo(GetLoc("AutoUseCrafterGathererManual-Notification", itemRow.Name.ExtractText()));
            return true;
        });
    }

    private static bool TryGetFirstValidItem(IEnumerable<uint> items, out uint itemID)
    {
        itemID = 0;
        
        var manager = InventoryManager.Instance();
        if (manager == null) return false;

        foreach (var item in items)
        {
            var count = manager->GetInventoryItemCount(item) + manager->GetInventoryItemCount(item, true);
            if (count == 0) continue;
            
            itemID = item;
            return true;
        }
        
        return false;
    }

    private class Config : ModuleConfiguration
    {
        public bool SendNotification = true;
    }
}
