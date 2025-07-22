using System.Globalization;
using System.Linq;
using DailyRoutines.Abstracts;
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
        Title       = "自动统计部队储物柜变卖用道具价值",
        Description = "打开部队储物柜后, 自动统计当前页面下所有变卖用道具的总价值金额",
        Category    = ModuleCategories.UIOptimization,
        Author      = ["采购"]
    };

    private static readonly uint[] ItemIDs = 
        LuminaGetter.Get<Item>().Where(x => x.ItemSortCategory.Value.Param == 150).Select(x => x.RowId).ToArray();

    private static long LastTotalPrice;
    
    protected override void Init()
    {
        Overlay ??= new(this);
        
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
        
        LastTotalPrice = 0;
    }
    
    protected override unsafe void OverlayUI()
    {
        if (FreeCompanyChest == null)
        {
            Overlay.IsOpen = false;
            return;
        }

        var node = FreeCompanyChest->GetNodeById(108);
        if (!IsAddonAndNodesReady(FreeCompanyChest) || node == null)
        {
            Overlay.Flags |= ImGuiWindowFlags.NoBackground;
            return;
        }
        
        if (Throttler.Throttle("AutoDisplayFCCSubmarineItemsValue-OnUpdate"))
            LastTotalPrice = TryGetTotalPrice(out var totalPrice) ? totalPrice : 0;

        if (LastTotalPrice == 0)
        {
            Overlay.Flags |= ImGuiWindowFlags.NoBackground;
            return;
        }
        
        Overlay.Flags &= ~ImGuiWindowFlags.NoBackground;
        
        ImGui.SetWindowPos(new(node->ScreenX, FreeCompanyChest->GetY() - ImGui.GetWindowSize().Y));
        
        ImGui.Text($"变卖用道具总价值: {FormatNumber(LastTotalPrice)}");
    }

    private static unsafe bool TryGetTotalPrice(out long totalPrice)
    {
        totalPrice = 0;

        if (!IsAddonAndNodesReady(FreeCompanyChest)) return false;
        
        var manager = InventoryManager.Instance();
        if (manager == null) return false;

        var fcPage = GetCurrentFcPage(FreeCompanyChest);
        if (fcPage == InventoryType.Invalid) return false;
        
        foreach (var item in ItemIDs)
        {
            if (!LuminaGetter.TryGetRow(item, out Item itemData)) continue;
            
            var itemCount = manager->GetItemCountInContainer(item, fcPage);
            if (itemCount == 0) continue;
            
            var price = itemData.PriceLow;
            totalPrice += itemCount * price;
        }
        
        return totalPrice > 0;
    }
    
    private static string FormatNumber(long number) =>
        Lang.CurrentLanguage is not ("ChineseSimplified" or "ChineseTraditional") ? 
            number.ToString(CultureInfo.InvariantCulture) : 
            FormatNumberByChineseNotation(number, Lang.CurrentLanguage);
    
    private static unsafe InventoryType GetCurrentFcPage(AtkUnitBase* addon) => 
        addon == null || GetNodeVisible(addon->GetNodeById(106)) ? InventoryType.Invalid : (InventoryType)(20000 + addon->AtkValues[2].UInt);
}
