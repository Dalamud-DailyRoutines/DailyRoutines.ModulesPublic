using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Memory;
using Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Addon;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.TabBar;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public class FastGrandCompanyExchange : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("FastGrandCompanyExchangeTitle"),
        Description = GetLoc("FastGrandCompanyExchangeDescription"),
        Category    = ModuleCategories.UIOperation
    };

    public bool IsExchanging => TaskHelper?.IsBusy ?? false;

    private const string Command = "gce";
    
    private static Config ModuleConfig = null!;

    private static DRFastGCExchange? Addon;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        TaskHelper ??= new();
        
        Addon ??= new(this)
        {
            InternalName          = "DRFastGCExchange",
            Title                 = Info.Title,
            Size                  = new(290f, 240f),
            Position              = new(800f, 350f),
            NativeController      = Service.AddonController,
            RememberClosePosition = true,
        };

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "GrandCompanyExchange", OnAddon);

        CommandManager.AddSubCommand(Command, new(OnCommand) { HelpMessage = GetLoc("FastGrandCompanyExchange-CommandHelp") });
    }

    protected override void Uninit()
    {
        CommandManager.RemoveSubCommand(Command);
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        
        Addon?.Dispose();
        Addon = null;
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("Command")}:");
        
        ImGui.SameLine();
        ImGui.TextWrapped($"/pdr {Command} {GetLoc("FastGrandCompanyExchange-CommandHelp")}");
    }
    
    private static unsafe void OnAddon(AddonEvent type, AddonArgs? args)
    {
        if (Addon.IsOpen || !IsAddonAndNodesReady(GrandCompanyExchange)) return;
        Addon.Open();
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

    public unsafe bool? EnqueueByName(string itemName, int itemCount = -1)
    {
        if (!IsAddonAndNodesReady(GrandCompanyExchange)) return false;
        if (IsAddonAndNodesReady(SelectYesno))
        {
            ClickSelectYesnoYes();
            return false;
        }
        
        if (itemName == "default")
        {
            itemName  = ModuleConfig.ExchangeItemName;
            itemCount = ModuleConfig.ExchangeItemCount;
        }
        
        var grandCompany = PlayerState.Instance()->GrandCompany;
        var gcRank       = PlayerState.Instance()->GetGrandCompanyRank();
        var seals        = InventoryManager.Instance()->GetCompanySeals(grandCompany);
        if (seals == 0) return true;

        var result = LuminaGetter.GetSub<GCScripShopItem>()
                                .SelectMany(x => x)
                                .Where(x => LuminaGetter.GetRow<GCScripShopCategory>(x.RowId)!.Value.GrandCompany.RowId == grandCompany)
                                .Where(x => gcRank >= x.RequiredGrandCompanyRank.RowId)
                                .Where(x => (x.Item.ValueNullable?.Name.ExtractText() ?? string.Empty)
                                           .Contains(itemName, StringComparison.OrdinalIgnoreCase))
                                .OrderBy(x => (x.Item.ValueNullable?.Name.ExtractText() ?? string.Empty).Length)
                                .FirstOrDefault();
        if (result.RowId == 0) return true;
        
        var singleCost             = result.CostGCSeals;
        var availableExchangeCount = (int)(seals / singleCost);
        var exchangeCount = Math.Min(itemCount == -1 ? availableExchangeCount : itemCount, availableExchangeCount);
        if (exchangeCount == 0)
        {
            // 不管怎么说 Delay 一下方便其他模块控制
            TaskHelper.DelayNext(100);
            return true;
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

                    SendEvent(AgentId.GrandCompanyExchange, 0, 0, i, exchangeCount, 0, true, false);
                    
                    if (itemCount == -1)
                        TaskHelper.Enqueue(() => EnqueueByName(itemName, itemCount));

                    break;
                }
                catch
                {
                    // ignored
                }
            }
        }, "点击道具");

        /*
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
        }*/
        
        return true;
    }
    
    private unsafe class DRFastGCExchange(FastGrandCompanyExchange Instance) : NativeAddon
    {
        private bool IsNotClosed { get; set; }
        
        protected override void OnSetup(AtkUnitBase* addon)
        {
            var layoutNode = new VerticalListNode
            {
                IsVisible   = true,
                Position    = ContentStartPosition + new Vector2(0, 2),
                ItemSpacing = 1,
                Size        = new(275, 28),
                FitContents = true,
            };

            var exchangeButtonNode = new TextButtonNode
            {
                IsVisible = true,
                IsEnabled = true,
                Size      = new(layoutNode.Size.X - 10, 38),
                SeString  = GetLoc("Exchange"),
                OnClick = () =>
                {
                    if (Instance.TaskHelper.IsBusy) return;
                    Instance.EnqueueByName(ModuleConfig.ExchangeItemName, ModuleConfig.ExchangeItemCount);
                }
            };
            
            layoutNode.AddNode(exchangeButtonNode);
            
            layoutNode.AddDummy(5);

            var itemLableNode = new TextNode
            {
                IsVisible = true,
                Size      = new(layoutNode.Size.X - 20, 24),
                FontSize  = 14,
                SeString  = GetLoc("Item"),
            };
            
            layoutNode.AddNode(itemLableNode);

            var itemNameInputNode = new TextInputNode
            {
                IsVisible       = true,
                Size            = new(layoutNode.Size.X - 10, 35),
                SeString        = ModuleConfig.ExchangeItemName,
                OnInputReceived = x => ModuleConfig.ExchangeItemName = x.ExtractText(),
            };

            itemNameInputNode.OnInputComplete = UpdateExchangeItem;
            itemNameInputNode.OnEditComplete  = () => UpdateExchangeItem(itemNameInputNode.SeString);
            itemNameInputNode.OnUnfocused     = () => UpdateExchangeItem(itemNameInputNode.SeString);
            
            itemNameInputNode.CursorNode.ScaleY        =  1.4f;
            itemNameInputNode.CurrentTextNode.FontSize =  14;
            itemNameInputNode.CurrentTextNode.Y        += 3f;
            
            layoutNode.AddNode(itemNameInputNode);
            
            layoutNode.AddDummy(5);
            
            var countLableNode = new TextNode
            {
                IsVisible = true,
                Size      = new(layoutNode.Size.X - 20, 24),
                FontSize  = 14,
                SeString  = GetLoc("Amount"),
            };
            
            layoutNode.AddNode(countLableNode);

            var countInputNode = new NumericInputNode
            {
                IsVisible = true,
                Size      = new(layoutNode.Size.X - 10, 35),
                Step      = 1,
                Min       = -1,
                OnValueUpdate = newValue =>
                {
                    ModuleConfig.ExchangeItemCount = newValue;

                    ModuleConfig.ExchangeItemCount = Math.Max(-1, ModuleConfig.ExchangeItemCount);
                    ModuleConfig.Save(Instance);
                },
                Value = ModuleConfig.ExchangeItemCount
            };
            
            layoutNode.AddNode(countInputNode);
            
            AttachNode(layoutNode);
        }

        private void UpdateExchangeItem(SeString x)
        {
            ModuleConfig.ExchangeItemName = x.ExtractText();

            var grandCompany = PlayerState.Instance()->GrandCompany;
            var gcRank       = PlayerState.Instance()->GetGrandCompanyRank();

            var result = LuminaGetter.GetSub<GCScripShopItem>()
                                     .SelectMany(d => d)
                                     .Where(d => LuminaGetter.GetRowOrDefault<GCScripShopCategory>(d.RowId).GrandCompany.RowId == grandCompany)
                                     .Where(d => gcRank                                                                        >= d.RequiredGrandCompanyRank.RowId)
                                     .Where(d => (d.Item.ValueNullable?.Name.ExtractText() ?? string.Empty)
                                                .Contains(ModuleConfig.ExchangeItemName, StringComparison.OrdinalIgnoreCase))
                                     .OrderBy(d => (d.Item.ValueNullable?.Name.ExtractText() ?? string.Empty).Length)
                                     .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(ModuleConfig.ExchangeItemName) || result.RowId == 0)
                ModuleConfig.ExchangeItemName = LuminaWrapper.GetItemName(21072);
            else if (result.RowId != 0)
                ModuleConfig.ExchangeItemName = result.Item.Value.Name.ExtractText();

            if (ModuleConfig.ExchangeItemName == x.ExtractText())
                return;

            IsNotClosed = true;
            Close();
            ModuleConfig.Save(Instance);
        }

        protected override void OnUpdate(AtkUnitBase* addon)
        {
            if (GrandCompanyExchange == null)
            {
                Close();
                return;
            }

            var position = new Vector2(GrandCompanyExchange->RootNode->ScreenX - addon->GetScaledWidth(true), GrandCompanyExchange->RootNode->ScreenY);
            SetPosition(addon,           position);
            SetPosition(addon->RootNode, position);
        }

        protected override void OnFinalize(AtkUnitBase* addon) 
        {
            if (IsNotClosed)
            {
                IsNotClosed = false;
                return;
            }
            
            IsNotClosed = false;
            
            if (GrandCompanyExchange == null) return;
            GrandCompanyExchange->Close(true);
        }
    }

    private class Config : ModuleConfiguration
    {
        public string ExchangeItemName  = string.Empty;
        public int    ExchangeItemCount = -1;
    }
    
    [IPCProvider("DailyRoutines.Modules.FastGrandCompanyExchange.IsBusy")]
    public bool IsCurrentlyBusy => IsExchanging;
}
