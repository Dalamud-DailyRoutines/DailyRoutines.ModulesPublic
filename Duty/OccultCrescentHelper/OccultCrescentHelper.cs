using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.Dalamud;
using OmenTools.Info.Game;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Duty;

public partial class OccultCrescentHelper : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title           = Lang.Get("OccultCrescentHelperTitle"),
        Description     = Lang.Get("OccultCrescentHelperDescription"),
        Category        = ModuleCategory.Duty,
        Author          = ["Fragile"],
        ModulesConflict = ["AutoFaceCameraDirection"]
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private Config config = null!;

    private AetheryteManager  aetheryteModule  = null!;
    private EventManager      eventModule      = null!;
    private TreasureManager   treasureModule   = null!;
    private SupportJobManager supportJobModule = null!;
    private OthersManager     othersModule     = null!;

    private List<BaseIslandModule> modules = [];

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        var isConfigChanged = false;

        if (!string.IsNullOrWhiteSpace(config.AutoEnableDisablePlugins))
        {
            var plugins = config.AutoEnableDisablePlugins
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Take(MAX_DUTY_COMMAND_LINES)
                .ToArray();

            config.JoinDutyCommands  = string.Join("\n", plugins.Select(plugin => $"/xlenableplugin {plugin}"));
            config.LeaveDutyCommands = string.Join("\n", plugins.Select(plugin => $"/xldisableplugin {plugin}"));
            config.AutoEnableDisablePlugins = string.Empty;
            isConfigChanged = true;
        }

        var joinDutyCommands = LimitDutyCommandLines(config.JoinDutyCommands);
        if (joinDutyCommands != config.JoinDutyCommands)
        {
            config.JoinDutyCommands = joinDutyCommands;
            isConfigChanged = true;
        }

        var leaveDutyCommands = LimitDutyCommandLines(config.LeaveDutyCommands);
        if (leaveDutyCommands != config.LeaveDutyCommands)
        {
            config.LeaveDutyCommands = leaveDutyCommands;
            isConfigChanged = true;
        }

        if (isConfigChanged)
            config.Save(this);

        Overlay       ??= new(this);
        Overlay.Flags &=  ~ImGuiWindowFlags.AlwaysAutoResize;
        
        aetheryteModule  = new(this);
        eventModule      = new(this);
        treasureModule   = new(this);
        supportJobModule = new(this);
        othersModule     = new(this);

        modules = [aetheryteModule, eventModule, treasureModule, supportJobModule, othersModule];

        foreach (var module in modules)
            module.Init();

        IClientState.Instance().TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
    }

    protected override void Uninit()
    {
        IClientState.Instance().TerritoryChanged -= OnZoneChanged;
        FrameworkManager.Instance().Unreg(OnUpdate);

        foreach (var module in modules)
            module.Uninit();
    }

    private void OnZoneChanged
    (
        uint u
    )
    {
        FrameworkManager.Instance().Unreg(OnUpdate);
        if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent) return;
        FrameworkManager.Instance().Reg(OnUpdate, 1_000);
    }

    private void OnUpdate
    (
        IFramework framework
    )
    {
        if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent)
        {
            FrameworkManager.Instance().Unreg(OnUpdate);
            return;
        }

        foreach (var module in modules)
            module.OnUpdate();
    }

    protected override void ConfigUI()
    {
        using var fontPush = FontManager.Instance().UIFont.Push();
        
        using var tab = ImRaii.TabBar("###Config", ImGuiTabBarFlags.Reorderable);
        if (!tab) return;

        using (var aetheryteTab = ImRaii.TabItem($"{LuminaWrapper.GetEObjName(2014664)}"))
        {
            if (aetheryteTab)
                using (ImRaii.PushId("AetheryteManager"))
                    aetheryteModule.DrawConfig();
        }

        using (var ceTab = ImRaii.TabItem(Lang.Get("DynamicEvent")))
        {
            if (ceTab)
                using (ImRaii.PushId("CEManager"))
                    eventModule.DrawConfig();
        }

        using (var treasureTab = ImRaii.TabItem($"{LuminaWrapper.GetAddonText(395)}"))
        {
            if (treasureTab)
                using (ImRaii.PushId("TreasureManager"))
                    treasureModule.DrawConfig();
        }

        using (var supportJobTab = ImRaii.TabItem($"{LuminaWrapper.GetAddonText(16633)}"))
        {
            if (supportJobTab)
                using (ImRaii.PushId("SupportJobManager"))
                    supportJobModule.DrawConfig();
        }

        using (var othersTab = ImRaii.TabItem($"{LuminaWrapper.GetAddonText(832)}"))
        {
            if (othersTab)
                using (ImRaii.PushId("OthersManager"))
                    othersModule.DrawConfig();
        }
    }

    protected override void OverlayPreDraw() => FontManager.Instance().UIFont80.Push();

    protected override void OverlayUI()
    {
        if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent)
        {
            Overlay.IsOpen = false;
            return;
        }

        ConfigUI();
    }

    protected override void OverlayPostDraw() => FontManager.Instance().UIFont80.Pop();

    private static string LimitDutyCommandLines
    (
        string commands
    )
    {
        if (string.IsNullOrEmpty(commands)) return string.Empty;

        var commandLines = commands.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        return commandLines.Length <= MAX_DUTY_COMMAND_LINES ?
                   commands :
                   string.Join("\n", commandLines.Take(MAX_DUTY_COMMAND_LINES));
    }

    private static int GetDutyCommandLineCount
    (
        string commands
    ) =>
        string.IsNullOrEmpty(commands) ?
            0 :
            commands.Split(["\r\n", "\n", "\r"], StringSplitOptions.None).Length;

    private class Config : ModuleConfig
    {
        // 辅助职业技能是否为真
        public bool AddonIsDragRealAction = true;

        // 仅用于迁移旧配置
        public string AutoEnableDisablePlugins = string.Empty;
        public string JoinDutyCommands         = string.Empty;
        public string LeaveDutyCommands        = string.Empty;

        public Vector3 DefaultPositionEnterZoneNorthHorn = new(882.2f, 258.5f, 882.0f);
        public Vector3 DefaultPositionEnterZoneSouthHorn = new(834, 73, -694);

        public float DistanceToAutoOpenTreasure = 5f;

        // 自动开箱
        public bool IsEnabledAutoOpenTreasure;

        // 辅助吟游诗人
        public bool IsEnabledBardOffensiveAria = true;

        // 辅助狂战士
        public bool IsEnabledBerserkerRageAutoFace = true;
        public bool IsEnabledBerserkerRageReplace  = true;

        // 突出标注
        public bool IsEnabledHighlightCarrot      = true;
        public bool IsEnabledHighlightSurveyPoint = true;
        public bool IsEnabledHighlightTreasure    = true;
        public bool IsEnabledHighlightCE          = true;
        public bool IsEnabledHighlightFATE        = true;

        // 隐藏任务指令
        public bool IsEnabledHideDutyCommand;

        // 修改 HUD
        public bool IsEnabledModifyInfoHUD = true;

        // 显示知见水晶
        public bool IsEnabledKnowledgeCrystalFastUse = true;

        // 辅助武僧
        public bool IsEnabledMonkKickNoMove = true;

        // 寻路控制
        public bool InterruptPathfindingOnMovementInput = true;
        public bool IsEnabledDismountCE;
        public bool IsEnabledDismountFATE;

        // 通知 CE 开始
        public bool IsEnabledNotifyCENotification = true;
        public bool IsEnabledNotifyCETTS          = true;
        public bool IsEnabledNotifyCESystemSound  = true;

        // 通知任务出现
        public Dictionary<CrescentEventType, bool> IsEnabledNotifyEventsCategoried = [];
    }

    private abstract class BaseIslandModule
    (
        OccultCrescentHelper mainModule
    )
    {
        protected readonly OccultCrescentHelper MainModule = mainModule;

        public virtual void Init() { }

        public virtual void OnUpdate() { }

        public virtual void DrawConfig() { }

        public virtual void Uninit() { }
    }

    #region 常量

    private const int  MAX_DUTY_COMMAND_LINES        = 15;
    private const uint DEMI_RETURN_ACTION_ID         = 41343;
    private const uint SOUTH_HORN_TERRITORY_ID       = 1252;
    private const uint NORTH_HORN_TERRITORY_ID       = 1346;
    private const uint PHANTOM_VILLIAGE_TERRITORY_ID = 1278;

    #endregion
}
