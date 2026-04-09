using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Internal;
using Dalamud.Game.Gui.ContextMenu;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoInventoryTransfer : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoInventoryTransferTitle"),
        Description = Lang.Get("AutoInventoryTransferDescription"),
        Category    = ModuleCategory.UIOperation,
        Author      = ["Yangdoubao"]
    };

    protected override void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 2_000 };

        DService.Instance().ContextMenu.OnMenuOpened += OnContextMenuOpened;
    }
    
    protected override void Uninit() =>
        DService.Instance().ContextMenu.OnMenuOpened -= OnContextMenuOpened;

    protected override void ConfigUI() => ImGuiOm.ConflictKeyText();

    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        if (!PluginConfig.Instance().ConflictKeyBinding.IsPressed() || !IsInventoryOpen()) return;

        TaskHelper.Enqueue(() => ContextMenuAddon->IsAddonAndNodesReady());
        TaskHelper.Enqueue(() => { AddonContextMenuEvent.Select(MenuTexts); });

        return;

        bool IsInventoryOpen()
        {
            return Inventory->IsAddonAndNodesReady()          ||
                   InventoryLarge->IsAddonAndNodesReady()     ||
                   InventoryExpansion->IsAddonAndNodesReady() ||
                   InventoryRetainer->IsAddonAndNodesReady()  ||
                   InventoryRetainerLarge->IsAddonAndNodesReady();
        }
    }
    
    #region 常量

    private static readonly List<string> MenuTexts =
    [
        LuminaWrapper.GetAddonText(97),
        LuminaWrapper.GetAddonText(98),
        LuminaWrapper.GetAddonText(881),
        LuminaWrapper.GetAddonText(887)
    ];

    #endregion
}
