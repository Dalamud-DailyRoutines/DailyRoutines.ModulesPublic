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
    
    private delegate bool OpenListingByContentIDDelegate(AgentLookingForGroup* agent, ulong contentID);
    private static readonly OpenListingByContentIDDelegate? OpenListingByContentIDInfo =
        new CompSig("40 53 48 83 EC 20 48 8B D9 E8 ?? ?? ?? ?? 84 C0 74 07 C6 83 90 31 00 00 01").GetDelegate<OpenListingByContentIDDelegate>();
    
    public override void Init()
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

    public override void OverlayUI()
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

                ClickSelectYesnoYes();
                
                ChatHelper.SendMessage("/leave");
                ChatHelper.SendMessage("/pcmd breakup");
                SendEvent(AgentId.PartyMember, 0, 2, 3);
                
                return !IsInAnyParty();
            });
        }
        
        TaskHelper.Enqueue(() =>
        {
            if (!Throttler.Throttle("FastJoinAnotherPartyRecruitment-Task", 100)) return false;
            if (AgentLookingForGroup.Instance()->ListingContentId == currentCID) return true;
            
            OpenListingByContentIDInfo(AgentLookingForGroup.Instance(), currentCID);
            return AgentLookingForGroup.Instance()->ListingContentId == currentCID;
        });
        
        TaskHelper.Enqueue(() =>
        {
            if (!Throttler.Throttle("FastJoinAnotherPartyRecruitment-Task", 100)) return false;
            
            var buttonNode = LookingForGroupDetail->GetComponentButtonById(109);
            if (buttonNode == null) return false;

            buttonNode->ClickAddonButton(LookingForGroupDetail);
            return true;
        });
        
        TaskHelper.Enqueue(() => ClickSelectYesnoYes());
    }

    public override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        
        base.Uninit();
    }
}
