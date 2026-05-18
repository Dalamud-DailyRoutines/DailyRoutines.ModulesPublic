using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.CrossDCPartyFinder;

public partial class CrossDCPartyFinder
{
    private Dictionary<string, CheckboxNode> checkboxNodes = [];
    private HorizontalListNode?              layoutNode;

    private unsafe void OnAddon(AddonEvent type, AddonArgs? args)
    {
        ClearResources();

        dataCenters = LuminaGetter.Get<WorldDCGroupType>()
                                  .Where(x => x.Region.RowId == GameState.HomeDataCenterData.Region.RowId)
                                  .Select(x => x.Name.ToString())
                                  .ToList();
        selectedDataCenter = GameState.CurrentDataCenterData.Name.ToString();

        switch (type)
        {
            case AddonEvent.PostSetup:
                Overlay.IsOpen = true;

                layoutNode = new()
                {
                    IsVisible = true,
                    Position  = new(85, 8)
                };

                foreach (var dataCenter in dataCenters)
                {
                    var node = new CheckboxNode
                    {
                        Size      = new(100f, 28f),
                        IsVisible = true,
                        IsChecked = dataCenter == selectedDataCenter,
                        IsEnabled = true,
                        String    = dataCenter,
                        OnClick = _ =>
                        {
                            selectedDataCenter = dataCenter;

                            foreach (var x in checkboxNodes)
                                x.Value.IsChecked = x.Key == dataCenter;

                            if (LocatedDataCenter == dataCenter)
                            {
                                AgentId.LookingForGroup.SendEvent(1, 17);
                                return;
                            }

                            SendRequestDynamic();
                            isNeedToDisable = true;
                        }
                    };

                    checkboxNodes[dataCenter] = node;

                    layoutNode.AddNode(node);
                }

                layoutNode.AttachNode(LookingForGroup->GetComponentNodeById(51));
                break;
            case AddonEvent.PreFinalize:
                Overlay.IsOpen = false;
                ClearNodes();
                break;
        }
    }

    private void ClearNodes()
    {
        layoutNode?.Dispose();
        layoutNode = null;

        foreach (var x in checkboxNodes.Values)
            x.Dispose();
        checkboxNodes.Clear();
    }
}
