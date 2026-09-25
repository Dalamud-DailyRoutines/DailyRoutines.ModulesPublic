using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.KamiToolKit.Addons.SelectYesno;
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

    private SelectYesnoAddon? confirmAddon;

    private TextButtonNode? button;

    protected override void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 10_000 };

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreDraw,     "LookingForGroupDetail", OnAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreRefresh,  "LookingForGroupDetail", OnAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize, "LookingForGroupDetail", OnAddon);
        if (LookingForGroupDetail->IsAddonAndNodesReady())
            OnAddon(AddonEvent.PreRefresh, null);

        if (LookingForGroup->IsAddonAndNodesReady())
            AgentId.LookingForGroup.SendEvent(1, 17);

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnAddonYesno);
    }

    protected override void Uninit()
    {
        IAddonLifecycle.Instance().UnregisterListener(OnAddon, OnAddonYesno);

        button?.Dispose();
        button = null;

        confirmAddon?.Dispose();
        confirmAddon = null;
    }

    private void OnAddonYesno
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        if (!TaskHelper.IsBusy) return;
        AddonSelectYesnoEvent.ClickYes();
    }

    private void OnAddon
    (
        AddonEvent type,
        AddonArgs? args
    )
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
                button = null;
                break;
        }
    }

    private void CreateButton
    (
        AtkUnitBase* addon,
        TaskHelper   taskHelper
    )
    {
        if (addon == null || button != null) return;

        // 团队招募
        var partyCount = addon->AtkValues[19].UInt;
        if (partyCount != 1) return;

        var agent = AgentLookingForGroup.Instance();

        // 自己开的招募
        if (agent->ListingContentId == LocalPlayerState.ContentID) return;

        // 底部操作栏容器
        var containerNode = addon->GetNodeById(108);
        if (containerNode == null) return;

        button = new()
        {
            Size      = new(140, 28),
            Position  = new(100, 0),
            IsVisible = false,
            IsEnabled = LocalPlayerState.IsInAnyParty,
            String    = Lang.Get("FastJoinAnotherPartyRecruitment-LeaveAndJoin"),
            OnClick = () =>
            {
                confirmAddon?.Dispose();

                using var rented  = new RentedSeStringBuilder();
                var       builder = rented.Builder;

                var listing = agent->LastViewedListing;

                builder.Append(listing.LeaderString);

                if (listing.HomeWorld != GameState.HomeWorld)
                {
                    builder.AppendIcon(BitmapFontIcon.CrossWorld)
                           .Append(LuminaWrapper.GetWorldName(listing.HomeWorld));
                }

                confirmAddon = SelectYesnoAddon.Open
                (
                    new()
                    {
                        Prompt = ISeStringEvaluator.Instance().EvaluateFromAddon
                        (
                            120,
                            [builder.ToReadOnlySeString()]
                        ),
                        BlockedParentID = addon->Id,
                        ParentID        = addon->Id,
                        Position = new
                        (
                            addon->RootNode->GetNodeState().Center,
                            AddonPositionAlignment.TopCenter
                        ),
                        Callback = (_, result) =>
                        {
                            confirmAddon = null;

                            if (result != SelectYesnoAddonResult.Yes)
                                return;

                            Enqueue(taskHelper);
                        }
                    }
                );
            }
        };

        button.AttachNode(containerNode);
    }

    private void UpdateOtherButtons
    (
        AtkUnitBase* addon
    )
    {
        if (addon == null) return;

        // 团队招募
        var partyCount = addon->AtkValues[19].UInt;
        if (partyCount != 1) return;

        // 自己开的招募
        if (AgentLookingForGroup.Instance()->ListingContentId == LocalPlayerState.ContentID) return;

        var containerNode = addon->GetNodeById(108);
        if (containerNode == null) return;

        var button0 = addon->GetComponentButtonById(109);
        var button1 = addon->GetComponentButtonById(110);
        var button2 = addon->GetComponentButtonById(111);
        if (button0 == null || button1 == null || button2 == null) return;

        // 在 ULD 中带有 Float2 Position 关键帧, 引擎每帧都会用关键帧插值回写节点坐标,
        // 只有摘掉节点的 Timeline 才能让坐标立即生效
        DetachTimelineAndSetPosition(containerNode,                   35,  56);
        DetachTimelineAndSetPosition((AtkResNode*)button0->OwnerNode, -50, 0);
        DetachTimelineAndSetPosition((AtkResNode*)button1->OwnerNode, 250, 0);
        DetachTimelineAndSetPosition((AtkResNode*)button2->OwnerNode, 400, 0);

        if (button != null)
        {
            button.IsEnabled = LocalPlayerState.IsInAnyParty;
            button.IsVisible = button2->OwnerNode->IsVisible();
        }
    }

    private static void DetachTimelineAndSetPosition
    (
        AtkResNode* node,
        float       x,
        float       y
    )
    {
        node->Timeline = null;
        node->SetPositionFloat(x, y);
    }

    private static void Enqueue
    (
        TaskHelper taskHelper
    )
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
