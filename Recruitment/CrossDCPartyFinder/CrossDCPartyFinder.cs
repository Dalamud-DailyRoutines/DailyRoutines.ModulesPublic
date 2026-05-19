using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Agent;
using Dalamud.Game.Agent.AgentArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.OmenService;
using AgentId = Dalamud.Game.Agent.AgentId;

namespace DailyRoutines.ModulesPublic.CrossDCPartyFinder;

public partial class CrossDCPartyFinder : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "跨大区队员招募",
        Description = "为队员招募界面新增大区切换按钮, 以选择并查看由众包网站提供的其他大区的招募信息",
        Category    = ModuleCategory.Recruitment,
        PreviewImageURL =
        [
            "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/CrossDCPartyFinder/preview-1.png"
        ]
    };

    public override ModulePermission Permission { get; } = new() { CNOnly = true, CNDefaultEnabled = true };

    private static string LocatedDataCenter =>
        GameState.CurrentDataCenterData.Name.ToString();

    private Config config = null!;

    private List<string>                             dataCenters     = [];
    private List<PartyFinderList.PartyFinderListing> listings        = [];
    private List<PartyFinderList.PartyFinderListing> listingsDisplay = [];

    private CancellationTokenSource? cancelSource;

    private DateTime           lastUpdate = DateTime.MinValue;
    private bool               isNeedToDisable;
    private PartyFinderRequest lastRequest  = new();
    private string             currentSeach = string.Empty;
    private int                currentPage;
    private string             selectedDataCenter = string.Empty;

    protected override unsafe void Init()
    {
        config        =   Config.Load(this) ?? new();
        Overlay       ??= new(this);
        Overlay.Flags |=  ImGuiWindowFlags.NoBackground;

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "LookingForGroup", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LookingForGroup", OnAddon);
        if (LookingForGroup->IsAddonAndNodesReady())
            OnAddon(AddonEvent.PostSetup, null);

        DService.Instance().AgentLifecycle.RegisterListener(AgentEvent.PostReceiveEvent, AgentId.LookingForGroup, OnAgent);
    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
        DService.Instance().AgentLifecycle.UnregisterListener(OnAgent);

        ClearResources();

        ClearNodes();
    }

    private unsafe void OnAgent(AgentEvent type, AgentArgs args)
    {
        var agent = args.Agent.ToStruct<AgentLookingForGroup>();
        if (agent == null) return;

        var formatted = args as AgentReceiveEventArgs;
        var atkValues = (AtkValue*)formatted.AtkValues;

        if (selectedDataCenter != LocatedDataCenter)
        {
            // 招募类别刷新
            if (formatted is { EventKind: 1, ValueCount: 3 } && atkValues[1].Type == AtkValueType.UInt)
                SendRequestDynamic();

            // 招募刷新
            if (formatted is { EventKind: 1, ValueCount: 1 } && atkValues[0].Type == AtkValueType.Int && atkValues[0].Int == 17)
                SendRequestDynamic();
            
            // 升序、降序变化
            if (formatted is { EventKind: 1, ValueCount: 3 } && atkValues[0].Type == AtkValueType.Int && atkValues[0].Int == 24)
                SendRequestDynamic();
        }
    }

    private class Config : ModuleConfig
    {
        public int PageSize = 100;
    }
}
