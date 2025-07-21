using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public unsafe class AdventurerPlateThroughInspect : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AdventurerPlateThroughInspectTitle"),
        Description = GetLoc("AdventurerPlateThroughInspectDescription"),
        Category    = ModuleCategories.UIOptimization
    };

    protected override void Init()
    {
        Overlay       ??= new(this);
        Overlay.Flags |=  ImGuiWindowFlags.NoBackground;
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "CharacterInspect", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "CharacterInspect", OnAddon);
        if (IsAddonAndNodesReady(CharacterInspect)) 
            OnAddon(AddonEvent.PostSetup, null);
    }

    private void OnAddon(AddonEvent type, AddonArgs? args) =>
        Overlay.IsOpen = type switch
        {
            AddonEvent.PostSetup   => true,
            AddonEvent.PreFinalize => false,
            _                      => Overlay.IsOpen
        };

    protected override void OverlayUI()
    {
        var addon = CharacterInspect;
        if (addon == null)
        {
            Overlay.IsOpen = false;
            return;
        }

        var baseNode = addon->GetNodeById(21);
        if (baseNode == null) return;

        var nodeState = NodeState.Get(baseNode);
        ImGui.SetWindowPos(nodeState.Position2 - ImGui.GetWindowSize());
        
        if (!DService.Texture.TryGetFromGameIcon(new(66469), out var texture)) return;
        
        if (ImGui.ImageButton(texture.GetWrapOrEmpty().ImGuiHandle, ScaledVector2(24f)))
            new CharaCardOpenPacket(AgentInspect.Instance()->CurrentEntityId).Send();
        ImGuiOm.TooltipHover($"{LuminaWrapper.GetAddonText(15083)}");
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        base.Uninit();
    }
}
