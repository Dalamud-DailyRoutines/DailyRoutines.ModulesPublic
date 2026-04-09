using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.OmenService;
using OmenTools.Threading;
using OmenTools.Threading.TaskHelper;

namespace DailyRoutines.ModulesPublic;

public unsafe class FastJoinAnotherPartyRecruitment : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("FastJoinAnotherPartyRecruitmentTitle"),
        Description = Lang.Get("FastJoinAnotherPartyRecruitmentDescription"),
        Category    = ModuleCategory.Recruitment
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private TextButtonNode? button;

    protected override void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 10_000 };

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreDraw,     "LookingForGroupDetail", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreRefresh,  "LookingForGroupDetail", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LookingForGroupDetail", OnAddon);
        if (LookingForGroupDetail->IsAddonAndNodesReady())
            OnAddon(AddonEvent.PreRefresh, null);

        if (LookingForGroup->IsAddonAndNodesReady())
            AgentId.LookingForGroup.SendEvent(1, 17);

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnAddonYesno);
    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon, OnAddonYesno);
        OnAddon(AddonEvent.PreFinalize, null);
    }

    private void OnAddonYesno(AddonEvent type, AddonArgs args)
    {
        if (!TaskHelper.IsBusy) return;
        AddonSelectYesnoEvent.ClickYes();
    }

    private void OnAddon(AddonEvent type, AddonArgs? args)
    {
        switch (type)
        {
            case AddonEvent.PreRefresh:
                CreateButton(LookingForGroupDetail, TaskHelper);
                break;

            case AddonEvent.PreDraw:
                UpdateOtherButtons(LookingForGroupDetail);
                break;

            case AddonEvent.PreFinalize:
                button?.Dispose();
                button = null;
                break;
        }
    }

    private void CreateButton(AtkUnitBase* addon, TaskHelper taskHelper)
    {
        if (addon == null || button != null) return;

        // 团队招募
        var partyCount = addon->AtkValues[19].UInt;
        if (partyCount != 1) return;

        // 自己开的招募
        if (AgentLookingForGroup.Instance()->ListingContentId == LocalPlayerState.ContentID) return;

        // 底部操作栏容器
        var containerNode = addon->GetNodeById(108);
        if (containerNode == null) return;

        button = new()
        {
            Size      = new(140, 28),
            Position  = new(100, 0),
            IsVisible = false,
            String    = Lang.Get("FastJoinAnotherPartyRecruitment-LeaveAndJoin"),
            OnClick   = () => Enqueue(taskHelper)
        };

        button.AttachNode(containerNode);
    }

    private void UpdateOtherButtons(AtkUnitBase* addon)
    {
        if (addon == null) return;

        // 团队招募
        var partyCount = addon->AtkValues[19].UInt;
        if (partyCount != 1) return;

        // 自己开的招募
        if (AgentLookingForGroup.Instance()->ListingContentId == LocalPlayerState.ContentID) return;

        var containerNode = addon->GetNodeById(108);
        if (containerNode != null)
            containerNode->SetPosition(35, 56);

        var button0 = addon->GetComponentButtonById(109);

        if (button0 != null)
        {
            button0->OwnerNode->ToggleVisibility(button0->OwnerNode->X == -50);
            button0->OwnerNode->SetPosition(-50, 0);
        }

        var button1 = addon->GetComponentButtonById(110);

        if (button1 != null)
        {
            button1->OwnerNode->ToggleVisibility(button1->OwnerNode->X == 250);
            button1->OwnerNode->SetPosition(250, 0);
        }

        var button2 = addon->GetComponentButtonById(111);

        if (button2 != null)
        {
            button2->OwnerNode->ToggleVisibility(button2->OwnerNode->X == 400);
            button2->OwnerNode->SetPosition(400, 0);
        }

        if (button != null)
            button.IsVisible = button2->OwnerNode->IsVisible();
    }

    private static void Enqueue(TaskHelper taskHelper)
    {
        taskHelper.Abort();

        var currentCID = AgentLookingForGroup.Instance()->ListingContentId;
        if (currentCID == 0) return;

        if (LocalPlayerState.IsInAnyParty)
        {
            taskHelper.Enqueue
            (() =>
                {
                    if (!Throttler.Shared.Throttle("FastJoinAnotherPartyRecruitment-Task", 100)) return false;
                    if (!LocalPlayerState.IsInAnyParty) return true;

                    ChatManager.Instance().SendMessage("/leave");
                    ChatManager.Instance().SendMessage("/pcmd breakup");
                    AgentId.PartyMember.SendEvent(0, 2, 3);

                    return !LocalPlayerState.IsInAnyParty;
                }
            );
        }

        taskHelper.Enqueue
        (() =>
            {
                if (!Throttler.Shared.Throttle("FastJoinAnotherPartyRecruitment-Task")) return false;

                var instance = AgentLookingForGroup.Instance();
                if (instance->ListingContentId == currentCID) return true;

                instance->OpenListingByContentId(currentCID);
                return instance->ListingContentId == currentCID;
            }
        );

        taskHelper.Enqueue
        (() =>
            {
                if (!Throttler.Shared.Throttle("FastJoinAnotherPartyRecruitment-Task")) return false;
                if (!LookingForGroupDetail->IsAddonAndNodesReady()) return false;

                var buttonNode = LookingForGroupDetail->GetComponentButtonById(109);
                if (buttonNode == null) return false;

                buttonNode->Click();
                return true;
            }
        );

        // 滞留 500 毫秒避免点不了
        taskHelper.DelayNext(500);
    }
}
