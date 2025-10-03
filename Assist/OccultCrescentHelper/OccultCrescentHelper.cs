using System.Collections.Generic;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DailyRoutines.ModulesPublic;

public partial class OccultCrescentHelper : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title           = $"{GetLoc("OccultCrescentHelperTitle")}",
        Description     = $"{GetLoc("OccultCrescentHelperDescription")}",
        Category        = ModuleCategories.Assist,
        Author          = ["Fragile"],
        ModulesConflict = ["AutoFaceCameraDirection"]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private static Config ModuleConfig = null!;
    
    private static AetheryteManager  AetheryteModule;
    private static CEManager         CEModule;
    private static TreasureManager   TreasureModule;
    private static SupportJobManager SupportJobModule;
    private static OthersManager     OthersModule;

    private static List<BaseIslandModule> Modules = [];
    
    protected override void Init()
    {
        ModuleConfig =   LoadConfig<Config>() ?? new();
        
        Overlay       ??= new(this);
        Overlay.Flags &=  ~ImGuiWindowFlags.AlwaysAutoResize;
        
        AetheryteModule  = new(this);
        CEModule         = new(this);
        TreasureModule   = new(this);
        SupportJobModule = new(this);
        OthersModule     = new(this);
        
        Modules = [AetheryteModule, CEModule, TreasureModule, SupportJobModule, OthersModule];
        
        foreach (var module in Modules)
            module.Init();
        
        FrameworkManager.Register(OnUpdate, true, throttleMS: 500);
    }
    
    protected override void Uninit()
    {
        FrameworkManager.Unregister(OnUpdate);

        foreach (var module in Modules)
            module.Uninit();
    }
    
    private static void OnUpdate(IFramework framework)
    {
        if (GameState.TerritoryIntendedUse != 61) return;
        
        foreach (var module in Modules)
            module.OnUpdate();
    }

    protected override void ConfigUI()
    {
        using var tab = ImRaii.TabBar("###Config", ImGuiTabBarFlags.Reorderable);
        if (!tab) return;
        
        using (var aetheryteTab = ImRaii.TabItem($"{LuminaWrapper.GetEObjName(2014664)}"))
        {
            if (aetheryteTab)
                AetheryteModule.DrawConfig();
        }
        
        using (var ceTab = ImRaii.TabItem("CE / FATE"))
        {
            if (ceTab)
                CEModule.DrawConfig();
        }

        using (var treasureTab = ImRaii.TabItem($"{LuminaWrapper.GetAddonText(395)}"))
        {
            if (treasureTab)
                TreasureModule.DrawConfig();
        }
        
        using (var supportJobTab = ImRaii.TabItem($"{LuminaWrapper.GetAddonText(16633)}"))
        {
            if (supportJobTab)
                SupportJobModule.DrawConfig();
        }
        
        using (var othersTab = ImRaii.TabItem($"{LuminaWrapper.GetAddonText(832)}"))
        {
            if (othersTab)
                OthersModule.DrawConfig();
        }
    }

    protected override void OverlayPreDraw() => FontManager.UIFont80.Push();

    protected override void OverlayUI()
    {
        if (GameState.TerritoryIntendedUse != 61)
        {
            Overlay.IsOpen = false;
            return;
        }
        
        ConfigUI();
    }

    protected override void OverlayPostDraw() => FontManager.UIFont80.Pop();

    private static void TP(Vector3 pos, TaskHelper taskHelper, int weight = 0, bool abortBefore = true)
    {
        if (abortBefore)
            taskHelper.Abort();
        
        taskHelper.Enqueue(() => UseActionManager.UseActionLocation(ActionType.Action, 41343), weight: weight);
        taskHelper.Enqueue(() => !IsScreenReady(),                                             weight: weight);
        taskHelper.Enqueue(() => DService.ObjectTable.LocalPlayer != null && IsScreenReady(),  weight: weight);
        taskHelper.Enqueue(() =>
        {
            MovementManager.TPPlayerAddress(pos);
            MovementManager.TPMountAddress(pos);
        }, weight: weight);
        taskHelper.DelayNext(100, weight: weight);
        taskHelper.Enqueue(() => MovementManager.TPGround(), weight: weight);
    }
    
    private static unsafe uint GetIslandID() =>
        (uint)*(ulong*)((byte*)GameMain.Instance() + 0xB50 + 1488);
    
    public class Config : ModuleConfiguration
    {
        // 连接线
        public bool IsEnabledDrawLineToTreasure = true;
        public bool IsEnabledDrawLineToLog      = true;
        public bool IsEnabledDrawLineToCarrot   = true;
        
        // 自动开箱
        public bool  IsEnabledAutoOpenTreasure;
        public float DistanceToAutoOpenTreasure = 20f;
        
        // 优先移动到 魔路 / 简易魔路
        public bool  IsEnabledMoveToAetheryte  = true;
        public float DistanceToMoveToAetheryte = 100f;
        
        // 通知任务出现
        public bool                                IsEnabledNotifyEvents           = true;
        public Dictionary<CrescentEventType, bool> IsEnabledNotifyEventsCategoried = [];
        
        // 通知 CE 开始
        public bool IsEnabledNotifyCEStarts = true;
        
        // 优先移动到 CE / FATE
        public bool  IsEnabledMoveToEvent = true;
        public float LeftTimeMoveToEvent  = 90;
        
        // 岛 ID
        public bool IsEnabledIslandIDDTR  = true;
        public bool IsEnabledIslandIDChat = true;
        
        // 修改 HUD
        public bool IsEnabledModifyInfoHUD = true;
        
        // 辅助武僧
        public bool IsEnabledMonkKickNoMove = true;
        
        // 辅助狂战士
        public bool IsEnabledBerserkerRageAutoFace = true;
        public bool IsEnabledBerserkerRageReplace  = true;

        // 修改默认位置
        public bool    IsEnabledModifyDefaultPositionEnterZoneSouthHorn = true;
        public Vector3 DefaultPositionEnterZoneSouthHorn = new(834, 73, -694);
        
        // 自动启用/禁用插件
        public bool   IsEnabledAutoEnableDisablePlugins = true;
        public string AutoEnableDisablePlugins          = string.Empty;
        
        // 辅助职业排序
        public List<uint> AddonSupportJobOrder = [];
        
        // 辅助职业技能是否为真
        public bool AddonIsDragRealAction = true;
        
        // 隐藏任务指令
        public bool IsEnabledHideDutyCommand;
        
        // CE 历史记录
        // 岛 ID - CE ID - 刷新时间秒级时间戳
        public Dictionary<uint, Dictionary<uint, long>> CEHistory = [];
    }

    public abstract class BaseIslandModule(OccultCrescentHelper mainModule)
    {
        protected readonly OccultCrescentHelper MainModule = mainModule;

        public virtual void Init() { }
        
        public virtual void OnUpdate() { }

        public virtual void DrawConfig() { }

        public virtual void Uninit() { }
    }
}
