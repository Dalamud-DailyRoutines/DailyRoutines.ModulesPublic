using System.Numerics;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Manager;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using OmenTools.Interop.Game.Models.Packets.Upstream;
using OmenTools.OmenService;
using OmenTools.Threading;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoSellCards : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = Lang.Get("AutoSellCardsTitle"),
        Description         = Lang.Get("AutoSellCardsDescription"),
        Category            = ModuleCategory.GoldSaucer,
        ModulesPrerequisite = ["InstantLeaveDuty", "ContentFinderCommand"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private HorizontalListNode? layoutNode;
    private TextNode?           titleNode;
    private TextButtonNode?     startButton;
    private TextButtonNode?     stopButton;

    protected override void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 30_000, ShowDebug = true };

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "ShopCardDialog", OnAddonDialog);

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "TripleTriadCoinExchange", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "TripleTriadCoinExchange", OnAddon);

        CommandManager.Instance().AddSubCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("AutoSellCards-CommandHelp") });
    }
    
    protected override void Uninit()
    {
        CommandManager.Instance().RemoveSubCommand(COMMAND);

        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
        OnAddon(AddonEvent.PreFinalize, null);

        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonDialog);
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Command")}:");

        ImGui.SameLine();
        ImGui.TextUnformatted($"/pdr {COMMAND} → {Lang.Get("AutoSellCards-CommandHelp")}");
    }

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PostDraw:
                if (TripleTriadCoinExchange == null) return;

                if (layoutNode == null)
                {
                    titleNode = new TextNode
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
                        String           = Info.Title
                    };
                    titleNode.AttachNode(TripleTriadCoinExchange->RootNode);

                    layoutNode = new HorizontalListNode
                    {
                        IsVisible = true,
                        Position  = new(15, 495)
                    };
                    layoutNode.AttachNode(TripleTriadCoinExchange->RootNode);

                    startButton = new()
                    {
                        IsVisible = true,
                        Size      = new(260, 35),
                        String    = Lang.Get("Start"),
                        OnClick = () =>
                        {
                            TaskHelper.Abort();
                            StartHandOver();
                        }
                    };
                    layoutNode.AddNode(startButton);

                    stopButton = new()
                    {
                        IsVisible = true,
                        Size      = new(260, 35),
                        String    = Lang.Get("Stop"),
                        OnClick   = () => TaskHelper.Abort()
                    };
                    layoutNode.AddNode(stopButton);

                    TripleTriadCoinExchange->RootNode->SetHeight(486   + 60);
                    TripleTriadCoinExchange->WindowNode->SetHeight(486 + 60);

                    for (var i = 0; i < TripleTriadCoinExchange->WindowNode->Component->UldManager.NodeListCount; i++)
                    {
                        var node = TripleTriadCoinExchange->WindowNode->Component->UldManager.NodeList[i];
                        if (node == null) continue;

                        if (node->Height == 486)
                            node->SetHeight(486 + 60);
                        if (node->Height == 470)
                            node->SetHeight(470 + 60);
                    }
                }

                if (TaskHelper.IsBusy)
                {
                    startButton.IsEnabled = false;
                    stopButton.IsEnabled  = true;
                }
                else
                {
                    startButton.IsEnabled = true;
                    stopButton.IsEnabled  = false;
                }

                break;
            case AddonEvent.PreFinalize:
                layoutNode?.Dispose();
                layoutNode = null;

                startButton?.Dispose();
                startButton = null;

                stopButton?.Dispose();
                stopButton = null;

                titleNode?.Dispose();
                titleNode = null;

                TaskHelper?.Abort();
                break;
        }
    }

    private static void OnAddonDialog(AddonEvent type, AddonArgs args)
    {
        if (ShopCardDialog == null) return;

        ShopCardDialog->Callback(0, ShopCardDialog->AtkValues[6].UInt);
        ShopCardDialog->FireCloseCallback();
        ShopCardDialog->Close(true);
    }

    private void OnCommand(string command, string args)
    {
        // 交换界面已经打开了
        if (TripleTriadCoinExchange->IsAddonAndNodesReady())
        {
            StartHandOver();
            return;
        }

        // 附近没有可用的幻卡兑换地点
        if (!EventFramework.Instance()->IsEventIDNearby(721135))
        {
            TaskHelper.Enqueue(() => ChatManager.Instance().SendMessage("/pdrduty n 195"),       "发送九宫幻卡对局室参加申请");
            TaskHelper.Enqueue(() => GameState.TerritoryType == 579 && UIModule.IsScreenReady(), "等待进入九宫幻卡对局室");
        }

        TaskHelper.Enqueue(() => new EventStartPackt(LocalPlayerState.EntityID, 721135).Send(), "发包打开幻卡交换页面");
        TaskHelper.Enqueue(StartHandOver,                                                       "开始交换");
        TaskHelper.Enqueue
        (
            () =>
            {
                if (!TripleTriadCoinExchange->IsAddonAndNodesReady()) return;
                TripleTriadCoinExchange->Callback(-1);
            },
            "交换完毕, 关闭界面"
        );
        TaskHelper.Enqueue(() => ChatManager.Instance().SendMessage("/pdr leaveduty"), "离开幻卡对局室");
    }

    private bool StartHandOver()
    {
        if (!Throttler.Shared.Throttle("AutoSellCards-HandOver"))
            return false;

        if (ShopCardDialog->IsAddonAndNodesReady())
        {
            TaskHelper.RemoveQueueTasks(2);
            return true;
        }

        if (!TripleTriadCoinExchange->IsAddonAndNodesReady())
            return false;

        var cardsAmount = TripleTriadCoinExchange->AtkValues[1].Int;

        if (cardsAmount == 0)
        {
            TaskHelper.RemoveQueueTasks(2);
            return true;
        }

        var isCardInDeck = Convert.ToBoolean(TripleTriadCoinExchange->AtkValues[204].Byte);

        if (!isCardInDeck)
        {
            var message = Lang.Get("AutoSellCards-CurrentCardNotInDeckMessage");
            NotifyHelper.Instance().ChatError(message);
            NotifyHelper.Instance().NotificationWarning(message);

            TaskHelper.RemoveQueueTasks(2);
            return true;
        }

        TaskHelper.Enqueue(() => TripleTriadCoinExchange->Callback(0, 0, 0), "点击交换幻卡", weight: 2);
        TaskHelper.DelayNext(100, "等待 100 毫秒", 2);
        TaskHelper.Enqueue(StartHandOver, "开始新一轮检测交换", weight: 2);
        return true;
    }
    
    #region 常量

    private const string COMMAND = "scards";

    #endregion
}
