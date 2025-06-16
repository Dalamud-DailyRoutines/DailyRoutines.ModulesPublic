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
        Title = "物品快速转移",
        Description = "按住打断热键并右键点击物品时，自动转移物品",
        Category = ModuleCategories.UIOperation,
        Author = ["Yangdoubao"]
    };


    
    // 菜单文本
    private readonly string[] entrustTexts = ["交给雇员保管", "从雇员处取回", "放入陆行鸟鞍囊", "从陆行鸟鞍囊中取回"];

    public override void Init()
    {
        TaskHelper ??= new() { TimeLimitMS = 2_000 };
        
        // 注册右键菜单打开事件
        DService.ContextMenu.OnMenuOpened += OnContextMenuOpened;
    }

    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        // 检查打断热键是否按下
        if (!IsConflictKeyPressed()) return;

        // 检查是否是物品菜单
        if (!IsValidInventoryMenu(args.AddonName)) return;

        // 处理陆行鸟鞍囊转移 (需要鞍囊窗口和物品栏窗口都打开)
        if (IsInventoryOpen())
        {
            HandleTransfer();
            return;
        }
    }


    private void HandleTransfer()
    {
        TaskHelper.Abort();
        TaskHelper.Enqueue(() => 
        {
            // 确保上下文菜单已打开
            if (!IsAddonAndNodesReady(InfosOm.ContextMenu)) return false;

            // 尝试查找并点击相关菜单项
            foreach (var text in entrustTexts)
            {
                if (ClickContextMenu(text))
                    return true;
            }
            return true;
        }, "点击相关菜单项");
    }



    private bool IsValidInventoryMenu(string? addonName)
    {
        if (addonName == null) return false;
        
        // 检查是否是物品相关的菜单
        return addonName.StartsWith("Inventory") || 
               addonName == "InventoryGrid" ||
               addonName == "InventoryLarge" ||
               addonName == "SaddleBagGrid" ||
               addonName == "SaddleBag" ||
               addonName == "RetainerGrid" ||
               addonName == "RetainerInventoryLarge";
    }



    private bool IsInventoryOpen()
    {
        // 检查是否有玩家物品栏窗口打开
        return TryGetAddonByName<AtkUnitBase>("Inventory", out _) ||
               TryGetAddonByName<AtkUnitBase>("InventoryGrid", out _) ||
               TryGetAddonByName<AtkUnitBase>("InventoryLarge", out _);
    }



    public override void Uninit()
    {
        // 取消注册右键菜单事件
        DService.ContextMenu.OnMenuOpened -= OnContextMenuOpened;
        
        TaskHelper.Abort();
        base.Uninit();
    }
}
