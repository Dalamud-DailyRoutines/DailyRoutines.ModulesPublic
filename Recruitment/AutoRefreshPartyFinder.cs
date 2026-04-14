using System.Timers;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using Timer = System.Timers.Timer;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoRefreshPartyFinder : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoRefreshPartyFinderTitle"),
        Description = Lang.Get("AutoRefreshPartyFinderDescription"),
        Category    = ModuleCategory.Recruitment
    };
    
    private Config config = null!;

    private Timer? refreshTimer;

    private int cooldown;

    private NumericInputNode?   refreshIntervalNode;
    private CheckboxNode?       onlyInactiveNode;
    private TextNode?           leftTimeNode;
    private HorizontalListNode? layoutNode;
    
    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        refreshTimer           ??= new(1_000);
        refreshTimer.AutoReset =   true;
        refreshTimer.Elapsed   +=  OnRefreshTimer;

        cooldown = config.RefreshInterval;

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "LookingForGroup",       OnAddonPF);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "LookingForGroup",       OnAddonPF);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LookingForGroup",       OnAddonPF);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "LookingForGroupDetail", OnAddonLFGD);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LookingForGroupDetail", OnAddonLFGD);

        if (LookingForGroup != null)
            OnAddonPF(AddonEvent.PostSetup, null);
    }
    
    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonPF);
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonLFGD);

        if (refreshTimer != null)
        {
            refreshTimer.Elapsed -= OnRefreshTimer;
            refreshTimer.Stop();
            refreshTimer.Dispose();
        }

        refreshTimer = null;

        CleanNodes();
    }

    // 招募
    private void OnAddonPF(AddonEvent type, AddonArgs? args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                cooldown = config.RefreshInterval;

                CreateRefreshIntervalNode();

                refreshTimer.Restart();
                break;
            case AddonEvent.PostRefresh when config.OnlyInactive:
                cooldown = config.RefreshInterval;
                UpdateNextRefreshTime(cooldown);
                refreshTimer.Restart();
                break;
            case AddonEvent.PreFinalize:
                refreshTimer.Stop();
                CleanNodes();
                break;
        }
    }

    // 招募详情
    private void OnAddonLFGD(AddonEvent type, AddonArgs? args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                refreshTimer.Stop();
                break;
            case AddonEvent.PreFinalize:
                cooldown = config.RefreshInterval;
                refreshTimer.Restart();
                break;
        }
    }

    private void OnRefreshTimer(object? sender, ElapsedEventArgs e)
    {
        if (!LookingForGroup->IsAddonAndNodesReady() || LookingForGroupDetail->IsAddonAndNodesReady())
        {
            refreshTimer.Stop();
            return;
        }

        if (cooldown > 1)
        {
            cooldown--;
            UpdateNextRefreshTime(cooldown);
            return;
        }

        cooldown = config.RefreshInterval;
        UpdateNextRefreshTime(cooldown);

        DService.Instance().Framework.Run(() => AgentLookingForGroup.Instance()->RequestListingsUpdate());
    }

    private void CleanNodes()
    {
        refreshIntervalNode?.Dispose();
        refreshIntervalNode = null;

        onlyInactiveNode?.Dispose();
        onlyInactiveNode = null;

        layoutNode?.Dispose();
        layoutNode = null;

        leftTimeNode?.Dispose();
        leftTimeNode = null;
    }

    private void CreateRefreshIntervalNode()
    {
        if (LookingForGroup == null) return;

        onlyInactiveNode ??= new()
        {
            Size      = new(150f, 28f),
            IsVisible = true,
            IsChecked = config.OnlyInactive,
            IsEnabled = true,
            String    = Lang.Get("AutoRefreshPartyFinder-OnlyInactive"),
            OnClick = newState =>
            {
                config.OnlyInactive = newState;
                config.Save(ModuleManager.Instance().GetModule<AutoRefreshPartyFinder>());
            },
            Position = new(0, 1)
        };

        refreshIntervalNode ??= new()
        {
            Size      = new(150f, 30f),
            Position  = new(0, 2),
            IsVisible = true,
            Min       = 5,
            Max       = 10000,
            Step      = 5,
            OnValueUpdate = newValue =>
            {
                config.RefreshInterval = newValue;
                config.Save(ModuleManager.Instance().GetModule<AutoRefreshPartyFinder>());

                cooldown = config.RefreshInterval;
                refreshTimer.Restart();
            },
            Value = config.RefreshInterval
        };

        refreshIntervalNode.Value = config.RefreshInterval;
        refreshIntervalNode.ValueTextNode.SetNumber(config.RefreshInterval);

        leftTimeNode ??= new TextNode
        {
            String           = $"({config.RefreshInterval})  ",
            FontSize         = 12,
            IsVisible        = true,
            Size             = new(0, 28f),
            AlignmentType    = AlignmentType.Right,
            Position         = new(10, 2),
            TextColor        = ColorHelper.GetColor(8),
            TextOutlineColor = ColorHelper.GetColor(7)
        };

        layoutNode = new HorizontalListNode
        {
            Width     = 270,
            IsVisible = true,
            Position  = new(500, 630),
            Alignment = HorizontalListAnchor.Right
        };
        layoutNode.AddNode([onlyInactiveNode, refreshIntervalNode, leftTimeNode]);

        layoutNode.AttachNode(LookingForGroup->RootNode);
    }

    private void UpdateNextRefreshTime(int leftTime)
    {
        if (leftTimeNode == null) return;

        leftTimeNode.String = $"({leftTime})  ";
    }
    
    private class Config : ModuleConfig
    {
        public bool OnlyInactive    = true;
        public int  RefreshInterval = 10; // 秒
    }
}
