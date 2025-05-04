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

    private static readonly Dictionary<ulong, uint> CIDToListingID = [];
    
    public override void Init()
    {
        TaskHelper    ??= new() { TimeLimitMS = 10_000 };
        Overlay       ??= new(this);
        Overlay.Flags |=  ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove;
        
        CIDToListingID.Clear();
        DService.PartyFinder.ReceiveListing += OnListing;
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "LookingForGroupDetail", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LookingForGroupDetail", OnAddon);
        if (IsAddonAndNodesReady(LookingForGroupDetail)) OnAddon(AddonEvent.PostDraw, null);
        
        if (IsAddonAndNodesReady(LookingForGroup)) SendEvent(AgentId.LookingForGroup, 1, 17);
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

        var buttonNode = addon->GetButtonNodeById(109);
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

    private static void OnListing(IPartyFinderListing listing, IPartyFinderListingEventArgs args) 
        => CIDToListingID[listing.ContentId] = listing.Id;

    private void Enqueue()
    {
        TaskHelper.Abort();
        
        var currentCID = AgentLookingForGroup.Instance()->ListingContentId;
        if (currentCID == 0) return;

        if (!CIDToListingID.TryGetValue(currentCID, out var currentListingID)) return;
        
        if (IsInAnyParty())
        {
            TaskHelper.Enqueue(() =>
            {
                if (!Throttler.Throttle("FastJoinAnotherPartyRecruitment-Task", 100)) return false;
                if (!IsInAnyParty()) return true;
                
                ChatHelper.Instance.SendMessage("/leave");
                return !IsInAnyParty();
            });
        }
        
        TaskHelper.Enqueue(() =>
        {
            if (!Throttler.Throttle("FastJoinAnotherPartyRecruitment-Task", 100)) return false;
            if (AgentLookingForGroup.Instance()->ListingContentId == currentCID) return true;
            
            AgentLookingForGroup.Instance()->OpenListing(currentListingID);
            return AgentLookingForGroup.Instance()->ListingContentId == currentCID;
        });
        
        TaskHelper.Enqueue(() =>
        {
            if (!Throttler.Throttle("FastJoinAnotherPartyRecruitment-Task", 100)) return false;
            
            var buttonNode = LookingForGroupDetail->GetButtonNodeById(109);
            if (buttonNode == null) return false;

            buttonNode->ClickAddonButton(LookingForGroupDetail);
            return true;
        });
        
        TaskHelper.Enqueue(() => ClickSelectYesnoYes());
    }

    private static bool IsInAnyParty() => 
        InfoProxyCrossRealm.IsCrossRealmParty() || DService.PartyList.Length >= 2;

    public override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        DService.PartyFinder.ReceiveListing -= OnListing;
        CIDToListingID.Clear();
        
        base.Uninit();
    }
}
