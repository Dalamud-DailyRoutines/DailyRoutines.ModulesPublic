using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Gui.ContextMenu;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Collections.Generic;
using static FFXIVClientStructs.STD.Helper.IStaticEncoding;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoInventoryTransfer : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("AutoInventoryTransfer"),
        Description = GetLoc("AutoInventoryTransferDescription"),
        Category = ModuleCategories.UIOperation,
        Author = ["Yangdoubao"]
    };

    private readonly string[] entrustTexts;
    public AutoInventoryTransfer()
    {
        entrustTexts = GetMenuItems();
    }
    private string[] GetMenuItems()
    {

        var addonIds = new uint[]
        {
            97, 98, 881, 887
        };
        var menuTexts = new List<string>();
        foreach (var id in addonIds)
        {
            var text = LuminaWrapper.GetAddonText(id);
            if (!string.IsNullOrEmpty(text))
            {
                menuTexts.Add(text);
            }
        }
        return [.. menuTexts];
    }

    public override void Init()
    {
        TaskHelper ??= new() { TimeLimitMS = 2_000 };
        DService.ContextMenu.OnMenuOpened += OnContextMenuOpened;
    }

    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        if (!IsConflictKeyPressed()) return;
        if (IsInventoryOpen())
        {
            HandleTransfer();
            return;
        }
        return;

        bool IsInventoryOpen()
            => IsAddonAndNodesReady(Inventory) ||
               IsAddonAndNodesReady(InventoryLarge) ||
               IsAddonAndNodesReady(InventoryExpansion) ||
               IsAddonAndNodesReady(InventoryRetainer) ||
               IsAddonAndNodesReady(InventoryRetainerLarge);

    }

    private bool HandleTransfer()
    {
        if (!IsAddonAndNodesReady(InfosOm.ContextMenu))
        {
            foreach (var text in entrustTexts)
            {
                if (ClickContextMenu(text))
                    return true;
            }
        }
        else
        {
            TaskHelper.DelayNext(10);
            TaskHelper.Enqueue(() =>
            {
                HandleTransfer();
            });
        }
            

        return true;
    }

    public override void Uninit()
    {
        DService.ContextMenu.OnMenuOpened -= OnContextMenuOpened;
        base.Uninit();
    }
}
