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
    
    protected override void Init()
    {
        TaskHelper ??= new() { TimeLimitMS = 5_000 };
        Overlay    ??= new Overlay(this);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "FreeCompanyChest", CheckFcChestAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "FreeCompanyChest", CheckFcChestAddon);
    }
    
    protected override void Uninit()
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
        if (!TryGetAddonByName("FreeCompanyChest", out var addon)) return;
        ImGui.AlignTextToFramePadding();
        ImGui.SetWindowPos(new Vector2(addon->GetNodeById(108)->ScreenX, addon->GetY() - ImGui.GetWindowSize().Y));
        ImGui.Text("部队箱沉船首饰统计");
        ImGui.SameLine();
        if (ImGui.Button("点我"))
            Check(addon);
    }
    //需要统计的六种首饰id
    private static readonly uint[] ItemIds =  [ 22500, 22501, 22502, 22503, 22504, 22505, 22506, 22507 ];
    // 检查部队箱中的沉船首饰数量和价值
    private static unsafe void Check(AtkUnitBase* addon)
    {
        var fcPage = GetCurrentFcPage(addon);
        var manager = InventoryManager.Instance();
        if (manager == null) return;
        var totalPrice = 0;
        foreach (var item in ItemIds)
        {
            var itemInfo = LuminaGetter.TryGetRow(item, out Item itemData) ;
            if (itemInfo == false) continue;
            if (manager->GetItemCountInContainer == null)
            {
                Chat("部队箱容器加载失败,请重试");
                continue;
            }
            var itemCount = manager->GetItemCountInContainer(item, fcPage);
            if (itemCount == 0) continue;
            var price = itemData.PriceLow;
            Chat("物品" + itemData.Name.ToString() + "的数量为:" + itemCount + " 单个物品价值为:" + price +
                                " 单个物品总价值为:" + (itemCount * price)+ " Gil");
            totalPrice += (int)(itemCount * price);
        }
        Chat(totalPrice > 0
                 ? $"部队箱中沉船首饰的总价值为: {totalPrice} Gil"
                 : "部队箱中没有沉船首饰");
    }

    //获取当前部队箱的InventoryType
    private static unsafe InventoryType GetCurrentFcPage(AtkUnitBase* addon) => 
        addon == null ? InventoryType.FreeCompanyPage1 : (InventoryType)(20000 + addon->AtkValues[2].UInt);
}
