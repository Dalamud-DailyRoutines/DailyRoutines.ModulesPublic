using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoDesynthesizeItems : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoDesynthesizeItemsTitle"),
        Description = GetLoc("AutoDesynthesizeItemsDescription"),
        Category    = ModuleCategories.UIOperation,
    };

    private static Config ModuleConfig = null!;
    private static TextNode LableNode;
    private static CheckboxNode CheckboxNode;
    private static TextButtonNode StartButtonNode;
    private static TextButtonNode StopButtonNode;

    protected override void Init()
    {
        TaskHelper ??= new() { TimeLimitMS = 10_000 };

        ModuleConfig = LoadConfig<Config>() ?? new();

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "SalvageItemSelector", OnAddonList);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "SalvageItemSelector", OnAddonList);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "SalvageDialog",       OnAddon);
        if (IsAddonAndNodesReady(SalvageItemSelector))
            OnAddonList(AddonEvent.PostSetup, null);
    }

    private void OnAddonList(AddonEvent type, AddonArgs? args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                if (SalvageItemSelector == null) return;

                if (LableNode == null)
                {
                    LableNode = new()
                    {
                        IsVisible = true,
                        Position = new(150, 8),
                        Size = new(150, 28),
                        Text = $"{Info.Title}",
                        FontSize = 14,
                        AlignmentType = AlignmentType.Left,
                        TextFlags = TextFlags.AutoAdjustNodeSize | TextFlags.Edge
                    };
                    Service.AddonController.AttachNode(LableNode, SalvageItemSelector->RootNode);
                }

                if (CheckboxNode == null)
                {
                    CheckboxNode = new()
                    {
                        IsVisible = true,
                        Position = new(240, 12),
                        Size = new(75, 20),
                        IsChecked = ModuleConfig.SkipWhenHQ,
                        LabelText = GetLoc("AutoDesynthesizeItems-SkipHQ"),
                        OnClick = newState =>
                        {
                            ModuleConfig.SkipWhenHQ = newState;
                            SaveConfig(ModuleConfig);
                        }
                    };
                    Service.AddonController.AttachNode(CheckboxNode, SalvageItemSelector->RootNode);
                }

                if (StartButtonNode == null)
                {
                    StartButtonNode = new()
                    {
                        IsVisible = true,
                        Position = new(355, 10),
                        Size = new(100, 28),
                        Label = GetLoc("Start"),
                        OnClick = StartDesynthesizeAll
                    };
                    Service.AddonController.AttachNode(StartButtonNode, SalvageItemSelector->RootNode);
                }

                StartButtonNode.IsEnabled = !TaskHelper.IsBusy;

                if (StopButtonNode == null)
                {
                    StopButtonNode = new()
                    {
                        IsVisible = true,
                        Position = new(460, 10),
                        Size = new(100, 28),
                        Label = GetLoc("Stop"),
                        OnClick = () => TaskHelper.Abort(),
                    };
                    Service.AddonController.AttachNode(StopButtonNode, SalvageItemSelector->RootNode);
                }

                break;
            case AddonEvent.PreFinalize:
                Service.AddonController.DetachNode(LableNode);
                LableNode = null;

                Service.AddonController.DetachNode(CheckboxNode);
                CheckboxNode = null;

                Service.AddonController.DetachNode(StartButtonNode);
                StartButtonNode = null;

                Service.AddonController.DetachNode(StopButtonNode);
                StopButtonNode = null;

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
