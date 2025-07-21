using System.Numerics;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

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

    protected override void Init()
    {
        TaskHelper ??= new() { TimeLimitMS = 10_000 };
        Overlay    ??= new(this);

        ModuleConfig = LoadConfig<Config>() ?? new();

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "SalvageDialog",       OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "SalvageItemSelector", OnAddonList);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "SalvageItemSelector", OnAddonList);
        if (IsAddonAndNodesReady(SalvageItemSelector)) 
            OnAddonList(AddonEvent.PostSetup, null);
    }

    protected override void OverlayUI()
    {
        var addon = SalvageItemSelector;
        if (addon == null) return;

        var pos = new Vector2(addon->GetX() - ImGui.GetWindowSize().X, addon->GetY() + 6);
        ImGui.SetWindowPos(pos);

        ImGui.TextColored(LightSkyBlue, GetLoc("AutoDesynthesizeItemsTitle"));

        ImGui.Separator();

        using (ImRaii.Disabled(TaskHelper.IsBusy))
        {
            if (ImGui.Checkbox(GetLoc("AutoDesynthesizeItems-SkipHQ"), ref ModuleConfig.SkipWhenHQ))
                SaveConfig(ModuleConfig);

            if (ImGui.Button(GetLoc("Start"))) 
                StartDesynthesize();
        }
        
        ImGui.SameLine();
        if (ImGui.Button(GetLoc("Stop"))) 
            TaskHelper.Abort();
    }

    private void OnAddonList(AddonEvent type, AddonArgs? args)
    {
        Overlay.IsOpen = type switch
        {
            AddonEvent.PostSetup   => true,
            AddonEvent.PreFinalize => false,
            _                      => Overlay.IsOpen,
        };
        
        if (type == AddonEvent.PreFinalize)
            TaskHelper.Abort();
    }

    private static void OnAddon(AddonEvent type, AddonArgs args)
    {
        if (!Throttler.Throttle("AutoDesynthesizeItems-Process", 100)) return;
        if (!IsAddonAndNodesReady(SalvageDialog)) return;

        Callback(SalvageDialog, true, 0, 0);
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
