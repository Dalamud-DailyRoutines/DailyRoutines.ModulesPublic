using System.Numerics;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using Lumina.Text.ReadOnly;

namespace DailyRoutines.ModulesPublic;

public unsafe class SelectableRecruitmentText : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title           = GetLoc("SelectableRecruitmentTextTitle"),
        Description     = GetLoc("SelectableRecruitmentTextDescription"),
        Category        = ModuleCategories.Recruitment,
        PreviewImageURL = ["https://gh.atmoomen.top/raw.githubusercontent.com/AtmoOmen/StaticAssets/main/DailyRoutines/image/SelectableRecruitmentText-UI.png"]
    };
    
    private static TextMultiLineInputNode? RecruitmentTextNode;
    
    protected override void Init()
    {
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreDraw,     "LookingForGroupDetail", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LookingForGroupDetail", OnAddon);
    }
    
    private static void OnAddon(AddonEvent type, AddonArgs? args)
    {
        switch (type)
        {
            case AddonEvent.PreFinalize:
                RecruitmentTextNode?.Dispose();
                RecruitmentTextNode = null;
                
                break;
            
            case AddonEvent.PreDraw:
                if (!LookingForGroupDetail->IsAddonAndNodesReady()) return;

                var agent = AgentLookingForGroup.Instance();
                if (agent == null) return;
                
                var origText = LookingForGroupDetail->GetTextNodeById(20);
                if (origText == null) return;
                
                var origButton = LookingForGroupDetail->GetComponentButtonById(18);
                if (origButton == null) return;
                
                if (RecruitmentTextNode != null)
                {
                    RecruitmentTextNode.Position = new Vector2(origButton->OwnerNode->X, origButton->OwnerNode->Y) - new Vector2(10, 8);
                    
                    var formatAddon = (AddonLookingForGroupDetail*)LookingForGroupDetail;
                    if (formatAddon->PartyLeaderTextNode->NodeText.StringPtr.ExtractText() !=
                        agent->LastViewedListing.LeaderString)
                        return;
                    
                    if (RecruitmentTextNode is { IsFocused: false, String.IsEmpty: true })
                    {
                        var seString = new ReadOnlySeStringSpan(agent->LastViewedListing.Comment).PraseAutoTranslate().ToDalamudString();
                        RecruitmentTextNode.String = seString.Encode();
                    }
                    
                    if (RecruitmentTextNode is { IsVisible: false, String.IsEmpty: false })
                        RecruitmentTextNode.IsVisible = true;
                    
                    return;
                }

                var textNodeContainer = LookingForGroupDetail->GetNodeById(19);
                if (textNodeContainer == null) return;

                origButton->OwnerNode->ToggleVisibility(false);
                origText->ToggleVisibility(false);
                textNodeContainer->ToggleVisibility(false);

                RecruitmentTextNode = new()
                {
                    AutoUpdateHeight = false,
                    Size             = new(520, 60),
                    Position         = new Vector2(origButton->OwnerNode->X, origButton->OwnerNode->Y) - new Vector2(10, 8),
                    ShowLimitText    = false,
                    IsVisible        = false,
                    MaxLines         = 2,
                };
                RecruitmentTextNode.TextLimitsNode.DetachNode();
                RecruitmentTextNode.CurrentTextNode.TextFlags |= TextFlags.WordWrap;
                RecruitmentTextNode.AttachNode(LookingForGroupDetail);

                break;
        }
    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
        OnAddon(AddonEvent.PreFinalize, null);
    }
}
