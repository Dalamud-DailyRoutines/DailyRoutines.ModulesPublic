using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using Lumina.Text.ReadOnly;

namespace DailyRoutines.ModulesPublic;

public unsafe class SelectableRecruitmentText : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title           = Lang.Get("SelectableRecruitmentTextTitle"),
        Description     = Lang.Get("SelectableRecruitmentTextDescription"),
        Category        = ModuleCategory.Recruitment,
        PreviewImageURL = ["https://gh.atmoomen.top/raw.githubusercontent.com/AtmoOmen/StaticAssets/main/DailyRoutines/image/SelectableRecruitmentText-UI.png"] // TODO: 调整仓库
    };
    
    private TextMultiLineInputNode? recruitmentTextNode;

    protected override void Init()
    {
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "LookingForGroupDetail", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LookingForGroupDetail", OnAddon);
    }
    
    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
        OnAddon(AddonEvent.PreFinalize, null);
    }

    private void OnAddon(AddonEvent type, AddonArgs? args)
    {
        switch (type)
        {
            case AddonEvent.PreFinalize:
                recruitmentTextNode?.Dispose();
                recruitmentTextNode = null;

                break;

            case AddonEvent.PostDraw:
                if (!LookingForGroupDetail->IsAddonAndNodesReady()) return;

                var agent = AgentLookingForGroup.Instance();
                if (agent == null) return;

                var origText = LookingForGroupDetail->GetTextNodeById(20);
                if (origText == null) return;

                var origButton = LookingForGroupDetail->GetComponentButtonById(18);
                if (origButton == null) return;

                if (recruitmentTextNode != null)
                {
                    recruitmentTextNode.Position = new Vector2(origButton->OwnerNode->X, origButton->OwnerNode->Y) - new Vector2(10, 8);

                    var formatAddon = (AddonLookingForGroupDetail*)LookingForGroupDetail;

                    var leaderNode = formatAddon->PartyLeaderTextNode;
                    if (leaderNode == null) return;

                    var leaderText = leaderNode->NodeText;
                    if (leaderText.IsEmpty || !leaderText.StringPtr.HasValue) return;

                    if (leaderText.StringPtr.ExtractText() != agent->LastViewedListing.LeaderString)
                        return;

                    if (recruitmentTextNode is { IsFocused: false, String.IsEmpty: true })
                    {
                        var seString = new ReadOnlySeStringSpan(agent->LastViewedListing.Comment).PraseAutoTranslate().ToDalamudString();
                        recruitmentTextNode.String = seString.Encode();
                    }

                    if (recruitmentTextNode is { IsVisible: false, String.IsEmpty: false })
                        recruitmentTextNode.IsVisible = true;

                    return;
                }

                var textNodeContainer = LookingForGroupDetail->GetNodeById(19);
                if (textNodeContainer == null) return;

                origButton->OwnerNode->ToggleVisibility(false);
                origText->ToggleVisibility(false);
                textNodeContainer->ToggleVisibility(false);

                recruitmentTextNode = new()
                {
                    AutoUpdateHeight = false,
                    Size             = new(520, 60),
                    Position         = new Vector2(origButton->OwnerNode->X, origButton->OwnerNode->Y) - new Vector2(10, 8),
                    ShowLimitText    = false,
                    IsVisible        = false,
                    MaxLines         = 2
                };
                recruitmentTextNode.TextLimitsNode.DetachNode();
                recruitmentTextNode.CurrentTextNode.TextFlags |= TextFlags.WordWrap;
                recruitmentTextNode.AttachNode(LookingForGroupDetail);

                break;
        }
    }
}
