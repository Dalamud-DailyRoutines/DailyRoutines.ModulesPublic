using System.Numerics;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Internal;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using OmenTools.Dalamud;
using OmenTools.Info.Game.Packets.Upstream;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe class AutoCollectableExchange : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoCollectableExchangeTitle"),
        Description = Lang.Get("AutoCollectableExchangeDescription"),
        Category    = ModuleCategory.Interface,
        PreviewImageURL =
        [
            "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/AutoCollectableExchange/preview-1.png"
        ]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static readonly CompSig HandInCollectablesSig =
        new("48 89 6C 24 ?? 48 89 74 24 ?? 57 41 56 41 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B F1 48 8B 49");
    private delegate nint HandInCollectablesDelegate
    (
        AgentInterface* agentCollectablesShop
    );
    private HandInCollectablesDelegate? HandInCollectables;

    private AtkEventWrapper? exchangeEvent;
    private TextButtonNode?  scripExchangeButton;
    private string?          currentExchangeText;

    protected override void Init()
    {
        TaskHelper ??= new();

        HandInCollectables ??= HandInCollectablesSig.GetDelegate<HandInCollectablesDelegate>();

        LogMessageManager.Instance().RegPre(OnLogMessage);

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostUpdate,  "CollectablesShop", OnAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize, "CollectablesShop", OnAddon);
        if (CollectablesShopAddon != null)
            OnAddon(AddonEvent.PostSetup, null);
    }

    protected override void Uninit()
    {
        IAddonLifecycle.Instance().UnregisterListener(OnAddon);
        
        LogMessageManager.Instance().Unreg(OnLogMessage);

        exchangeEvent?.Dispose();
        exchangeEvent = null;
        
        scripExchangeButton?.Dispose();
        scripExchangeButton = null;

        currentExchangeText = null;
    }

    private void OnAddon
    (
        AddonEvent type,
        AddonArgs? args
    )
    {
        switch (type)
        {
            case AddonEvent.PreFinalize:
                exchangeEvent?.Dispose();
                exchangeEvent = null;
        
                scripExchangeButton = null;
                currentExchangeText = null;
                break;
            
            case AddonEvent.PostUpdate:
                if (!CollectablesShopAddon->IsAddonAndNodesReady()) 
                    return;

                var exchangeButton = CollectablesShopAddon->GetComponentButtonById(51);
                if (exchangeButton == null) return;

                var exchangeNode = (AtkResNode*)exchangeButton->OwnerNode;

                if (exchangeEvent == null)
                {
                    exchangeNode->ClearEvents();

                    exchangeEvent = new
                    ((eventType, addon, _, _) =>
                        {
                            switch (eventType)
                            {
                                case AtkEventType.ButtonClick:
                                    OnExchangeButtonClick();
                                    break;

                                case AtkEventType.MouseOver:
                                    if (!PluginConfig.Instance().ConflictKeyBinding.IsPressed())
                                    {
                                        AtkStage.Instance()->TooltipManager.ShowTooltip
                                        (
                                            addon->Id,
                                            exchangeNode,
                                            Lang.Get
                                            (
                                                "AutoCollectableExchange-ConflictKeyHint",
                                                new Dictionary<string, object>
                                                {
                                                    ["conflictKey"] = PluginConfig.Instance().ConflictKeyBinding
                                                }
                                            )
                                        );
                                    }

                                    break;

                                case AtkEventType.MouseOut:
                                    AtkStage.Instance()->TooltipManager.HideTooltip(addon->Id);
                                    break;
                            }
                        }
                    );
                    exchangeEvent.Add(CollectablesShopAddon, exchangeNode, AtkEventType.ButtonClick);
                    exchangeEvent.Add(CollectablesShopAddon, exchangeNode, AtkEventType.MouseOver);
                    exchangeEvent.Add(CollectablesShopAddon, exchangeNode, AtkEventType.MouseOut);
                }

                UpdateExchangeButton(exchangeButton);
                UpdateScripExchangeButton(exchangeNode);
                break;
        }
    }
    
    private void OnLogMessage
    (
        ref bool                isPrevented,
        ref uint                logMessageID,
        ref LogMessageQueueItem item
    )
    {
        if (logMessageID != 1941)
            return;
        if (!TaskHelper.IsBusy)
            return;

        isPrevented = true;
    }

    private void OnExchangeButtonClick()
    {
        if (TaskHelper.IsBusy)
        {
            TaskHelper.Abort();
            return;
        }

        if (PluginConfig.Instance().ConflictKeyBinding.IsPressed())
        {
            var list = CollectablesShopAddon->GetComponentNodeById(31)->GetAsAtkComponentList();
            if (list == null) 
                return;
            
            EnqueueExchange(-1, 0);
            return;
        }

        HandInCollectables(AgentModule.Instance()->GetAgentByInternalId(AgentId.CollectablesShop));
    }

    private void UpdateExchangeButton
    (
        AtkComponentButton* exchangeButton
    )
    {
        string text;
        var    hideTooltip = true;

        if (TaskHelper.IsBusy)
            text = Lang.Get("Stop");
        else
        {
            if (PluginConfig.Instance().ConflictKeyBinding.IsPressed())
                text = Lang.Get("AutoCollectableExchange-BatchExchange");
            else
            {
                text        = LuminaWrapper.GetAddonText(13788);
                hideTooltip = false;
            }
        }

        if (text == currentExchangeText)
            return;
        
        if (hideTooltip)
            AtkStage.Instance()->TooltipManager.HideTooltip(CollectablesShopAddon->Id);
        else if (currentExchangeText != null)
            ShowTooltip(CollectablesShopAddon, exchangeButton);

        currentExchangeText = text;
        exchangeButton->SetText(text);
    }

    private void UpdateScripExchangeButton(AtkResNode* exchangeNode)
    {
        if (scripExchangeButton == null)
        {
            scripExchangeButton = new TextButtonNode
            {
                Position = new Vector2
                (
                    exchangeNode->X - exchangeNode->Width - 8f,
                    exchangeNode->Y + 2
                ),
                Size    = new(exchangeNode->Width, exchangeNode->Height),
                String  = LuminaGetter.GetRowOrDefault<InclusionShop>(3801094).ShopName.ToString(),
                OnClick = EnqueueScripExchange
            };
            scripExchangeButton.AttachNode(CollectablesShopAddon);
        }

        scripExchangeButton.IsEnabled = !TaskHelper.IsBusy;
    }

    private void EnqueueScripExchange()
    {
        if (LocalPlayerState.Object is not { } localPlayer)
            return;
        
        if (!EventFramework.Instance()->TryGetNearestEventID
            (
                x => x.EventId.ContentId is EventHandlerContent.PreHandler,
                x => LuminaWrapper.GetENPCName(x.BaseId).Equals
                (
                    LuminaWrapper.GetENPCName(1001617),
                    StringComparison.OrdinalIgnoreCase
                ),
                localPlayer.Position,
                out var eventID
            ))
        {
            NotifyHelper.ToastError(Lang.Get("AutoCollectableExchange-Notification-NoInclusionShop"));
            return;
        }
        
        TaskHelper.Enqueue
        (() =>
            {
                if (CollectablesShopAddon->IsAddonAndNodesReady())
                    CollectablesShopAddon->Close(true);
            }
        );
        
        TaskHelper.Enqueue(() => !ICondition.Instance().IsOccupiedInEvent);

        TaskHelper.Enqueue
        (() => new EventStartPackt
         (
             localPlayer.EntityID,
             eventID
         ).Send());
    }

    private void EnqueueExchange(int lastListLength, uint finishedRound) =>
        TaskHelper.Enqueue
        (
            () =>
            {
                if (CollectablesShopAddon == null || SelectYesno->IsAddonAndNodesReady())
                {
                    NotifyFinishAndAbort();
                    return true;
                }

                var list = CollectablesShopAddon->GetComponentNodeById(31)->GetAsAtkComponentList();
                if (list == null) 
                    return false;

                var currentListLength = list->ListLength;
                if (currentListLength <= 0)
                {
                    NotifyFinishAndAbort();
                    return true;
                }
                
                if (list->ListLength == lastListLength)
                    return false;

                HandInCollectables(AgentModule.Instance()->GetAgentByInternalId(AgentId.CollectablesShop));

                finishedRound += 1;
                TaskHelper.Enqueue(() => EnqueueExchange(currentListLength, finishedRound), "EnqueueNewRound");
                return true;
                
                void NotifyFinishAndAbort()
                {
                    TaskHelper.Abort();
                    if (finishedRound == 0)
                        return;

                    var message = Lang.Get
                    (
                        "AutoCollectableExchange-Message",
                        new Dictionary<string, object>
                        {
                            ["count"] = finishedRound
                        }
                    );
                    
                    NotifyHelper.Instance().TrayInfo(message, Lang.Get("AutoCollectableExchange-Notification"));
                    NotifyHelper.Instance().Chat(message);
                }
            }
        );

    private static void ShowTooltip(AtkUnitBase* addon, AtkComponentButton* button)
    {
        if (!button->IsEnabled)
            return;
        
        AtkStage.Instance()->TooltipManager.ShowTooltip
        (
            addon->Id,
            (AtkResNode*)button->OwnerNode,
            Lang.Get
            (
                "AutoCollectableExchange-ConflictKeyHint",
                new Dictionary<string, object>
                {
                    ["conflictKey"] = PluginConfig.Instance().ConflictKeyBinding
                }
            )
        );
    }
}
