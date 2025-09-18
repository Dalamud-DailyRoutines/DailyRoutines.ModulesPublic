using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

namespace DailyRoutines.ModulesPublic;

public unsafe class FastJoinAnotherPartyRecruitment : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("FastJoinAnotherPartyRecruitmentTitle"),
        Description = GetLoc("FastJoinAnotherPartyRecruitmentDescription"),
        Category    = ModuleCategories.UIOptimization
    };
    
    private static TextButtonNode? Button;

    protected override void Init()
    {
        TaskHelper ??= new() { TimeLimitMS = 10_000 };

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "LookingForGroupDetail", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LookingForGroupDetail", OnAddon);
        if (IsAddonAndNodesReady(LookingForGroupDetail)) 
            OnAddon(AddonEvent.PostDraw, null);
        
        if (IsAddonAndNodesReady(LookingForGroup)) 
            SendEvent(AgentId.LookingForGroup, 1, 17);
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnAddonYesno);
    }
    private void OnAddonYesno(AddonEvent type, AddonArgs args)
    {
        if (!TaskHelper.IsBusy) return;
        ClickSelectYesnoYes();
    }

    private void OnAddon(AddonEvent type, AddonArgs? args)
    {
        switch (type)
        {
            case AddonEvent.PostDraw:
                CreateOrUpdateButton(LookingForGroupDetail, TaskHelper);
                break;
            case AddonEvent.PreFinalize:
                Service.AddonController.DetachNode(Button);
                Button = null;
                break;
        }
    }

    private static void CreateOrUpdateButton(AtkUnitBase* addon, TaskHelper taskHelper)
    {
        if (addon == null) return;

        // 团队招募
        var partyCount = addon->AtkValues[19].UInt;
        if (partyCount != 1) return;
        
        // 自己开的招募
        if (AgentLookingForGroup.Instance()->ListingContentId == LocalPlayerState.ContentID) return;
        
        var resNode = addon->GetNodeById(108);
        if (resNode == null) return;
        
        if (Button == null)
        {
            Button = new()
            {
                Size      = new(140, 28),
                Position  = new(100, 0),
                IsVisible = true,
                SeString  = GetLoc("FastJoinAnotherPartyRecruitment-LeaveAndJoin"),
                OnClick   = () => Enqueue(taskHelper),
            };

            Service.AddonController.AttachNode(Button, resNode);
        }
        
        resNode->SetPositionFloat(35, 56);
        
        var button0 = addon->GetComponentButtonById(109);
        var button1 = addon->GetComponentButtonById(110);
        var button2 = addon->GetComponentButtonById(111);
        if (button0 == null || button1 == null || button2 == null) return;
        
        button0->OwnerNode->SetPositionFloat(-50, 0);
        button1->OwnerNode->SetPositionFloat(250, 0);
        button2->OwnerNode->SetPositionFloat(400, 0);
    }
    
    private static void Enqueue(TaskHelper taskHelper)
    {
        taskHelper.Abort();
        
        var currentCID = AgentLookingForGroup.Instance()->ListingContentId;
        if (currentCID == 0) return;
        
        if (IsInAnyParty())
        {
            taskHelper.Enqueue(() =>
            {
                if (!Throttler.Throttle("FastJoinAnotherPartyRecruitment-Task", 100)) return false;
                if (!IsInAnyParty()) return true;
                
                ChatHelper.SendMessage("/leave");
                ChatHelper.SendMessage("/pcmd breakup");
                SendEvent(AgentId.PartyMember, 0, 2, 3);
                
                return !IsInAnyParty();
            });
        }
        
        taskHelper.Enqueue(() =>
        {
            if (!Throttler.Throttle("FastJoinAnotherPartyRecruitment-Task")) return false;
            
            var instance = AgentLookingForGroup.Instance();
            if (instance->ListingContentId == currentCID) return true;
            
            instance->OpenListingByContentId(currentCID);
            return instance->ListingContentId == currentCID;
        });
        
        taskHelper.Enqueue(() =>
        {
            if (!Throttler.Throttle("FastJoinAnotherPartyRecruitment-Task")) return false;
            if (!IsAddonAndNodesReady(LookingForGroupDetail)) return false;
            
            var buttonNode = LookingForGroupDetail->GetComponentButtonById(109);
            if (buttonNode == null) return false;

            buttonNode->ClickAddonButton(LookingForGroupDetail);
            return true;
        });
        
        // 滞留 500 毫秒避免点不了
        taskHelper.DelayNext(500);
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        DService.AddonLifecycle.UnregisterListener(OnAddonYesno);
        
        Service.AddonController.DetachNode(Button);
        Button = null;
        
        base.Uninit();
    }
}
