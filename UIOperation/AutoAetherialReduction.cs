using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using DailyRoutines.Windows;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Colors;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace DailyRoutines.Modules;

public unsafe class AutoAetherialReduction : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("AutoAetherialReductionTitle"),
        Description = GetLoc("AutoAetherialReductionDescription"),
        Category = ModuleCategories.UIOperation,
    };

    private static AtkUnitBase* AetherialReductionAddon = null;
    private static AtkUnitBase* AetherialReductionResultAddon = null;

    private static readonly InventoryType[] BackpackInventories =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4
    ];

    public override void Init()
    {
        TaskHelper ??= new TaskHelper { TimeLimitMS = 15_000 };
        Overlay ??= new Overlay(this);
        Overlay.Flags |= ImGuiWindowFlags.NoMove;

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "PurifyItemSelector", OnPurifyItemSelectorAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "PurifyItemSelector", OnPurifyItemSelectorAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "PurifyResult", OnPurifyResultAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "PurifyResult", OnPurifyResultAddon);
    }

    public override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnPurifyItemSelectorAddon);
        DService.AddonLifecycle.UnregisterListener(OnPurifyResultAddon);
        base.Uninit();
    }

    public override void ConfigUI()
    {
        ConflictKeyText();
    }

    public override void OverlayUI()
    {
        var addon = AetherialReductionAddon;
        if (addon == null) return;

        var pos = new Vector2(addon->X + 6, addon->Y - ImGui.GetWindowSize().Y + 6);
        ImGui.SetWindowPos(pos);

        using (FontManager.UIFont80.Push())
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(ImGuiColors.DalamudYellow, Lang.Get("AutoAetherialReductionTitle"));

            ImGui.SameLine();
            using (ImRaii.Disabled(TaskHelper.IsBusy))
            {
                if (ImGui.Button(Lang.Get("AutoAetherialReduction-DesynthesizeAll")))
                    StartAetherialReductionRoundAll();
            }

            ImGui.SameLine();
            ImGui.TextDisabled("|");

            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("Stop")))
                TaskHelper.Abort();

            ImGui.SameLine();
            ImGui.TextDisabled("|");
        }
    }

    private void StartAetherialReductionRoundAll()
        => TaskHelper.Enqueue(() => StartAetherialReductionRound(BackpackInventories), "开始精选全部物品");

    private bool? StartAetherialReductionRound(IReadOnlyList<InventoryType> types)
    {
        if (InterruptByConflictKey(TaskHelper, this)) return true;
        if (!Throttler.Throttle("AutoAetherialReduction")) return false;
        if (!IsEnvironmentValid()) return false;

        var addon = AetherialReductionAddon;
        if (addon == null || !addon->IsVisible) return false;

        try
        {
            // 获取列表组件
            var listComponent = addon->UldManager.NodeList[3]->GetAsAtkComponentList();
            if (listComponent == null) return false;
            
            // 获取列表长度（可精选物品数量）
            var listLength = listComponent->ListLength;
            if (listLength <= 0)
            {
                NotificationInfo("没有可精选的物品", "自动精选");
                return true;
            }

            TaskHelper.Enqueue(() => SelectFirstItem(addon), "选择物品");
            TaskHelper.DelayNext(250, "等待选择");
            TaskHelper.Enqueue(() => HandleResultDialog(), "处理结果");
            TaskHelper.DelayNext(500, "等待处理");
            
            // 进度通知
            TaskHelper.Enqueue(() => {
                NotificationInfo("正在精选物品...", "自动精选");
                return true;
            }, "通知进度");
            
            TaskHelper.Enqueue(() => StartAetherialReductionRound(types), "处理下一个物品");
            return true;
        }
        catch (Exception ex)
        {
            DService.Log.Error($"Error accessing reduction item list: {ex}");
            TaskHelper.Abort();
            return false;
        }
    }

    private bool? SelectFirstItem(AtkUnitBase* addon)
    {
        if (addon == null || !addon->IsVisible) return false;
        // 等待玩家空闲且结果窗口不可见
        if (DService.Condition[ConditionFlag.Occupied39] || DService.Condition[ConditionFlag.Casting] || 
            (AetherialReductionResultAddon != null && AetherialReductionResultAddon->IsVisible)) return false;

        try
        {
            var values = stackalloc AtkValue[2];
            values[0] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 12 };
            values[1] = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt, UInt = 0 };

            addon->FireCallback(2, values);
            return true;
        }
        catch (Exception ex)
        {
            DService.Log.Error($"SelectFirstItem Error: {ex}");
            TaskHelper.Abort();
            return false;
        }
    }

    private bool? HandleResultDialog()
    {
        var resultAddon = AetherialReductionResultAddon;
        if (resultAddon == null || !resultAddon->IsVisible) return false;
        
        try
        {
            // 直接关闭结果对话框
            resultAddon->Close(true);
            return true;
        }
        catch (Exception ex)
        {
            DService.Log.Error($"HandleResultDialog Error: {ex}");
            TaskHelper.Abort();
            return false;
        }
    }

    private bool IsEnvironmentValid()
    {
        if (IsInventoryFull(BackpackInventories))
        {
            TaskHelper.Abort();
            NotificationError("背包已满，无法继续精选", "自动精选");
            return false;
        }

        if (DService.Condition[ConditionFlag.Mounted])
        {
            TaskHelper.Abort();
            NotificationError("骑乘状态下无法执行此操作", "自动精选");
            return false;
        }

        if (OccupiedInEvent) return false;
        if (DService.Condition[ConditionFlag.InCombat]) return false;

        return true;
    }

    private static void OnPurifyResultAddon(AddonEvent type, AddonArgs args)
    {
        AetherialReductionResultAddon = type == AddonEvent.PostSetup ? (AtkUnitBase*)args.Addon : null;
    }

    private void OnPurifyItemSelectorAddon(AddonEvent type, AddonArgs args)
    {
        AetherialReductionAddon = type == AddonEvent.PostSetup ? (AtkUnitBase*)args.Addon : null;
        Overlay.IsOpen = AetherialReductionAddon != null;
        if (AetherialReductionAddon == null) TaskHelper.Abort(); 
    }
}
