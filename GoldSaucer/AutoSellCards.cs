using System;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;

namespace DailyRoutines.ModulesPublic;

// TODO: 调回原版浅蓝界面修改未完全覆盖问题
public unsafe class AutoSellCards : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = GetLoc("AutoSellCardsTitle"),
        Description         = GetLoc("AutoSellCardsDescription"),
        Category            = ModuleCategories.GoldSaucer,
        ModulesPrerequisite = ["InstantLeaveDuty", "ContentFinderCommand"]
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static HorizontalListNode? LayoutNode;
    private static TextNode?           TitleNode;
    private static TextButtonNode?     StartButton;
    private static TextButtonNode?     StopButton;
    
    private const string Command = "scards";

    protected override void Init()
    {
        TaskHelper ??= new() { TimeLimitMS = 30_000, ShowDebug = true };

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "ShopCardDialog", OnAddonDialog);
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "TripleTriadCoinExchange", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "TripleTriadCoinExchange", OnAddon);

        CommandManager.AddSubCommand(Command, new(OnCommand) { HelpMessage = GetLoc("AutoSellCards-CommandHelp") });
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("Command")}:");
        
        ImGui.SameLine();
        ImGui.Text($"/pdr {Command} → {GetLoc("AutoSellCards-CommandHelp")}");
    }

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PostDraw:
                if (TripleTriadCoinExchange == null) return;

                if (LayoutNode == null)
                {
                    TitleNode = new TextNode
                    {
                        LineSpacing      = 23,
                        AlignmentType    = AlignmentType.Left,
                        FontSize         = 23,
                        FontType         = FontType.TrumpGothic,
                        TextColor        = ColorHelper.GetColor(2),
                        TextOutlineColor = ColorHelper.GetColor(7),
                        BackgroundColor  = Vector4.Zero,
                        Size             = new(86, 31),
                        Position         = new(15, 465),
                        SeString         = Info.Title
                    };
                    TitleNode.AttachNode(TripleTriadCoinExchange->RootNode);
                    
                    LayoutNode = new HorizontalListNode
                    {
                        IsVisible = true,
                        Position  = new(15, 495)
                    };
                    LayoutNode.AttachNode(TripleTriadCoinExchange->RootNode);

                    StartButton = new()
                    {
                        IsVisible = true,
                        Size      = new(260, 35),
                        SeString  = GetLoc("Start"),
                        OnClick = () =>
                        {
                            TaskHelper.Abort();
                            StartHandOver();
                        }
                    };
                    LayoutNode.AddNode(StartButton);
            
                    StopButton = new()
                    {
                        IsVisible = true,
                        Size      = new(260, 35),
                        SeString  = GetLoc("Stop"),
                        OnClick   = () => TaskHelper.Abort()
                    };
                    LayoutNode.AddNode(StopButton);

                    TripleTriadCoinExchange->RootNode->SetHeight(486   + 60);
                    TripleTriadCoinExchange->WindowNode->SetHeight(486 + 60);
                    
                    for (var i = 0; i < TripleTriadCoinExchange->WindowNode->Component->UldManager.NodeListCount; i++)
                    {
                        var node = TripleTriadCoinExchange->WindowNode->Component->UldManager.NodeList[i];
                        if (node == null) continue;

                        if (node->Height == 486)
                            node->SetHeight(486 + 60);
                    }
                }

                if (TaskHelper.IsBusy)
                {
                    StartButton.IsEnabled = false;
                    StopButton.IsEnabled  = true;
                }
                else
                {
                    StartButton.IsEnabled = true;
                    StopButton.IsEnabled  = false;
                }
                
                break;
            case AddonEvent.PreFinalize:
                LayoutNode?.DetachNode();
                LayoutNode = null;
                
                StartButton?.DetachNode();
                StartButton = null;
                
                StopButton?.DetachNode();
                StopButton = null;
                
                TitleNode?.DetachNode();
                TitleNode = null;
                
                TaskHelper?.Abort();
                break;
        }
    }
    
    private static void OnAddonDialog(AddonEvent type, AddonArgs args)
    {
        if (ShopCardDialog == null) return;
        
        Callback(ShopCardDialog, true, 0, ShopCardDialog->AtkValues[6].UInt);
        ShopCardDialog->FireCloseCallback();
        ShopCardDialog->Close(true);
    }

    private void OnCommand(string command, string args)
    {
        // 交换界面已经打开了
        if (IsAddonAndNodesReady(TripleTriadCoinExchange))
        {
            StartHandOver();
            return;
        }
        
        // 附近没有可用的幻卡兑换地点
        if (!IsEventIDNearby(721135))
        {
            TaskHelper.Enqueue(() => ChatManager.SendMessage("/pdrduty n 195"), "发送九宫幻卡对局室参加申请");
            TaskHelper.Enqueue(() => GameState.TerritoryType == 579 && IsScreenReady(), "等待进入九宫幻卡对局室");
        }

        TaskHelper.Enqueue(() => new EventStartPackt(LocalPlayerState.EntityID, 721135).Send(), "发包打开幻卡交换页面");
        TaskHelper.Enqueue(StartHandOver, "开始交换");
        TaskHelper.Enqueue(() =>
        {
            if (!IsAddonAndNodesReady(TripleTriadCoinExchange)) return;
            Callback(TripleTriadCoinExchange, true, -1);
        }, "交换完毕, 关闭界面");
        TaskHelper.Enqueue(() => ChatManager.SendMessage("/pdr leaveduty"), "离开幻卡对局室");
    }

    private bool? StartHandOver()
    {
        if (!Throttler.Throttle("AutoSellCards-HandOver")) 
            return false;

        if (IsAddonAndNodesReady(ShopCardDialog))
        {
            TaskHelper.RemoveAllTasks(2);
            return true;
        }

        if (!IsAddonAndNodesReady(TripleTriadCoinExchange)) 
            return false;

        var cardsAmount = TripleTriadCoinExchange->AtkValues[1].Int;
        if (cardsAmount == 0)
        {
            TaskHelper.RemoveAllTasks(2);
            return true;
        }

        var isCardInDeck = Convert.ToBoolean(TripleTriadCoinExchange->AtkValues[204].Byte);
        if (!isCardInDeck)
        {
            var message = GetLoc("AutoSellCards-CurrentCardNotInDeckMessage");
            ChatError(message);
            NotificationWarning(message);
            
            TaskHelper.RemoveAllTasks(2);
            return true;
        }

        TaskHelper.Enqueue(() => Callback(TripleTriadCoinExchange, true, 0, 0, 0), "点击交换幻卡", weight: 2);
        TaskHelper.DelayNext(100, "等待 100 毫秒", weight: 2);
        TaskHelper.Enqueue(StartHandOver, "开始新一轮检测交换", weight: 2);
        return true;
    }

    protected override void Uninit()
    {
        CommandManager.RemoveSubCommand(Command);
        
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        OnAddon(AddonEvent.PreFinalize, null);
        
        DService.AddonLifecycle.UnregisterListener(OnAddonDialog);
    }
}
