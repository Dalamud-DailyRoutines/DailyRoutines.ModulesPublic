using System.Timers;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using Timer = System.Timers.Timer;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoRefreshPartyFinder : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoRefreshPartyFinderTitle"),
        Description = GetLoc("AutoRefreshPartyFinderDescription"),
        Category    = ModuleCategories.UIOperation,
    };

    private static Config ModuleConfig = null!;
    
    private static Timer? PFRefreshTimer;
    
    private static int Cooldown;

    private static NumericInputNode?   RefreshIntervalNode;
    private static CheckboxNode?       OnlyInactiveNode;
    private static TextNode?           LeftTimeNode;
    private static HorizontalListNode? LayoutNode;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        PFRefreshTimer           ??= new(1_000);
        PFRefreshTimer.AutoReset =   true;
        PFRefreshTimer.Elapsed   +=  OnRefreshTimer;
        
        Cooldown = ModuleConfig.RefreshInterval;

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "LookingForGroup",       OnAddonPF);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "LookingForGroup",       OnAddonPF);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LookingForGroup",       OnAddonPF);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "LookingForGroupDetail", OnAddonLFGD);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LookingForGroupDetail", OnAddonLFGD);

        if (LookingForGroup != null) 
            OnAddonPF(AddonEvent.PostSetup, null);
    }
    
    // 招募
    private static void OnAddonPF(AddonEvent type, AddonArgs? args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                Cooldown = ModuleConfig.RefreshInterval;
                
                CreateRefreshIntervalNode();
                
                PFRefreshTimer.Restart();
                break;
            case AddonEvent.PostRefresh when ModuleConfig.OnlyInactive:
                Cooldown = ModuleConfig.RefreshInterval;
                UpdateNextRefreshTime(Cooldown);
                PFRefreshTimer.Restart();
                break;
            case AddonEvent.PreFinalize:
                PFRefreshTimer.Stop();
                CleanNodes();
                break;
        }
    }

    // 招募详情
    private static void OnAddonLFGD(AddonEvent type, AddonArgs? args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                PFRefreshTimer.Stop();
                break;
            case AddonEvent.PreFinalize:
                Cooldown = ModuleConfig.RefreshInterval;
                PFRefreshTimer.Restart();
                break;
        }
    }

    private static void OnRefreshTimer(object? sender, ElapsedEventArgs e)
    {
        if (!IsAddonAndNodesReady(LookingForGroup) || IsAddonAndNodesReady(LookingForGroupDetail))
        {
            PFRefreshTimer.Stop();
            return;
        }

        if (Cooldown > 1)
        {
            Cooldown--;
            UpdateNextRefreshTime(Cooldown);
            return;
        }

        Cooldown = ModuleConfig.RefreshInterval;
        UpdateNextRefreshTime(Cooldown);

        DService.Framework.Run(() => AgentLookingForGroup.Instance()->RequestListingsUpdate());
    }

    private static void CleanNodes()
    {
        Service.AddonController.DetachNode(RefreshIntervalNode);
        RefreshIntervalNode = null;
        
        Service.AddonController.DetachNode(OnlyInactiveNode);
        OnlyInactiveNode = null;
        
        Service.AddonController.DetachNode(LayoutNode);
        LayoutNode = null;
        
        Service.AddonController.DetachNode(LeftTimeNode);
        LeftTimeNode = null;
    }

    private static void CreateRefreshIntervalNode()
    {
        if (LookingForGroup == null) return;
        
        RefreshIntervalNode ??= new() 
        {
            Size      = new(150.0f, 28f),
            IsVisible = true,
            Min       = 5,
            Max       = 10000,
            Step      = 5,
            OnValueUpdate = newValue =>
            {
                ModuleConfig.RefreshInterval = newValue;
                ModuleConfig.Save(ModuleManager.GetModule<AutoRefreshPartyFinder>());
                
                Cooldown = ModuleConfig.RefreshInterval;
                PFRefreshTimer.Restart();
            },
            Value   = ModuleConfig.RefreshInterval
        };

        RefreshIntervalNode.Value = ModuleConfig.RefreshInterval;
        RefreshIntervalNode.ValueTextNode.SetNumber(ModuleConfig.RefreshInterval);
        
        OnlyInactiveNode ??= new()
        {
            Size      = new(150f, 28f),
            IsVisible = true,
            IsChecked = ModuleConfig.OnlyInactive,
            IsEnabled = true,
            SeString  = GetLoc("AutoRefreshPartyFinder-OnlyInactive"),
            OnClick = newState =>
            {
                ModuleConfig.OnlyInactive = newState;
                ModuleConfig.Save(ModuleManager.GetModule<AutoRefreshPartyFinder>());
            },
            Position = new(0, 1)
        };

        LeftTimeNode ??= new TextNode()
        {
            SeString         = $"({ModuleConfig.RefreshInterval})  ",
            FontSize         = 12,
            IsVisible        = true,
            Size             = new(0, 28f),
            AlignmentType    = AlignmentType.Right,
            Position         = new(10, 0),
            TextColor        = ColorHelper.GetColor(8),
            TextOutlineColor = ColorHelper.GetColor(7),
        };

        LayoutNode = new HorizontalListNode
        {
            Width     = 270,
            IsVisible = true,
            Position  = new(500, 630),
            Alignment = HorizontalListAnchor.Right
        };
        LayoutNode.AddNode(OnlyInactiveNode, RefreshIntervalNode, LeftTimeNode);
        
        Service.AddonController.AttachNode(LayoutNode, LookingForGroup->RootNode);
    }

    private static void UpdateNextRefreshTime(int leftTime)
    {
        if (LeftTimeNode == null) return;

        LeftTimeNode.SeString = $"({leftTime})  ";
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddonPF);
        DService.AddonLifecycle.UnregisterListener(OnAddonLFGD);

        if (PFRefreshTimer != null)
        {
            PFRefreshTimer.Elapsed -= OnRefreshTimer;
            PFRefreshTimer.Stop();
            PFRefreshTimer.Dispose();
        }
        PFRefreshTimer = null;
        
        CleanNodes();

        base.Uninit();
    }

    private class Config : ModuleConfiguration
    {
        public int RefreshInterval = 10; // 秒
        public bool OnlyInactive = true;
    }
}
