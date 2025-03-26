using System;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Memory;
using Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public class FastGrandCompanyExchange : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title       = GetLoc("FastGrandCompanyExchangeTitle"),
        Description = GetLoc("FastGrandCompanyExchangeDescription"),
        Category    = ModuleCategories.UIOptimization
    };

    public bool IsExchanging => TaskHelper?.IsBusy ?? false;

    private const string Command = "gce";
    
    private static Config ModuleConfig = null!;
    private static IPC?   ModuleIPC;
    
    public override unsafe void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        Overlay    ??= new(this);
        TaskHelper ??= new();
        ModuleIPC  ??= new();
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "GrandCompanyExchange", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "GrandCompanyExchange", OnAddon);
        if (IsAddonAndNodesReady(GrandCompanyExchange)) OnAddon(AddonEvent.PostSetup, null);

        CommandManager.AddSubCommand(Command, new(OnCommand) { HelpMessage = GetLoc("FastGrandCompanyExchange-CommandHelp") });
    }
    
    public override void Uninit()
    {
        CommandManager.RemoveSubCommand(Command);
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        
        base.Uninit();
    }

    public override void ConfigUI()
    {
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("Command")}:");
        
        ImGui.SameLine();
        ImGui.TextWrapped($"/pdr {Command} {GetLoc("FastGrandCompanyExchange-CommandHelp")}");
    }

    public override unsafe void OverlayUI()
    {
        if (!IsAddonAndNodesReady(GrandCompanyExchange))
        {
            Overlay.IsOpen = false;
            return;
        }

        var addon = GrandCompanyExchange;
        ImGui.SetWindowPos(new(addon->GetX() + ((addon->GetScaledWidth(true) - ImGui.GetWindowSize().X) / 2), 
                               addon->GetY() - ImGui.GetWindowSize().Y));
        
        ImGui.TextColored(LightSkyBlue, GetLoc("FastGrandCompanyExchangeTitle"));

        using (ImRaii.Group())
        {
            ImGui.AlignTextToFramePadding();
            ImGui.Text(GetLoc("Item"));
            
            ImGui.AlignTextToFramePadding();
            ImGui.Text(GetLoc("Amount"));
        }

        ImGui.SameLine();
        using (ImRaii.Group())
        {
            ImGui.SetNextItemWidth(200f * GlobalFontScale);
            ImGui.InputText("###ItemNameInput", ref ModuleConfig.ExchangeItemName, 512);
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                var grandCompany = PlayerState.Instance()->GrandCompany;
                var gcRank       = PlayerState.Instance()->GetGrandCompanyRank();
                
                var result = LuminaGetter.GetSub<GCScripShopItem>()
                                        .SelectMany(x => x)
                                        .Where(x => LuminaGetter.GetRow<GCScripShopCategory>(x.RowId)!.Value.GrandCompany.RowId == grandCompany)
                                        .Where(x => gcRank >= x.RequiredGrandCompanyRank.RowId)
                                        .Where(x => (x.Item.ValueNullable?.Name.ExtractText() ?? string.Empty)
                                                   .Contains(ModuleConfig.ExchangeItemName, StringComparison.OrdinalIgnoreCase))
                                        .OrderBy(x => (x.Item.ValueNullable?.Name.ExtractText() ?? string.Empty).Length)
                                        .FirstOrDefault();
                if (string.IsNullOrWhiteSpace(ModuleConfig.ExchangeItemName) || result.RowId == 0)
                    ModuleConfig.ExchangeItemName = LuminaGetter.GetRow<Item>(21072)!.Value.Name.ExtractText();
                else if (result.RowId != 0) 
                    ModuleConfig.ExchangeItemName = result.Item.Value.Name.ExtractText();
                ModuleConfig.Save(this);
            }
            
            ImGui.SetNextItemWidth(200f * GlobalFontScale);
            ImGui.InputInt("###ItemCountInput", ref ModuleConfig.ExchangeItemCount, 1, 1);
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                ModuleConfig.ExchangeItemCount = Math.Max(-1, ModuleConfig.ExchangeItemCount);
                ModuleConfig.Save(this);
            }
        }
        
        ImGui.SameLine();
        if (ImGuiOm.ButtonIconWithTextVertical(FontAwesomeIcon.ExchangeAlt, GetLoc("Exchange"),
                                               new Vector2(ImGui.CalcTextSize(GetLoc("Exchange")).X * 1.5f, ImGui.GetItemRectSize().Y)))
            EnqueueByName(ModuleConfig.ExchangeItemName, ModuleConfig.ExchangeItemCount);
    }

    private void OnAddon(AddonEvent type, AddonArgs? args)
    {
        Overlay.IsOpen = type switch
        {
            AddonEvent.PostSetup   => true,
            AddonEvent.PreFinalize => false,
            _                      => Overlay.IsOpen
        };
    }

    private void OnCommand(string command, string args)
    {
        args = args.Trim();
        if (string.IsNullOrWhiteSpace(args)) return;
        
        var splited = args.Split(' ');
        if (splited.Length is not (1 or 2)) return;

        if (splited[0] == "default")
        {
            EnqueueByName(ModuleConfig.ExchangeItemName, ModuleConfig.ExchangeItemCount);
            return;
        }
        
        var itemCount = splited.Length == 2 && int.TryParse(splited[1], out var itemCountParsed) && itemCountParsed >= -1 ? itemCountParsed : -1;
        EnqueueByName(splited[0], itemCount);
    }

    public unsafe void EnqueueByName(string itemName, int itemCount = -1)
    {
        if (!IsAddonAndNodesReady(GrandCompanyExchange)) return;
        if (itemName == "default")
        {
            itemName  = ModuleConfig.ExchangeItemName;
            itemCount = ModuleConfig.ExchangeItemCount;
        }
        
        var grandCompany = PlayerState.Instance()->GrandCompany;
        var gcRank       = PlayerState.Instance()->GetGrandCompanyRank();
        var seals        = InventoryManager.Instance()->GetCompanySeals(grandCompany);
        if (seals == 0) return;

        var result = LuminaGetter.GetSub<GCScripShopItem>()
                                .SelectMany(x => x)
                                .Where(x => LuminaGetter.GetRow<GCScripShopCategory>(x.RowId)!.Value.GrandCompany.RowId == grandCompany)
                                .Where(x => gcRank >= x.RequiredGrandCompanyRank.RowId)
                                .Where(x => (x.Item.ValueNullable?.Name.ExtractText() ?? string.Empty)
                                           .Contains(itemName, StringComparison.OrdinalIgnoreCase))
                                .OrderBy(x => (x.Item.ValueNullable?.Name.ExtractText() ?? string.Empty).Length)
                                .FirstOrDefault();
        if (result.RowId == 0) return;

        var isVenture = result.Item.RowId == 21072;

        var singleCost             = result.CostGCSeals;
        var availableExchangeCount = (int)(seals / singleCost);
        var exchangeCount = Math.Min(itemCount == -1 ? availableExchangeCount : itemCount, availableExchangeCount);
        if (exchangeCount == 0)
        {
            // 不管怎么说 Delay 一下方便其他模块控制
            TaskHelper.DelayNext(100);
            return;
        }

        var categoryData = LuminaGetter.GetRow<GCScripShopCategory>(result.RowId)!.Value;
        var tier         = categoryData.Tier;
        var subCategory  = categoryData.SubCategory;

        if (GrandCompanyExchange->AtkValues[2].UInt != (uint)tier - 1)
        {
            TaskHelper.Enqueue(() =>
            {
                if (GrandCompanyExchange->AtkValues[2].UInt == (uint)tier - 1) return true;
                Callback(GrandCompanyExchange, true, 1, tier - 1);
                return false;
            }, "点击军衔类别");
        }

        TaskHelper.Enqueue(() => Callback(GrandCompanyExchange, true, 2, (int)subCategory), "点击道具类别");
        
        TaskHelper.Enqueue(() =>
        {
            var listNode = (AtkComponentNode*)GrandCompanyExchange->GetNodeById(57);
            if (listNode == null) return;

            for (var i = 0; i < 40; i++)
            {
                try
                {
                    var offset   = 17 + i;
                    var atkValue = GrandCompanyExchange->AtkValues[offset];
                    var name     = MemoryHelper.ReadSeStringNullTerminated((nint)atkValue.String.Value);
                    if (string.IsNullOrWhiteSpace(name.ExtractText()) || name.ExtractText() != result.Item.Value.Name.ExtractText()) continue;

                    var listComponent = (AtkComponentList*)listNode->Component;
                    if (listComponent->GetItemDisabledState(i)) continue;

                    var numericInputNode = (AtkComponentNumericInput*)listComponent->GetItemRenderer(i)->UldManager.SearchNodeById(6)->GetComponent();
                    if (numericInputNode == null) continue;

                    var maxExchangeCount = Math.Min(numericInputNode->Data.Max, exchangeCount);
                    listComponent->SetItemCount(maxExchangeCount);
                    numericInputNode->SetValue(maxExchangeCount);
                    
                    listComponent->SelectItem(i, true);
                    listComponent->DispatchItemEvent(i, AtkEventType.ListItemClick);

                    if (!isVenture && itemCount == -1)
                        TaskHelper.Enqueue(() => EnqueueByName(itemName, itemCount));

                    break;
                }
                catch
                {
                    // ignored
                }
            }
        }, "点击道具");

        if (isVenture)
        {
            TaskHelper.Enqueue(() =>
            {
                if (!IsAddonAndNodesReady(ShopExchangeCurrencyDialog)) return false;
                
                var numericInput = (AtkComponentNumericInput*)ShopExchangeCurrencyDialog->GetNodeById(13)->GetComponent();
                if (numericInput == null) return true;
                
                numericInput->SetValue(itemCount == -1 ? numericInput->Data.Max : Math.Min(itemCount, numericInput->Data.Max));
                
                var buttonNode = ShopExchangeCurrencyDialog->GetButtonNodeById(17);
                if (buttonNode == null) return true;

                buttonNode->ClickAddonButton(ShopExchangeCurrencyDialog);
                return true;
            }, "交换货币");
        }
        
        TaskHelper.Enqueue(() => ClickSelectYesnoYes());
    }

    private class Config : ModuleConfiguration
    {
        public string ExchangeItemName  = string.Empty;
        public int    ExchangeItemCount = -1;
    }
    
    public class IPC : DailyModuleIPCBase
    {
        private const string IsBusyName = $"DailyRoutines.Modules.FastGrandCompanyExchange.IsBusy";
        private static ICallGateProvider<bool>? IsBusyIPC;
        
        public override void Init()
        {
            IsBusyIPC ??= DService.PI.GetIpcProvider<bool>(IsBusyName);
            IsBusyIPC.RegisterFunc(() => ModuleManager.GetModule<FastGrandCompanyExchange>().IsExchanging);
        }
    }
}
