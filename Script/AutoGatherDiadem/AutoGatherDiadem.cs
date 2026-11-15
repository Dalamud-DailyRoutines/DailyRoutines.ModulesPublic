using DailyRoutines.Common.Info.Models;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Lumina.Excel.Sheets;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic;

internal unsafe partial class AutoGatherDiadem : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoGatherDiademTitle"),
        Description = Lang.Get("AutoGatherDiademDescription"),
        Category    = ModuleCategory.Script,
        ModulesConflict =
        [
            "IgnoreActionTargetBlocked",
            "AutoFateSync"
        ],
        ModulesPrerequisite =
        [
            "FieldEntryCommand",
            "AutoCommenceDuty"
        ],
        ModulesPair = 
        [
            "AutoDiscard",
            "AutoRepair",
            "OptimizedInteraction"
        ],
        PluginsPrerequisite = [PluginPrerequisiteInfo.vnavmesh]
    };

    public override ModuleBehaviour Behaviour { get; } = new()
    {
        NeedAuth = true
    };

    public override string ModuleName => nameof(AutoGatherDiadem);

    private Config config = null!;

    private GatheringItemSelectCombo gatheringItemCombo = null!;
    private ItemSelectCombo          exchangeItemCombo  = null!;

    private readonly StateMachineContext machine = new();

    private string addNewBlacklistMonsterInput = string.Empty;
    private uint   addNewLevelThresholdInput;

    private uint lastLevel;

    private bool isOnAutoGather;

    protected override void Init()
    {
        config = Config.Load(this) ??
                 new()
                 {
                     BlacklistMonsters = [LuminaWrapper.GetBNPCName(2250)],
                     ExchangeItem      = ExchangeItems.First()
                 };

        if (config.LevelThresholds.Count == 0)
        {
            config.LevelThresholds = [50, 70, 100];
            config.Save(this);
        }

        gatheringItemCombo = new
        (
            "AutoGatherDiademGatheringItems",
            GATHERING_ITEMS.Select(itemID => LuminaGetter.GetRow<GatheringItem>(itemID).GetValueOrDefault())
        )
        {
            SelectedIDs = config.GatheringItems
        };

        exchangeItemCombo = new
        (
            "ExchangeItemCombo",
            ExchangeItems.Select(item => item.ItemID)
                         .Distinct()
                         .Select(itemID => LuminaGetter.GetRow<Item>(itemID).GetValueOrDefault())
        )
        {
            SelectedID = config.ExchangeItem?.ItemID ?? 0
        };

        Overlay            ??= new(this);
        Overlay.WindowName =   $"{Info.Title}###AutoGatherDiadem";
        Overlay.Flags = ImGuiWindowFlags.NoScrollbar |
                        ImGuiWindowFlags.NoTitleBar  |
                        ImGuiWindowFlags.AlwaysAutoResize;

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup, "Gathering", OnGatheringAddon);

        ICondition.Instance().ConditionChange += OnConditionChanged;

        IClientState.Instance().TerritoryChanged += OnZoneChanged;

        GameState.Instance().Login += OnLogin;

        FrameworkManager.Instance().Reg(OnUpdate_StateMachine, 100);
    }

    protected override void Uninit()
    {
        FrameworkManager.Instance().Unreg(OnUpdate_StateMachine);

        GameState.Instance().Login -= OnLogin;

        IClientState.Instance().TerritoryChanged -= OnZoneChanged;

        ICondition.Instance().ConditionChange -= OnConditionChanged;

        IAddonLifecycle.Instance().UnregisterListener(OnGatheringAddon);

        StopAutomation();
    }

    private class Config : ModuleConfig
    {
        public string AutoDiscardGroupName = string.Empty;

        public HashSet<string> BlacklistMonsters = [];
        public HashSet<uint>   BlacklistWeathers = [];

        public ShopExchangeItem? ExchangeItem;
        public bool              IgnoreSkillPoints;

        public HashSet<uint> GatheringItems  = [];
        public List<uint>    LevelThresholds = [];

        public GatheringMode Mode        = GatheringMode.冲分;
        public uint          RoundMinute = 40;

        public bool UseCordial;
    }
}
