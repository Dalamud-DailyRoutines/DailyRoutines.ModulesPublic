using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using DailyRoutines.Windows;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Colors;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Numerics;

namespace DailyRoutines.Modules;

public unsafe class AutoAetherialReduction : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoAetherialReductionTitle"),
        Description = GetLoc("AutoAetherialReductionDescription"),
        Category    = ModuleCategories.UIOperation,
    };

    private static unsafe AtkUnitBase* PurifyItemSelector => (AtkUnitBase*)DService.Gui.GetAddonByName("PurifyItemSelector");
    private static unsafe AtkUnitBase* PurifyResult => (AtkUnitBase*)DService.Gui.GetAddonByName("PurifyResult");

    private static readonly InventoryType[] BackpackInventories =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4
    ];

    public override void Init()
    {
        TaskHelper ??= new TaskHelper { TimeLimitMS = 10_000 };
        Overlay    ??= new Overlay(this);

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "PurifyItemSelector", OnAddonList);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "PurifyItemSelector", OnAddonList);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "PurifyResult",       OnAddon);
        
        if (IsAddonAndNodesReady(PurifyItemSelector)) OnAddonList(AddonEvent.PostSetup, null);
        
        GameResourceManager.AddToBlacklist(typeof(AutoAetherialReduction), "chara/action/normal/item_action.tmb");
    }

    public override void Uninit()
    {
        GameResourceManager.RemoveFromBlacklist(typeof(AutoAetherialReduction), "chara/action/normal/item_action.tmb");
        
        DService.AddonLifecycle.UnregisterListener(OnAddonList);
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        base.Uninit();
    }

    public override void ConfigUI()
    {
        ConflictKeyText();
    }

    public override void OverlayUI()
    {
        var addon = PurifyItemSelector;
        if (addon == null) return;

        var pos = new Vector2(addon->GetX() - ImGui.GetWindowSize().X, addon->GetY() + 6);
        ImGui.SetWindowPos(pos);

        ImGui.TextColored(LightSkyBlue, GetLoc("AutoAetherialReductionTitle"));

        ImGui.Separator();

        using (ImRaii.Disabled(TaskHelper.IsBusy))
        {
            if (ImGui.Button(GetLoc("Start")))
                StartAetherialReduction();
        }
            
        ImGui.SameLine();
        if (ImGui.Button(GetLoc("Stop"))) 
            TaskHelper.Abort();
    }

    private void StartAetherialReduction()
        => TaskHelper.Enqueue(ProcessAetherialReduction, "开始精选物品");

    private bool? ProcessAetherialReduction()
    {
        // 基本检查，与AutoDesynthesizeItems保持一致
        if (OccupiedInEvent) return false;
        if (!IsAddonAndNodesReady(PurifyItemSelector)) return false;

        // 检查环境
        if (IsEnvironmentBlockingOperation()) return false;
        
        // 获取列表组件和项目数量
        var itemAmount = PurifyItemSelector->AtkValues[9].Int;
        if (itemAmount == 0)
        {
            TaskHelper.Abort();
            return true;
        }
        
        // 恢复使用原始的Callback方法处理第一个项目，与原版保持一致
        Callback(PurifyItemSelector, true, 12, 0);
        
        // 再次将此方法入队以处理下一个物品
        TaskHelper.Enqueue(ProcessAetherialReduction);
        return true;
    }

    // 重命名并简化环境检查方法，减少冗余检查
    private bool IsEnvironmentBlockingOperation()
    {
        // 检查背包是否已满
        if (IsInventoryFull(BackpackInventories))
        {
            TaskHelper.Abort();
            return true;
        }

        // 检查玩家状态，移除OccupiedInEvent（已在ProcessAetherialReduction中检查）
        if (DService.Condition[ConditionFlag.Mounted] ||
            DService.Condition[ConditionFlag.InCombat] ||
            DService.Condition[ConditionFlag.Occupied39] ||
            DService.Condition[ConditionFlag.Casting])
        {
            TaskHelper.Abort();
            return true;
        }

        // 检查结果窗口是否已打开
        if (PurifyResult != null && PurifyResult->IsVisible)
        {
            return true;
        }

        return false;
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
        if (!Throttler.Throttle("AutoAetherialReduction", 100)) return;
        if (!IsAddonAndNodesReady(PurifyResult)) return;
        
        // 自动处理结果对话框
        Callback(PurifyResult, true, 0, 0);
    }
}
