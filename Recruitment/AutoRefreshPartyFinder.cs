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
    private static Config ModuleConfig = null!;

    private static Timer? PFRefreshTimer;

    private static int Cooldown;

    private static NumericInputNode?   RefreshIntervalNode;
    private static CheckboxNode?       OnlyInactiveNode;
    private static TextNode?           LeftTimeNode;
    private static HorizontalListNode? LayoutNode;

    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoRefreshPartyFinderTitle"),
        Description = Lang.Get("AutoRefreshPartyFinderDescription"),
        Category    = ModuleCategory.Recruitment
    };

    protected override void Init()
    {
        ModuleConfig = Config.Load(this) ?? new();

        PFRefreshTimer           ??= new(1_000);
        PFRefreshTimer.AutoReset =   true;
        PFRefreshTimer.Elapsed   +=  OnRefreshTimer;

        Cooldown = ModuleConfig.RefreshInterval;

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "LookingForGroup",       OnAddonPF);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "LookingForGroup",       OnAddonPF);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LookingForGroup",       OnAddonPF);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "LookingForGroupDetail", OnAddonLFGD);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LookingForGroupDetail", OnAddonLFGD);

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
        if (!LookingForGroup->IsAddonAndNodesReady() || LookingForGroupDetail->IsAddonAndNodesReady())
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

        DService.Instance().Framework.Run(() => AgentLookingForGroup.Instance()->RequestListingsUpdate());
    }

    private static void CleanNodes()
    {
        RefreshIntervalNode?.Dispose();
        RefreshIntervalNode = null;

        OnlyInactiveNode?.Dispose();
        OnlyInactiveNode = null;

        LayoutNode?.Dispose();
        LayoutNode = null;

        LeftTimeNode?.Dispose();
        LeftTimeNode = null;
    }

    private static void CreateRefreshIntervalNode()
    {
        if (LookingForGroup == null) return;

        OnlyInactiveNode ??= new()
        {
            Size      = new(150f, 28f),
            IsVisible = true,
            IsChecked = ModuleConfig.OnlyInactive,
            IsEnabled = true,
            String    = Lang.Get("AutoRefreshPartyFinder-OnlyInactive"),
            OnClick = newState =>
            {
                ModuleConfig.OnlyInactive = newState;
                ModuleConfig.Save(ModuleManager.Instance().GetModule<AutoRefreshPartyFinder>());
            },
            Position = new(0, 1)
        };

        RefreshIntervalNode ??= new()
        {
            Size      = new(150f, 30f),
            Position  = new(0, 2),
            IsVisible = true,
            Min       = 5,
            Max       = 10000,
            Step      = 5,
            OnValueUpdate = newValue =>
            {
                ModuleConfig.RefreshInterval = newValue;
                ModuleConfig.Save(ModuleManager.Instance().GetModule<AutoRefreshPartyFinder>());

                Cooldown = ModuleConfig.RefreshInterval;
                PFRefreshTimer.Restart();
            },
            Value = ModuleConfig.RefreshInterval
        };

        RefreshIntervalNode.Value = ModuleConfig.RefreshInterval;
        RefreshIntervalNode.ValueTextNode.SetNumber(ModuleConfig.RefreshInterval);

        LeftTimeNode ??= new TextNode
        {
            String           = $"({ModuleConfig.RefreshInterval})  ",
            FontSize         = 12,
            IsVisible        = true,
            Size             = new(0, 28f),
            AlignmentType    = AlignmentType.Right,
            Position         = new(10, 2),
            TextColor        = ColorHelper.GetColor(8),
            TextOutlineColor = ColorHelper.GetColor(7)
        };

        LayoutNode = new HorizontalListNode
        {
            Width     = 270,
            IsVisible = true,
            Position  = new(500, 630),
            Alignment = HorizontalListAnchor.Right
        };
        LayoutNode.AddNode([OnlyInactiveNode, RefreshIntervalNode, LeftTimeNode]);

        LayoutNode.AttachNode(LookingForGroup->RootNode);
    }

    private static void UpdateNextRefreshTime(int leftTime)
    {
        if (LeftTimeNode == null) return;

        LeftTimeNode.String = $"({leftTime})  ";
    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonPF);
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonLFGD);

        if (PFRefreshTimer != null)
        {
            PFRefreshTimer.Elapsed -= OnRefreshTimer;
            PFRefreshTimer.Stop();
            PFRefreshTimer.Dispose();
        }

        PFRefreshTimer = null;

        CleanNodes();
    }

    private class Config : ModuleConfig
    {
        public bool OnlyInactive    = true;
        public int  RefreshInterval = 10; // 秒
    }
}
