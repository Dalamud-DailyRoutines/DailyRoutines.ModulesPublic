using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using OmenTools.Info.Game.Data;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoDesynthesizeItems : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoDesynthesizeItemsTitle"),
        Description = Lang.Get("AutoDesynthesizeItemsDescription"),
        Category    = ModuleCategory.UIOperation
    };
    
    private Config config = null!;

    private HorizontalListNode? layoutNode;
    private CheckboxNode?       checkboxNode;
    private TextButtonNode?     buttonNode;

    protected override void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 10_000 };

        config = Config.Load(this) ?? new();

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "SalvageItemSelector", OnAddonList);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "SalvageItemSelector", OnAddonList);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "SalvageDialog",       OnAddon);
    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonList);
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);

        OnAddonList(AddonEvent.PreFinalize, null);
    }
    
    private void OnAddonList(AddonEvent type, AddonArgs? args)
    {
        switch (type)
        {
            case AddonEvent.PostDraw:
                if (SalvageItemSelector == null) return;

                checkboxNode ??= new()
                {
                    IsVisible   = true,
                    Position    = new(50, -2),
                    Size        = new(25, 28),
                    IsChecked   = config.SkipWhenHQ,
                    TextTooltip = Lang.Get("AutoDesynthesizeItems-SkipHQ"),
                    OnClick = newState =>
                    {
                        config.SkipWhenHQ = newState;
                        config.Save(this);
                    }
                };

                buttonNode ??= new()
                {
                    IsVisible = true,
                    Size      = new(200, 28),
                    String    = $"{Info.Title}",
                    OnClick   = StartDesynthesizeAll
                };

                if (layoutNode == null)
                {
                    layoutNode = new()
                    {
                        IsVisible = true,
                        Size      = new(SalvageItemSelector->WindowNode->Width, 28),
                        Position  = new(-33, 8),
                        Alignment = HorizontalListAnchor.Right
                    };
                    
                    layoutNode.AddNode([buttonNode, checkboxNode]);
                    layoutNode.AttachNode(SalvageItemSelector->RootNode);
                }

                if (Throttler.Shared.Throttle("AutoDesynthesizeItems-PostDraw"))
                {
                    if (TaskHelper.IsBusy)
                    {
                        buttonNode.String  = Lang.Get("Stop");
                        buttonNode.OnClick = () => TaskHelper.Abort();
                    }
                    else
                    {
                        buttonNode.String  = $"{Info.Title}";
                        buttonNode.OnClick = StartDesynthesizeAll;
                    }
                }

                break;

            case AddonEvent.PreFinalize:
                checkboxNode?.Dispose();
                checkboxNode = null;

                buttonNode?.Dispose();
                buttonNode = null;

                layoutNode?.Dispose();
                layoutNode = null;

                TaskHelper?.Abort();
                break;
        }
    }

    private static void OnAddon(AddonEvent type, AddonArgs args)
    {
        if (!Throttler.Shared.Throttle("AutoDesynthesizeItems-Process", 100)) return;
        if (!SalvageDialog->IsAddonAndNodesReady()) return;

        SalvageDialog->Callback(13, true);
        SalvageDialog->Callback(0, 0);
    }

    private void StartDesynthesizeAll()
    {
        if (TaskHelper.IsBusy) return;
        TaskHelper.Enqueue(StartDesynthesize, "开始分解全部装备");
    }

    private bool StartDesynthesize()
    {
        if (DService.Instance().Condition.IsOccupiedInEvent) return false;
        if (!SalvageItemSelector->IsAddonAndNodesReady()) return false;

        // 背包满了
        if (Inventories.Player.IsFull(3))
        {
            RaptureLogModule.Instance()->ShowLogMessage(3974);
            TaskHelper.Abort();
            return true;
        }

        var itemCount = SalvageItemSelector->AtkValues[9].Int;
        if (itemCount == 0)
        {
            TaskHelper.Abort();
            return true;
        }

        for (var i = 0; i < itemCount; i++)
        {
            var itemName = SalvageItemSelector->AtkValues[i * 8 + 14].String.ToString();
            if (config.SkipWhenHQ)
            {
                if (itemName.Contains('\ue03c')) // HQ 符号
                    continue;
            }

            AgentId.Salvage.SendEvent(0, 12, i);
            TaskHelper.Enqueue(StartDesynthesize);
            return true;
        }

        TaskHelper.Abort();
        return true;
    }
    
    private class Config : ModuleConfig
    {
        public bool SkipWhenHQ;
    }
}
