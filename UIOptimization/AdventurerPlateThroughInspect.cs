using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using KamiToolKit.Nodes;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models.Packets.Upstream;

namespace DailyRoutines.ModulesPublic;

public unsafe class AdventurerPlateThroughInspect : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AdventurerPlateThroughInspectTitle"),
        Description = Lang.Get("AdventurerPlateThroughInspectDescription"),
        Category    = ModuleCategory.UIOptimization
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private IconButtonNode? openButton;

    protected override void Init()
    {
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "CharacterInspect", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "CharacterInspect", OnAddon);
        if (CharacterInspect->IsAddonAndNodesReady())
            OnAddon(AddonEvent.PostSetup, null);
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
            case AddonEvent.PostDraw:
                if (CharacterInspect == null) return;

                if (openButton == null)
                {
                    openButton = new()
                    {
                        Size        = new(36f),
                        IsVisible   = true,
                        IsEnabled   = true,
                        IconId      = 66469,
                        OnClick     = () => new CharaCardOpenPacket(AgentInspect.Instance()->CurrentEntityId).Send(),
                        TextTooltip = LuminaWrapper.GetAddonText(15083),
                        Position    = new(298, 86)
                    };
                    openButton.AttachNode(CharacterInspect->RootNode);
                }

                break;
            case AddonEvent.PreFinalize:
                openButton?.Dispose();
                openButton = null;
                break;
        }
    }
}
