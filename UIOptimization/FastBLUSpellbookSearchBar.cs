using System.Numerics;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using KamiToolKit.Nodes;

namespace DailyRoutines.ModulesPublic;

public unsafe class FastBLUSpellbookSearchBar : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("FastBLUSpellbookSearchBarTitle"),
        Description = GetLoc("FastBLUSpellbookSearchBarDescription"),
        Category    = ModuleCategories.UIOptimization,
    };

    private static string SearchBarInput = string.Empty;
    
    private static TextInputNode SearchBarNode;

    protected override void Init()
    {
        TaskHelper    ??= new();
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "AOZNotebook", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "AOZNotebook", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "AOZNotebook", OnAddon);
        if (IsAddonAndNodesReady(AOZNotebook)) 
            OnAddon(AddonEvent.PostSetup, null);
    }

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        var addon = AOZNotebook;
        if (addon == null) return;

        if (type == AddonEvent.PostSetup)
        {
            ConductSearch(SearchBarInput);

            var component = AOZNotebook->GetComponentNodeById(123);
            if (component == null) return;

            var windowTitleMain = component->GetComponent()->UldManager.SearchNodeById(3);
            if (windowTitleMain != null)
                windowTitleMain->ToggleVisibility(false);
            
            var windowTitleSub = component->GetComponent()->UldManager.SearchNodeById(4);
            if (windowTitleSub != null)
                windowTitleSub->ToggleVisibility(false);
            
            SearchBarNode = CreateSearchNode();
            Service.AddonController.AttachNode(SearchBarNode, AOZNotebook->GetComponentNodeById(123));
        }

        if (type == AddonEvent.PreFinalize)
            Service.AddonController.DetachNode(SearchBarNode);

        if (type == AddonEvent.PostDraw && SearchBarNode != null)     
            SearchBarNode.IsVisible = addon->AtkValues->Int < 9;
    }

    private TextInputNode CreateSearchNode()
    {
        var node = new TextInputNode
        {
            IsVisible     = true,
            Position      = new(40, 35),
            Size          = new(200.0f, 35f),
            MaxCharacters = 20,
            ShowLimitText = true,
            OnInputReceived = x =>
            {
                SearchBarInput = x.TextValue;
                ConductSearch(SearchBarInput);
            },
            OnInputComplete = x =>
            {
                SearchBarInput = x.TextValue;
                ConductSearch(SearchBarInput);
            },
        };

        node.CurrentTextNode.FontSize =  14;
        node.CurrentTextNode.Position += new Vector2(0, 3);
        return node;
    }

    private void ConductSearch(string input)
    {
        TaskHelper.Enqueue(() =>
        {
            var addon = AOZNotebook;
            if (addon == null)
            {
                TaskHelper.Abort();
                return true;
            }
            
            if (!IsAddonAndNodesReady(addon)) return false;
            // 非技能页面
            if (addon->AtkValues->Int >= 9)
            {
                TaskHelper.Abort();
                return true;
            }
            
            SendEvent(AgentId.AozNotebook, 2, 0, 0U, input);
            return true;
        });
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        
        Service.AddonController.DetachNode(SearchBarNode);
        
        base.Uninit();
    }
}
