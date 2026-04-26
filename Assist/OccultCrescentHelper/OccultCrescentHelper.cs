using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.Dalamud;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using OmenTools.Threading.TaskHelper;

namespace DailyRoutines.ModulesPublic;

public partial class OccultCrescentHelper : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title           = Lang.Get("OccultCrescentHelperTitle"),
        Description     = Lang.Get("OccultCrescentHelperDescription"),
        Category        = ModuleCategory.Assist,
        Author          = ["Fragile"],
        ModulesConflict = ["AutoFaceCameraDirection"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private Config config = null!;

    private readonly AetheryteManager  aetheryteModule;
    private readonly CEManager         ceModule;
    private readonly TreasureManager   treasureModule;
    private readonly SupportJobManager supportJobModule;
    private readonly OthersManager     othersModule;

    private List<BaseIslandModule> modules = [];

    private static readonly CompSig IslandIDInstanceOffsetSig = new("48 8D 8F ?? ?? ?? ?? 40 0F B6 D5 E8 ?? ?? ?? ?? 8B D3");
    private                 nint    islandIDInstanceOffset;

    public OccultCrescentHelper()
    {
        aetheryteModule  = new(this);
        ceModule         = new(this);
        treasureModule   = new(this);
        supportJobModule = new(this);
        othersModule     = new(this);
    }
    
    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        // lea     rcx, [rdi+XXXX], 因为是四字节所以用 uint
        if (islandIDInstanceOffset == nint.Zero)
            islandIDInstanceOffset = IslandIDInstanceOffsetSig.GetStatic();
        DLog.Debug($"[{nameof(OccultCrescentHelper)}] 岛 ID 存储实例偏移量: {islandIDInstanceOffset}");

        Overlay       ??= new(this);
        Overlay.Flags &=  ~ImGuiWindowFlags.AlwaysAutoResize;

        modules = [aetheryteModule, ceModule, treasureModule, supportJobModule, othersModule];

        foreach (var module in modules)
            module.Init();

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
    }

    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        FrameworkManager.Instance().Unreg(OnUpdate);

        foreach (var module in modules)
            module.Uninit();
    }

    private void OnZoneChanged(ushort obj)
    {
        FrameworkManager.Instance().Unreg(OnUpdate);
        if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent) return;
        FrameworkManager.Instance().Reg(OnUpdate, 1_000);
    }

    private void OnUpdate(IFramework framework)
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
        using var tab = ImRaii.TabBar("###Config", ImGuiTabBarFlags.Reorderable);
        if (!tab) return;

        using (var aetheryteTab = ImRaii.TabItem($"{LuminaWrapper.GetEObjName(2014664)}"))
        {
            if (aetheryteTab)
                aetheryteModule.DrawConfig();
        }

        using (var ceTab = ImRaii.TabItem("CE / FATE"))
        {
            if (ceTab)
                ceModule.DrawConfig();
        }

        using (var treasureTab = ImRaii.TabItem($"{LuminaWrapper.GetAddonText(395)}"))
        {
            if (treasureTab)
                treasureModule.DrawConfig();
        }

        using (var supportJobTab = ImRaii.TabItem($"{LuminaWrapper.GetAddonText(16633)}"))
        {
            if (supportJobTab)
                supportJobModule.DrawConfig();
        }

        using (var othersTab = ImRaii.TabItem($"{LuminaWrapper.GetAddonText(832)}"))
        {
            if (othersTab)
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

    private static void TP(Vector3 pos, TaskHelper taskHelper, int weight = 0, bool abortBefore = true)
    {
        if (abortBefore)
            taskHelper.Abort();

        taskHelper.Enqueue(() => UseActionManager.Instance().UseActionLocation(ActionType.Action, 41343),         weight: weight);
        taskHelper.Enqueue(() => !UIModule.IsScreenReady(),                                                       weight: weight);
        taskHelper.Enqueue(() => DService.Instance().ObjectTable.LocalPlayer != null && UIModule.IsScreenReady(), weight: weight);
        taskHelper.Enqueue
        (
            () =>
            {
                MovementManager.Instance().TPPlayerAddress(pos);
                MovementManager.Instance().TPMountAddress(pos);
            },
            weight: weight
        );
        taskHelper.DelayNext(100, weight: weight);
        taskHelper.Enqueue(() => MovementManager.Instance().TPGround(), weight: weight);
    }

    private unsafe uint GetIslandID() =>
        (uint)*(ulong*)((byte*)GameMain.Instance() + islandIDInstanceOffset + 1488);

    private class Config : ModuleConfig
    {
        // 辅助职业技能是否为真
        public bool AddonIsDragRealAction = true;

        // 辅助职业排序
        public List<uint> AddonSupportJobOrder     = [];
        public string     AutoEnableDisablePlugins = string.Empty;

        // CE 历史记录
        // 岛 ID - CE ID - 刷新时间秒级时间戳
        public Dictionary<uint, Dictionary<uint, long>> CEHistory                         = [];
        public Vector3                                  DefaultPositionEnterZoneSouthHorn = new(834, 73, -694);
        public float                                    DistanceToAutoOpenTreasure        = 20f;
        public float                                    DistanceToMoveToAetheryte         = 100f;

        // 自动启用/禁用插件
        public bool IsEnabledAutoEnableDisablePlugins = true;

        // 自动开箱
        public bool IsEnabledAutoOpenTreasure;

        // 辅助狂战士
        public bool IsEnabledBerserkerRageAutoFace = true;
        public bool IsEnabledBerserkerRageReplace  = true;
        public bool IsEnabledDrawLineToCarrot      = true;

        public bool IsEnabledDrawLineToLog = true;

        // 连接线
        public bool IsEnabledDrawLineToTreasure = true;

        // 隐藏任务指令
        public bool IsEnabledHideDutyCommand;

        // 岛 ID
        public bool IsEnabledIslandIDChat = true;

        // 显示知见水晶
        public bool IsEnabledKnowledgeCrystalFastUse = true;

        // 修改默认位置
        public bool IsEnabledModifyDefaultPositionEnterZoneSouthHorn = true;

        // 修改 HUD
        public bool IsEnabledModifyInfoHUD = true;

        // 辅助武僧
        public bool IsEnabledMonkKickNoMove = true;

        // 优先移动到 魔路 / 简易魔路
        public bool IsEnabledMoveToAetheryte = true;

        // 优先移动到 CE / FATE
        public bool IsEnabledMoveToEvent = true;

        // 通知 CE 开始
        public bool IsEnabledNotifyCENotification = true;
        public bool IsEnabledNotifyCETTS          = true;
        public bool IsEnabledNotifyCESystemSound  = true;

        // 通知任务出现
        public bool                                IsEnabledNotifyEvents           = true;
        public Dictionary<CrescentEventType, bool> IsEnabledNotifyEventsCategoried = [];
        public float                               LeftTimeMoveToEvent             = 90;
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
}
