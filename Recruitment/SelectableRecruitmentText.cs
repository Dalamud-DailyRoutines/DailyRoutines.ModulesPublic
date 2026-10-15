using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.Expansion.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.KamiToolKit.Nodes;

namespace DailyRoutines.ModulesPublic;

public unsafe class SelectableRecruitmentText : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("SelectableRecruitmentTextTitle"),
        Description = Lang.Get("SelectableRecruitmentTextDescription"),
        Category    = ModuleCategory.Recruitment,
        PreviewImageURL =
        [
            "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/SelectableRecruitmentText/preview-1.png"
        ]
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private TextMultiLineInputNode? recruitmentTextNode;
    private ulong                   lastViewedListingID;

    protected override void Init()
    {
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostDraw,    "LookingForGroupDetail", OnAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize, "LookingForGroupDetail", OnAddon);

        if (LookingForGroupDetail->IsAddonAndNodesReady())
            OnAddon(AddonEvent.PostRefresh, null);
    }

    protected override void Uninit()
    {
        IAddonLifecycle.Instance().UnregisterListener(OnAddon);

        recruitmentTextNode?.Dispose();
        recruitmentTextNode = null;
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
                recruitmentTextNode = null;
                lastViewedListingID = 0;
                break;

            case AddonEvent.PostDraw:
                var addon = (AddonLookingForGroupDetail*)LookingForGroupDetail;
                if (addon == null) return;

                var origText = addon->DescriptionTextNode;
                if (origText == null) return;

                var origButton = addon->GetComponentButtonById(18);
                if (origButton == null) return;

                var textNodeContainer = addon->GetNodeById(19);
                if (textNodeContainer == null) return;
                
                if (lastViewedListingID == AgentLookingForGroup.Instance()->LastViewedListing.ListingId)
                    return;
                lastViewedListingID = AgentLookingForGroup.Instance()->LastViewedListing.ListingId;
                
                origButton->OwnerNode->Timeline = null;
                origButton->OwnerNode->ToggleVisibility(false);
                
                origText->Timeline = null;
                origText->ToggleVisibility(false);
                
                textNodeContainer->Timeline = null;
                textNodeContainer->ToggleVisibility(false);
                
                recruitmentTextNode?.Dispose();
                recruitmentTextNode = new()
                {
                    AutoUpdateHeight = false,
                    Size             = new(520, 60),
                    Position         = new Vector2(origButton->OwnerNode->X, origButton->OwnerNode->Y) - new Vector2(10, 8),
                    ShowLimitText    = false
                };
                recruitmentTextNode.Component->EnableCompletion = true;
                recruitmentTextNode.CurrentTextNode.TextFlags |= TextFlags.WordWrap;
                recruitmentTextNode.Component->SetText(addon->RawDescription);
                
                recruitmentTextNode.AttachNode(&addon->AtkUnitBase);
                break;
        }
    }
}
