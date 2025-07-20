using System.Numerics;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using OmenTools;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ImGuiNET;

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
    private bool showWindow;     // 控制窗口显示
    private nint fcChestPtr = nint.Zero; // 缓存部队箱界面指针

    public override void Init()
    {
        TaskHelper ??= new() { TimeLimitMS = 5_000 };
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "FreeCompanyChest", OnFCChestAddon);
        DService.UiBuilder.Draw += DrawUi; // 注册ImGui绘制
    }
    public override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnFCChestAddon);
        DService.UiBuilder.Draw -= DrawUi; // 注销ImGui绘制
        base.Uninit();
    }

    private void OnFCChestAddon(AddonEvent type, AddonArgs? args)
    {
        TaskHelper.Abort();
        showWindow = false;
        fcChestPtr = nint.Zero;
    }
    //画UI
    private unsafe void DrawUi()
    {   
        
            // 查找部队箱界面
        var companyChestUi = DService.Gui.GetAddonByName("FreeCompanyChest");
        var addon = (AtkUnitBase*)companyChestUi;
        showWindow = companyChestUi != nint.Zero;
        fcChestPtr = companyChestUi;

        if (!showWindow) return;

        ImGui.SetNextWindowSize(new Vector2(ImGui.GetWindowSize().Y/2f, ImGui.GetWindowSize().Y/6f), ImGuiCond.Always);
        ImGui.SetNextWindowPos(new Vector2(addon->GetX() ,addon->GetY()- ImGui.GetWindowSize().Y/7f));
        //绘制界面并调用Check方法
        if (ImGui.Begin("部队箱价值统计", ImGuiWindowFlags.NoResize|ImGuiWindowFlags.NoCollapse|ImGuiWindowFlags.NoTitleBar))
        {
            if (ImGui.Button("统计在聊天框",new (-0.1f, -0.1f)))
                Check((AtkUnitBase*)fcChestPtr);
        } 
        ImGui.End();
    }
    //计算当前界面的首饰价值并输出
    private unsafe void Check(AtkUnitBase* addon)
    {
        var fcPage   = GetCurrentFcPage(addon);
        var manager = InventoryManager.Instance();
        if (manager == null) return;
        var id22500 = manager->GetItemCountInContainer(22500, fcPage);
        var id22501 = manager->GetItemCountInContainer(22501, fcPage);
        var id22502 = manager->GetItemCountInContainer(22502, fcPage);
        var id22503 = manager->GetItemCountInContainer(22503, fcPage);
        var id22504 = manager->GetItemCountInContainer(22504, fcPage);
        var id22505 = manager->GetItemCountInContainer(22505, fcPage);
        var id22506 = manager->GetItemCountInContainer(22506, fcPage);
        var id22507 = manager->GetItemCountInContainer(22507, fcPage);
        var total =id22500*8000 + id22501*9000 + id22502*10000 + id22503*13000 + id22504*27000 + id22505*28500 + id22506*30000 + id22507*34500;
        DService.Chat.Print("上等沉船戒指(2w7)数量:"+id22504);
        DService.Chat.Print("上等沉船手镯(2w85)数量:"+id22505);
        DService.Chat.Print("上等沉船耳饰(3w)数量:"+id22506);
        DService.Chat.Print("上等沉船项链(3w45)数量:"+id22507);
        DService.Chat.Print("沉船戒指(8k)数量:"+id22500);
        DService.Chat.Print("沉船手镯(9k)数量:"+id22501);
        DService.Chat.Print("沉船手镯(1w)数量:"+id22502);
        DService.Chat.Print("沉船手镯(1w3)数量:"+id22503);
        DService.Chat.Print("当前页面的所有首饰价值为:"+total);
    }
    //获取当前部队箱的InventoryType
    private static unsafe InventoryType GetCurrentFcPage(AtkUnitBase* addon) => 
        addon == null ? InventoryType.FreeCompanyPage1 : (InventoryType)(20000 + addon->AtkValues[2].UInt);
}
