using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using KamiToolKit.Nodes;

namespace DailyRoutines.ModulesPublic;

public unsafe class FastBLUSpellbookSearchBar : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("FastBLUSpellbookSearchBarTitle"),
        Description = Lang.Get("FastBLUSpellbookSearchBarDescription"),
        Category    = ModuleCategory.UIOptimization
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private TextInputNode? searchBarNode;
    
    private string searchBarInput = string.Empty;

    protected override void Init()
    {
        TaskHelper ??= new();

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "AOZNotebook", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "AOZNotebook", OnAddon);
    }
    
    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
        OnAddon(AddonEvent.PreFinalize, null);
    }

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PreFinalize:
                searchBarNode?.Dispose();
                searchBarNode = null;
                break;
            case AddonEvent.PostDraw:
                if (AOZNotebook == null) return;

                if (searchBarNode == null)
                {
                    ConductSearch(searchBarInput);

                    var component = AOZNotebook->GetComponentNodeById(123);
                    if (component == null) return;

                    var windowTitleMain = component->GetComponent()->UldManager.SearchNodeById(3);
                    if (windowTitleMain != null)
                        windowTitleMain->ToggleVisibility(false);

                    var windowTitleSub = component->GetComponent()->UldManager.SearchNodeById(4);
                    if (windowTitleSub != null)
                        windowTitleSub->ToggleVisibility(false);

                    searchBarNode = new TextInputNode
                    {
                        IsVisible     = true,
                        Position      = new(40, 35),
                        Size          = new(200f, 35f),
                        MaxCharacters = 20,
                        ShowLimitText = true,
                        OnInputReceived = x =>
                        {
                            searchBarInput = x.ToString();
                            ConductSearch(searchBarInput);
                        },
                        OnInputComplete = x =>
                        {
                            searchBarInput = x.ToString();
                            ConductSearch(searchBarInput);
                        }
                    };
                    searchBarNode.CurrentTextNode.FontSize =  14;
                    searchBarNode.CurrentTextNode.Position += new Vector2(0, 3);

                    searchBarNode.AttachNode(component);
                }

                searchBarNode.IsVisible = AOZNotebook->AtkValues->Int < 9;
                break;
        }
    }

    private void ConductSearch(string input)
    {
        TaskHelper.Enqueue
        (() =>
            {
                var addon = AOZNotebook;

                if (addon == null)
                {
                    TaskHelper.Abort();
                    return true;
                }

                if (!addon->IsAddonAndNodesReady()) return false;

                // 非技能页面
                if (addon->AtkValues->Int >= 9)
                {
                    TaskHelper.Abort();
                    return true;
                }

                AgentId.AozNotebook.SendEvent(2, 0, 0U, input);
                return true;
            }
        );
    }
}
