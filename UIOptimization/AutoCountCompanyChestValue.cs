using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Windows;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public class AutoCountCompanyChestValue : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("统计部队箱中的沉船首饰"),
        Description = GetLoc("计算部队箱中的沉船首饰的数量和价值"),
        Category = ModuleCategories.UIOptimization,
        Author = ["采购"]
    };
    
    public override void Init()
    {
        TaskHelper ??= new() { TimeLimitMS = 5_000 };
        Overlay    ??= new Overlay(this);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "FreeCompanyChest", CheckFcChestAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "FreeCompanyChest", CheckFcChestAddon);
    }
    
    public override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(CheckFcChestAddon);
        base.Uninit();
    }
    
    private void CheckFcChestAddon(AddonEvent type, AddonArgs? args)
    {
        Overlay.IsOpen = type switch
        {
            AddonEvent.PostSetup   => true,
            AddonEvent.PreFinalize => false,
            _                      => Overlay.IsOpen
        };
        if (type == AddonEvent.PreFinalize)
            TaskHelper.Abort();
    }
    
    public override unsafe void OverlayUI()
    {   
        var checkCompanyChestUi = OmenTools.Helpers.HelpersOm.TryGetAddonByName("FreeCompanyChest",out AtkUnitBase* addon);
        if (!checkCompanyChestUi) return;
        ImGui.AlignTextToFramePadding();
        ImGui.SetWindowPos(new Vector2(addon->GetX() ,addon->GetY()- ImGui.GetWindowSize().Y));
        ImGui.Text($"");
        ImGui.SameLine();
        if (ImGui.Button("点我统计在聊天框",new (ImGui.GetItemRectSize().X, ImGui.GetItemRectSize().Y+15)))
            Check(addon);
    }
    
    // 检查部队箱中的沉船首饰数量和价值
    private static unsafe void Check(AtkUnitBase* addon)
    {
        var fcPage = GetCurrentFcPage(addon);
        var manager = InventoryManager.Instance();
        var itemIds = new uint[] { 22500, 22501, 22502, 22503, 22504, 22505, 22506, 22507 };
        var totalPrice = 0;
        if (manager == null) return;
        foreach (var item in itemIds)
        {
            var itemInfo = LuminaGetter.GetRow<Item>(item);
            if (itemInfo == null) continue;
            var itemCount = manager->GetItemCountInContainer(item, fcPage);
            var price = itemInfo.Value.PriceLow;
            if (itemCount == 0) continue;
            DService.Chat.Print("物品" + itemInfo.Value.Name.ToString() + "的数量为:" + itemCount + " 单个物品价值为:" + price +
                                " 单个物品总价值为:" + (itemCount * price)+ " Gil");
            totalPrice += (int)(itemCount * price);
        }
        DService.Chat.Print(totalPrice > 0
                                ? $"部队箱中沉船首饰的总价值为: {totalPrice} Gil"
                                : "部队箱中没有沉船首饰");
    }

    //获取当前部队箱的InventoryType
    private static unsafe InventoryType GetCurrentFcPage(AtkUnitBase* addon) => 
        addon == null ? InventoryType.FreeCompanyPage1 : (InventoryType)(20000 + addon->AtkValues[2].UInt);
}
