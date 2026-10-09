using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.CrossDCPartyFinder;

public partial class CrossDCPartyFinder
{
    private RadioButtonGroupNode? dataCenterGroup;

    private unsafe void OnAddon
    (
        AddonEvent type,
        AddonArgs? args
    )
    {
        ClearResources();

        dataCenters =
        [
            .. LuminaGetter.Get<WorldDCGroupType>()
                           .Where(x => x.Region.RowId == GameState.HomeDataCenterData.Region.RowId)
                           .Select(x => x.Name.ToString())
        ];
        selectedDataCenter = GameState.CurrentDataCenterData.Name.ToString();

        switch (type)
        {
            case AddonEvent.PostSetup:
                Overlay.IsOpen = true;

                dataCenterGroup = new()
                {
                    Position                   = new(85, 14),
                    Height                     = 28,
                    ItemSpacing                = 4f,
                    LayoutOrientation          = LayoutOrientation.Horizontal,
                    SelectFirstButtonByDefault = false,
                    FitToContentWidth          = true
                };

                foreach (var dataCenter in dataCenters)
                {
                    var radioButton = new RadioButtonNode
                    {
                        Height      = 16.0f,
                        String      = dataCenter,
                        TextTooltip = $"查看{dataCenter}大区的招募信息",
                        Callback = () =>
                        {
                            selectedDataCenter = dataCenter;

                            if (LocatedDataCenter == dataCenter)
                            {
                                AgentId.LookingForGroup.SendEvent(1, 17);
                                return;
                            }

                            SendRequestDynamic();
                            isNeedToDisable = true;
                        }
                    };

                    var labelSize = radioButton.LabelNode.GetTextDrawSize(considerScale: false);
                    radioButton.LabelNode.Size = new Vector2(MathF.Ceiling(labelSize.X), radioButton.Height);
                    radioButton.Width          = radioButton.LabelNode.X + radioButton.LabelNode.Width;
                    
                    dataCenterGroup.AddButton(radioButton);
                }

                dataCenterGroup.SelectedOption = selectedDataCenter;

                dataCenterGroup.AttachNode(LookingForGroup->GetComponentNodeById(51));
                break;
            case AddonEvent.PreFinalize:
                Overlay.IsOpen  = false;
                dataCenterGroup = null;
                break;
        }
    }
}
