using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using KamiToolKit.Nodes;
using KamiToolKit.Classes;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoDesynthesizeItems : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoDesynthesizeItemsTitle"),
        Description = GetLoc("AutoDesynthesizeItemsDescription"),
        Category    = ModuleCategories.UIOperation,
    };

    private static Config             ModuleConfig = null!;
    private static HorizontalListNode LayoutNode;
    private static CheckboxNode       CheckboxNode;
    private static TextButtonNode     ButtonNode;

    protected override void Init()
    {
        TaskHelper ??= new() { TimeLimitMS = 10_000 };

        ModuleConfig = LoadConfig<Config>() ?? new();

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "SalvageItemSelector", OnAddonList);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "SalvageItemSelector", OnAddonList);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "SalvageDialog",       OnAddon);
        if (IsAddonAndNodesReady(SalvageItemSelector))
            OnAddonList(AddonEvent.PostDraw, null);
    }

    private void OnAddonList(AddonEvent type, AddonArgs? args)
    {
        switch (type)
        {
            case AddonEvent.PostDraw:
                if (SalvageItemSelector == null) return;

                if (CheckboxNode == null)
                {
                    CheckboxNode = new()
                    {
                        IsVisible = true,
                        Position = new(0,2),
                        Size = new(16, 16),
                        IsChecked = ModuleConfig.SkipWhenHQ,
                        LabelText = GetLoc("AutoDesynthesizeItems-SkipHQ"),
                        OnClick = newState =>
                        {
                            ModuleConfig.SkipWhenHQ = newState;
                            SaveConfig(ModuleConfig);
                        }
                    };
                }

                if (ButtonNode == null)
                {
                    ButtonNode = new()
                    {
                        IsVisible = true,
                        Size = new (200, 28),
                        Label = $"{Info.Title}",
                        OnClick = () =>
                        {
                            StartDesynthesizeAll();
                            ToggleButtonNode(true);
                        },
                    };
                }
                
                if (LayoutNode == null)
                {
                    LayoutNode = new()
                    {
                        Width     = SalvageItemSelector->GetScaledWidth(true),
                        IsVisible = true,
                        Position  = new(-30, 8),
                        Alignment = HorizontalListAnchor.Right
                    };
                    LayoutNode.AddNode(ButtonNode, CheckboxNode);
                    Service.AddonController.AttachNode(LayoutNode, SalvageItemSelector->RootNode);
                }
                break;
            
            case AddonEvent.PreFinalize:
                Service.AddonController.DetachNode(CheckboxNode);
                CheckboxNode = null;
                Service.AddonController.DetachNode(ButtonNode);
                ButtonNode = null;
                Service.AddonController.DetachNode(LayoutNode);
                LayoutNode = null;
                
                TaskHelper.Abort();
                break;
        }
    }

    private static void OnAddon(AddonEvent type, AddonArgs args)
    {
        if (!Throttler.Throttle("AutoDesynthesizeItems-Process", 100)) return;
        if (!IsAddonAndNodesReady(SalvageDialog)) return;

        Callback(SalvageDialog, true, 0, 0);
    }

    private void ToggleButtonNode(bool toStop)
    {
        if (toStop)
        {
            ButtonNode.Label = GetLoc("Stop");
            ButtonNode.OnClick = () =>
            {
                TaskHelper.Abort();
                ToggleButtonNode(false);
            };
        }
        else
        {
            ButtonNode.Label = $"{Info.Title}";
            ButtonNode.OnClick = () =>
            {
                StartDesynthesizeAll();
                ToggleButtonNode(true);
            };
        }
    }

    private void StartDesynthesizeAll()
    {
        if (TaskHelper.IsBusy) return;
        TaskHelper.Enqueue(StartDesynthesize, "开始分解全部装备");
    }

    private bool? StartDesynthesize()
    {
        if (OccupiedInEvent) return false;
        if (!IsAddonAndNodesReady(SalvageItemSelector)) return false;

        var itemAmount = SalvageItemSelector->AtkValues[9].Int;
        if (itemAmount == 0)
        {
            TaskHelper.Abort();
            ToggleButtonNode(false);
            return true;
        }

        for (var i = 0; i < itemAmount; i++)
        {
            var itemName = MemoryHelper.ReadStringNullTerminated((nint)SalvageItemSelector->AtkValues[(i * 8) + 14].String.Value);
            if (ModuleConfig.SkipWhenHQ)
            {
                if (itemName.Contains('')) // HQ 符号
                    continue;
            }

            SendEvent(AgentId.Salvage, 0, 12, i);
            TaskHelper.Enqueue(StartDesynthesize);
            return true;
        }

        TaskHelper.Abort();
        return true;
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddonList);
        DService.AddonLifecycle.UnregisterListener(OnAddon);

        base.Uninit();
    }

    private class Config : ModuleConfiguration
    {
        public bool SkipWhenHQ;
    }
}
