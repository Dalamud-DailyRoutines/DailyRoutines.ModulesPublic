using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using KamiToolKit.Nodes;

namespace DailyRoutines.ModulesPublic;

public unsafe class AdventurerPlateThroughInspect : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AdventurerPlateThroughInspectTitle"),
        Description = GetLoc("AdventurerPlateThroughInspectDescription"),
        Category    = ModuleCategories.UIOptimization
    };

    private static IconButtonNode? OpenButton;

    protected override void Init()
    {
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "CharacterInspect", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "CharacterInspect", OnAddon);
        if (IsAddonAndNodesReady(CharacterInspect)) 
            OnAddon(AddonEvent.PostSetup, null);
    }

    private static void OnAddon(AddonEvent type, AddonArgs? args)
    {
        switch (type)
        {
            case AddonEvent.PostDraw:
                if (CharacterInspect == null) return;

                if (OpenButton == null)
                {
                    OpenButton = new()
                    {
                        Size      = new(36f),
                        IsVisible = true,
                        IsEnabled = true,
                        IconId    = 66469,
                        OnClick   = () => new CharaCardOpenPacket(AgentInspect.Instance()->CurrentEntityId).Send(),
                        Tooltip   = LuminaWrapper.GetAddonText(15083),
                        Position  = new(298, 86)
                    };
                    Service.AddonController.AttachNode(OpenButton, CharacterInspect->RootNode);
                }
                
                break;
            case AddonEvent.PreFinalize:
                Service.AddonController.DetachNode(OpenButton);
                OpenButton = null;
                break;
        }
    }
    
    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        OnAddon(AddonEvent.PreFinalize, null);
    }
}
