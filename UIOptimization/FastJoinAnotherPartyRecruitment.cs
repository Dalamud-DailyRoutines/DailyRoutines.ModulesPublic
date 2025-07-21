using System.Collections.Generic;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.PartyFinder.Types;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public unsafe class FastJoinAnotherPartyRecruitment : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("FastJoinAnotherPartyRecruitmentTitle"),
        Description = GetLoc("FastJoinAnotherPartyRecruitmentDescription"),
        Category    = ModuleCategories.UIOptimization
    };

    protected override void Init()
    {
        TaskHelper    ??= new() { TimeLimitMS = 10_000 };
        Overlay       ??= new(this);
        Overlay.Flags |=  ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove;
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "LookingForGroupDetail", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LookingForGroupDetail", OnAddon);
        if (IsAddonAndNodesReady(LookingForGroupDetail)) 
            OnAddon(AddonEvent.PostDraw, null);
        
        if (IsAddonAndNodesReady(LookingForGroup)) 
            SendEvent(AgentId.LookingForGroup, 1, 17);
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnAddonYesno);
    }

    protected override void OverlayUI()
    {
        var addon = LookingForGroupDetail;
        if (addon == null)
        {
            Overlay.IsOpen = false;
            return;
        }

        var buttonNode = addon->GetComponentButtonById(109);
        if (buttonNode == null) return;

        if (!IsInAnyParty()                                            ||
            buttonNode->ButtonTextNode                         == null ||
            buttonNode->ButtonTextNode->NodeText.ExtractText() != LuminaWrapper.GetAddonText(2219))
        {
            Overlay.IsOpen = false;
            return;
        }

        var windowSize = ImGui.GetWindowSize();
        var nodeState  = NodeState.Get((AtkResNode*)buttonNode->OwnerNode);
        
        ImGui.SetWindowPos(new(nodeState.Position.X + ((nodeState.Size.X - windowSize.X) / 2), nodeState.Position.Y - windowSize.Y + 4f));
        if (ImGui.Button(GetLoc("FastJoinAnotherPartyRecruitment-LeaveAndJoin")))
            Enqueue();
    }

    private void OnAddonYesno(AddonEvent type, AddonArgs args)
    {
        if (!TaskHelper.IsBusy) return;
        ClickSelectYesnoYes();
    }

    private void OnAddon(AddonEvent type, AddonArgs? args)
    {
        Overlay.IsOpen = type switch
        {
            AddonEvent.PostDraw    => true,
            AddonEvent.PreFinalize => false,
            _                      => Overlay.IsOpen
        };
    }
    
    private void Enqueue()
    {
        TaskHelper.Abort();
        
        var currentCID = AgentLookingForGroup.Instance()->ListingContentId;
        if (currentCID == 0) return;
        
        if (IsInAnyParty())
        {
            TaskHelper.Enqueue(() =>
            {
                if (!Throttler.Throttle("FastJoinAnotherPartyRecruitment-Task", 100)) return false;
                if (!IsInAnyParty()) return true;
                
                ChatHelper.SendMessage("/leave");
                ChatHelper.SendMessage("/pcmd breakup");
                SendEvent(AgentId.PartyMember, 0, 2, 3);
                
                return !IsInAnyParty();
            });
        }
        
        TaskHelper.Enqueue(() =>
        {
            if (!Throttler.Throttle("FastJoinAnotherPartyRecruitment-Task")) return false;
            
            var instance = AgentLookingForGroup.Instance();
            if (instance->ListingContentId == currentCID) return true;

            instance->OpenListingByContentId(currentCID);
            return instance->ListingContentId == currentCID;
        });
        
        TaskHelper.Enqueue(() =>
        {
            if (!Throttler.Throttle("FastJoinAnotherPartyRecruitment-Task")) return false;
            if (!IsAddonAndNodesReady(LookingForGroupDetail)) return false;
            
            var buttonNode = LookingForGroupDetail->GetComponentButtonById(109);
            if (buttonNode == null) return false;

            buttonNode->ClickAddonButton(LookingForGroupDetail);
            return true;
        });
        
        // 滞留 500 毫秒避免点不了
        TaskHelper.DelayNext(500);
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        DService.AddonLifecycle.UnregisterListener(OnAddonYesno);
        
        base.Uninit();
    }
}
