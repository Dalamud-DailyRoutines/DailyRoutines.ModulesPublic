using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.Expansion.FFXIV.Client.UI;
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
            "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/SelectableRecruitmentText/preview-1.webp"
        ]
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private TextMultiLineInputNode? inputNode;

    protected override void Init()
    {
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostRefresh, "LookingForGroupDetail", OnAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize, "LookingForGroupDetail", OnAddon);

        if (LookingForGroupDetail->IsAddonAndNodesReady())
            OnAddon(AddonEvent.PostRefresh, null);
    }

    protected override void Uninit()
    {
        IAddonLifecycle.Instance().UnregisterListener(OnAddon);

        inputNode?.Dispose();
        inputNode = null;
    }

    private void OnAddon
    (
        AddonEvent type,
        AddonArgs? args
    )
    {
        var addonBase = args != null ?
                            (AtkUnitBase*)args.Addon.Address :
                            LookingForGroupDetail;
        if (addonBase == null) return;

        switch (type)
        {
            case AddonEvent.PreFinalize:
                if (inputNode is null ||
                    inputNode.ParentAddon != addonBase)
                    break;

                inputNode = null;
                break;

            case AddonEvent.PostRefresh:
                var addon = (AddonLookingForGroupDetail*)addonBase;

                var descriptionTextNode = addon->DescriptionTextNode;
                if (descriptionTextNode == null) return;

                var joinButton = addon->GetComponentButtonById(18);
                if (joinButton == null) return;

                var textContainerNode = addon->GetNodeById(19);
                if (textContainerNode == null) return;

                var targetNode = joinButton->OwnerNode->ParentNode;
                if (targetNode == null) return;

                joinButton->OwnerNode->Timeline = null;
                joinButton->OwnerNode->ToggleVisibility(false);

                descriptionTextNode->Timeline = null;
                descriptionTextNode->ToggleVisibility(false);

                textContainerNode->Timeline = null;
                textContainerNode->ToggleVisibility(false);

                inputNode?.Dispose();
                inputNode = new()
                {
                    AutoUpdateHeight = false,
                    Size             = new(520, 60),
                    Position         = new Vector2(joinButton->OwnerNode->X, joinButton->OwnerNode->Y) - new Vector2(10, 8),
                    ShowLimitText    = false,
                    String           = [with(addon->RawDescription.AsSpan())]
                };
                inputNode.Component->EnableCompletion =  true;
                inputNode.CurrentTextNode.TextFlags   |= TextFlags.WordWrap;
                inputNode.Component->SetText(addon->RawDescription);

                inputNode.AttachNode(targetNode);
                break;
        }
    }
}
