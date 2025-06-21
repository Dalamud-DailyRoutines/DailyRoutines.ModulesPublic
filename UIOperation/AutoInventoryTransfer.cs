using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Gui.ContextMenu;
using FFXIVClientStructs.FFXIV.Component.GUI;

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


    
    // 菜单文本
    private readonly string[] entrustTexts = [
        "交给雇员保管", "从雇员处取回", "放入陆行鸟鞍囊", "从陆行鸟鞍囊中取回", 
        "Entrust to Retainer", "Retrieve from Retainer", "Place in Saddle Bag", "Retrieve from Saddle Bag",
        "リテイナーに預ける", "リテイナーから取り出す", "チョコボ鞍袋に入れる", "チョコボ鞍袋から取り出す",
        "집사에게 맡기기", "집사에게서 찾기", "초코보 안장에 넣기", "초코보 안장에서 찾기"
    ];

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

        bool IsInventoryOpen()
            => IsAddonAndNodesReady(Inventory)           ||
               IsAddonAndNodesReady(InventoryLarge)      ||
               IsAddonAndNodesReady(InventoryExpansion)  ||
               IsAddonAndNodesReady(InventoryRetainer)   ||
               IsAddonAndNodesReady(InventoryRetainerLarge);

    }

    private void HandleTransfer()
    {
        TaskHelper.Abort();
        TaskHelper.Enqueue(() => 
        {
            if (!IsAddonAndNodesReady(InfosOm.ContextMenu)) return false;
            foreach (var text in entrustTexts)
            {
                if (ClickContextMenu(text))
                    return true;
            }
            return true;
        }, "点击相关菜单项");
    }

    public override void Uninit()
    {
        DService.ContextMenu.OnMenuOpened -= OnContextMenuOpened;
        base.Uninit();
    }
}
