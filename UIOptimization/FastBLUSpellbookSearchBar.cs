using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.Modules;

public unsafe class FastBLUSpellbookSearchBar : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title       = GetLoc("FastBLUSpellbookSearchBarTitle"),
        Description = GetLoc("FastBLUSpellbookSearchBarDescription"),
        Category    = ModuleCategories.UIOptimization,
    };

    private static string SearchBarInput = string.Empty;
    
    public override void Init()
    {
        TaskHelper ??= new();
        Overlay    ??= new(this);
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "AOZNotebook", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "AOZNotebook", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "AOZNotebook", OnAddon);
    }

    public override void OverlayUI()
    {
        var addon = GetAddonByName("AOZNotebook");
        if (addon == null)
        {
            Overlay.IsOpen = false;
            return;
        }
        
        if (!IsAddonAndNodesReady(addon)) return;

        var windowComponent = addon->GetNodeById(123);
        if (windowComponent == null) return;

        var windowTextNode = ((AtkComponentNode*)windowComponent)->Component->GetTextNodeById(3)->GetAsAtkTextNode();
        if (windowTextNode == null) return;
        
        ImGui.SetWindowPos(new(windowTextNode->ScreenX - 8, windowTextNode->ScreenY - 8));
        
        if (ImGui.InputText("###SearchBar", ref SearchBarInput, 128))
        {
            if (Throttler.Throttle($"FastBLUSpellbookSearchBar-Search-{SearchBarInput}"))
                ConductSearch(SearchBarInput);
        }
        
        if (ImGui.IsItemDeactivatedAfterEdit())
            ConductSearch(SearchBarInput);
    }

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        var addon = args.Addon.ToAtkUnitBase();
        if (addon == null) return;
        
        Overlay.IsOpen = type switch
        {
            AddonEvent.PostSetup   => true,
            AddonEvent.PreFinalize => false,
            _                      => Overlay.IsOpen
        };

        if (type == AddonEvent.PostSetup)
            ConductSearch(SearchBarInput);

        if (type == AddonEvent.PostDraw && Throttler.Throttle("FastBLUSpellbookSearchBar-Draw"))
        {
            if (((AddonAOZNotebook*)addon)->CursorTarget->NodeId != 4)
                Overlay.IsOpen = false;
            else
                Overlay.IsOpen = true;
        }
    }

    private void ConductSearch(string input)
    {
        TaskHelper.Enqueue(() =>
        {
            var addon = GetAddonByName("AOZNotebook");
            if (addon == null)
            {
                TaskHelper.Abort();
                return true;
            }
            
            if (!IsAddonAndNodesReady(addon)) return false;
            // 非技能页面
            if (((AddonAOZNotebook*)addon)->CursorTarget->NodeId != 4)
            {
                TaskHelper.Abort();
                return true;
            }
            
            SendEvent(AgentId.AozNotebook, 2, 0, 0U, input);
            return true;
        });
    }

    public override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        base.Uninit();
    }
}
