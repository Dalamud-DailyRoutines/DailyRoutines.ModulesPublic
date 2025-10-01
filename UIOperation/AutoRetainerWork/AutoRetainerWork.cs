using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using DailyRoutines.Windows;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class AutoRetainerWork : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = GetLoc("AutoRetainerWorkTitle"),
        Description         = GetLoc("AutoRetainerWorkDescription"),
        Category            = ModuleCategories.UIOperation,
        ModulesPrerequisite = ["AutoTalkSkip", "AutoRefreshMarketSearchResult"]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private static Config ModuleConfig = null!;
    private static readonly Throttler<string> RetainerThrottler = new();
    private static readonly HashSet<ulong> PlayerRetainers = [];

    private static readonly RetainerWorkerBase[] Workers =
    [
        new CollectWorker(), new EntrustDupsWorker(), new GilsShareWorker(), new GilsWithdrawWorker(),
        new RefreshWorker(), new TownDispatchWorker(), new PriceAdjustWorker()
    ];

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new Config();
        Overlay ??= new Overlay(this);
        
        // 雇员列表
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerList", OnRetainerList);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "RetainerList", OnRetainerList);

        foreach (var worker in Workers)
            worker.Init();
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnRetainerList);

        foreach (var worker in Workers)
            worker.Uninit();

        base.Uninit();
    }

    #region 模块界面

    protected override void ConfigUI()
    {
        foreach (var worker in Workers)
        {
            if (!worker.DrawConfigCondition()) continue;

            worker.DrawConfig();
            ImGui.Spacing();
        }
    }

    protected override void OverlayUI()
    {
        var activeAddon = RetainerList != null ? RetainerList : null;
        if (activeAddon == null) return;

        var pos = new Vector2(activeAddon->GetX() - ImGui.GetWindowSize().X, activeAddon->GetY() + 6);
        ImGui.SetWindowPos(pos);

        ScaledDummy(200f, 0.1f);

        foreach (var worker in Workers)
        {
            if (!worker.DrawOverlayCondition(activeAddon->NameString)) continue;
            worker.DrawOverlay(activeAddon->NameString);
            ImGui.Spacing();
        }
    }

    #endregion

    #region 单独操作

    /// <summary>
    /// 打开指定索引对应的雇员
    /// </summary>
    private static bool EnterRetainer(uint index)
    {
        if (!RetainerThrottler.Throttle("EnterRetainer", 100)) return false;

        if (!IsAddonAndNodesReady(RetainerList)) return false;

        Callback(RetainerList, true, 2, (int)index, 0, 0);
        return true;
    }

    /// <summary>
    /// 离开雇员界面
    /// </summary>
    private static bool LeaveRetainer()
    {
        // 如果存在
        if (IsAddonAndNodesReady(SelectYesno))
        {
            Callback(SelectYesno, true, 0);
            return false;
        }

        if (IsAddonAndNodesReady(SelectString))
        {
            Callback(SelectString, true, -1);
            return false;
        }

        return IsAddonAndNodesReady(RetainerList);
    }

    /// <summary>
    /// 根据条件获取符合要求的雇员数量
    /// </summary>
    private static uint GetValidRetainerCount(Func<RetainerManager.Retainer, bool> predicateFunc, out List<uint> validRetainers)
    {
        validRetainers = [];

        var manager = RetainerManager.Instance();
        if (manager == null) return 0;

        var counter = 0U;
        for (var i = 0U; i < manager->GetRetainerCount(); i++)
        {
            var retainer = manager->GetRetainerBySortedIndex(i);
            if (retainer == null) continue;
            if (!predicateFunc(*retainer)) continue;

            validRetainers.Add(i);
            counter++;
        }

        return counter;
    }

    /// <summary>
    /// 离开雇员背包界面, 防止右键菜单残留
    /// </summary>
    private static bool ExitRetainerInventory()
    {
        var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer);
        var agent2 = AgentModule.Instance()->GetAgentByInternalId(AgentId.Inventory);
        if (agent == null || agent2 == null || !agent->IsAgentActive()) return false;

        var addon = RaptureAtkUnitManager.Instance()->GetAddonById((ushort)agent->GetAddonId());
        var addon2 = RaptureAtkUnitManager.Instance()->GetAddonById((ushort)agent2->GetAddonId());

        if (addon != null) 
            addon->Close(true);
        if (addon2 != null) 
            Callback(addon2, true, -1);

        SendEvent(AgentId.Retainer, 0, -1);
        return true;
    }

    /// <summary>
    /// 搜索背包物品
    /// </summary>
    private static bool TrySearchItemInInventory(uint itemID, bool isHQ, out List<InventoryItem> foundItem)
    {
        foundItem = [];
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null) return false;

        foreach (var type in InventoryTypes)
        {
            var container = inventoryManager->GetInventoryContainer(type);
            if (container == null) return false;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0) continue;
                if (slot->ItemId == itemID &&
                    (!isHQ || (isHQ && slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality))))
                    foundItem.Add(*slot);
            }
        }

        return foundItem.Count > 0;
    }

    /// <summary>
    /// 将雇员 ID 添加至列表
    /// </summary>
    private static void ObtainPlayerRetainers()
    {
        var retainerManager = RetainerManager.Instance();
        if (retainerManager == null) return;

        for (var i = 0U; i < retainerManager->GetRetainerCount(); i++)
        {
            var retainer = retainerManager->GetRetainerBySortedIndex(i);
            if (retainer == null) break;

            PlayerRetainers.Add(retainer->RetainerId);
        }
    }

    /// <summary>
    /// 是否有其他 Worker 正在运行
    /// </summary>
    private static bool IsAnyOtherWorkerBusy(Type current)
    {
        foreach (var worker in Workers)
        {
            if (!worker.IsWorkerBusy()) continue;
            if (current == worker.GetType()) continue;
            return true;
        }

        return false;
    }

    #endregion

    #region 界面监控

    // 雇员列表 (悬浮窗控制)
    private void OnRetainerList(AddonEvent type, AddonArgs args)
    {
        Overlay.IsOpen = type switch
        {
            AddonEvent.PostSetup => true,
            AddonEvent.PreFinalize => false,
            _ => Overlay.IsOpen
        };
    }

    #endregion

    public class TownDispatchWorker : RetainerWorkerBase
    {
        public override bool DrawConfigCondition() => true;
        public override bool IsWorkerBusy() => TaskHelper?.IsBusy ?? false;
        public override string RunningMessage() => TaskHelper?.CurrentTaskName ?? string.Empty;

        private static TaskHelper? TaskHelper;

        public override void Init() => TaskHelper ??= new() { TimeLimitMS = 15_000 };

        public override void Uninit()
        {
            TaskHelper?.Abort();
            TaskHelper?.Dispose();
            TaskHelper = null;
        }

        public override void DrawConfig()
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(KnownColor.RoyalBlue.ToVector4(), GetLoc("AutoRetainerWork-Dispatch-Title"));

            var imageState = ImageHelper.TryGetImage(
                "https://gh.atmoomen.top/StaticAssets/main/DailyRoutines/image/AutoRetainersDispatch-1.png",
                out var imageHandle);
            ImGui.SameLine();
            ImGui.TextDisabled(FontAwesomeIcon.InfoCircle.ToIconString());
            if (ImGui.IsItemHovered())
            {
                using (ImRaii.Tooltip())
                {
                    ImGui.Text(GetLoc("AutoRetainerWork-Dispatch-Description"));
                    if (imageState)
                        ImGui.Image(imageHandle.Handle, imageHandle.Size * 0.8f);
                }
            }

            using var indent = ImRaii.PushIndent();

            if (ImGui.Button(GetLoc("Start"))) 
                EnqueueRetainersDispatch();

            ImGui.SameLine();
            if (ImGui.Button(GetLoc("Stop"))) 
                TaskHelper.Abort();
        }

        private static void EnqueueRetainersDispatch()
        {
            if (InterruptByConflictKey(TaskHelper, Module)) return;
            if (IsAnyOtherWorkerBusy(typeof(TownDispatchWorker))) return;

            var addon = (AddonSelectString*)SelectString;
            if (addon == null) return;

            var entryCount = addon->PopupMenu.PopupMenu.EntryCount;
            if (entryCount - 1 <= 0) return;

            for (var i = 0; i < entryCount - 1; i++)
            {
                var tempI = i;
                TaskHelper.Enqueue(() =>
                {
                    if (InterruptByConflictKey(TaskHelper, Module)) return true;
                    return ClickSelectString(tempI);
                }, $"点击第 {tempI} 位雇员, 拉起市场变更请求");
                TaskHelper.Enqueue(() =>
                {
                    if (InterruptByConflictKey(TaskHelper, Module)) return true;
                    return ClickSelectYesnoYes();
                }, "确认市场变更");
            }
        }
    }

    public class GilsWithdrawWorker : RetainerWorkerBase
    {
        public override bool DrawConfigCondition() => false;
        public override bool DrawOverlayCondition(string activeAddonName) => activeAddonName == "RetainerList";
        public override bool IsWorkerBusy() => TaskHelper?.IsBusy ?? false;
        public override string RunningMessage() => TaskHelper?.CurrentTaskName ?? string.Empty;

        private static TaskHelper? TaskHelper;

        public override void Init() => TaskHelper ??= new() { TimeLimitMS = 15_000 };

        public override void Uninit()
        {
            TaskHelper?.Abort();
            TaskHelper?.Dispose();
            TaskHelper = null;
        }

        public override void DrawOverlay(string activeAddonName)
        {
            using var node = ImRaii.TreeNode(GetLoc("AutoRetainerWork-GilsWithdraw-Title"));
            if (!node) return;

            if (ImGui.Button(GetLoc("Start")))
                EnqueueRetainersGilWithdraw();

            ImGui.SameLine();
            if (ImGui.Button(GetLoc("Stop")))
                TaskHelper.Abort();
        }

        private static void EnqueueRetainersGilWithdraw()
        {
            if (InterruptByConflictKey(TaskHelper, Module)) return;
            if (IsAnyOtherWorkerBusy(typeof(GilsWithdrawWorker))) return;

            var count = GetValidRetainerCount(x => x.Gil > 0, out var validRetainers);
            if (count == 0) return;

            validRetainers.ForEach(index =>
            {
                TaskHelper.Enqueue(() =>
                                   {
                                       if (InterruptByConflictKey(TaskHelper, Module)) return true;
                                       return EnterRetainer(index);
                                   }, $"选择进入 {index} 号雇员");
                TaskHelper.Enqueue(() =>
                {
                    if (InterruptByConflictKey(TaskHelper, Module)) return true;
                    return ClickSelectString(["金币管理", "金幣管理", "Entrust or withdraw gil", "ギルの受け渡し"]);
                }, "选择进入金币管理");
                TaskHelper.Enqueue(() =>
                {
                    if (InterruptByConflictKey(TaskHelper, Module)) return true;
                    if (!IsAddonAndNodesReady(Bank)) return false;

                    var retainerGils = Bank->AtkValues[6].Int;
                    var handler      = new ClickBank(Bank);

                    if (retainerGils == 0)
                        handler.Cancel();
                    else
                    {
                        handler.DepositInput((uint)retainerGils);
                        handler.Confirm();
                    }
                    
                    Bank->Close(true);
                    return true;
                }, "取出所有的金币");
                TaskHelper.Enqueue(() =>
                {
                    if (InterruptByConflictKey(TaskHelper, Module)) return true;
                    return LeaveRetainer();
                }, "回到雇员列表");
            });
        }
    }

    public class GilsShareWorker : RetainerWorkerBase
    {
        public override bool DrawConfigCondition() => false;
        public override bool DrawOverlayCondition(string activeAddonName) => activeAddonName == "RetainerList";
        public override bool IsWorkerBusy() => TaskHelper?.IsBusy ?? false;
        public override string RunningMessage() => TaskHelper?.CurrentTaskName ?? string.Empty;

        private static TaskHelper? TaskHelper;

        public override void Init() => TaskHelper ??= new() { TimeLimitMS = 15_000 };

        public override void Uninit()
        {
            TaskHelper?.Abort();
            TaskHelper?.Dispose();
            TaskHelper = null;
        }

        public override void DrawOverlay(string activeAddonName)
        {
            using var node = ImRaii.TreeNode(GetLoc("AutoRetainerWork-GilsShare-Title"));
            if (!node) return;

            if (ImGui.RadioButton($"{GetLoc("Method")} 1", ref ModuleConfig.GilsShareMethod, 0))
                ModuleConfig.Save(Module);

            ImGui.SameLine();
            if (ImGui.RadioButton($"{GetLoc("Method")} 2", ref ModuleConfig.GilsShareMethod, 1))
                ModuleConfig.Save(Module);

            ImGuiOm.HelpMarker(GetLoc("AutoRetainerWork-GilsShare-MethodsHelp"));

            if (ImGui.Button(GetLoc("Start")))
                EnqueueRetainersGilShare();

            ImGui.SameLine();
            if (ImGui.Button(GetLoc("Stop")))
                TaskHelper.Abort();
        }

        private void EnqueueRetainersGilShare()
        {
            if (InterruptByConflictKey(TaskHelper, Module)) return;
            if (IsAnyOtherWorkerBusy(typeof(GilsShareWorker))) return;

            var retainerManager = RetainerManager.Instance();
            var retainerCount = retainerManager->GetRetainerCount();

            var totalGilAmount = 0U;
            for (var i = 0U; i < GetValidRetainerCount(_ => true, out _); i++)
                totalGilAmount += retainerManager->GetRetainerBySortedIndex(i)->Gil;

            var avgAmount = (uint)Math.Floor(totalGilAmount / (double)retainerCount);
            if (avgAmount <= 1) return;

            switch (ModuleConfig.GilsShareMethod)
            {
                case 0:
                    for (var i = 0U; i < retainerCount; i++)
                        EnqueueRetainersGilShareMethodFirst(i, avgAmount);

                    break;
                case 1:
                    for (var i = 0U; i < retainerCount; i++)
                        EnqueueRetainersGilShareMethodSecond(i);

                    for (var i = 0U; i < retainerCount; i++)
                        EnqueueRetainersGilShareMethodFirst(i, avgAmount);

                    break;
            }
        }

        private static void EnqueueRetainersGilShareMethodFirst(uint index, uint avgAmount)
        {
            TaskHelper.Enqueue(() =>
                               {
                                   if (InterruptByConflictKey(TaskHelper, Module)) return true;
                                   return EnterRetainer(index);
                               }, $"选择进入 {index} 号雇员");
            TaskHelper.Enqueue(() =>
            {
                if (InterruptByConflictKey(TaskHelper, Module)) return true;
                return ClickSelectString(["金币管理", "金幣管理", "Entrust or withdraw gil", "ギルの受け渡し"]);
            }, "选择进入金币管理");
            TaskHelper.Enqueue(() =>
            {
                if (InterruptByConflictKey(TaskHelper, Module)) return true;
                if (!IsAddonAndNodesReady(Bank)) return false;

                var retainerGils = Bank->AtkValues[6].Int;
                var handler = new ClickBank(Bank);

                if (retainerGils == avgAmount) // 金币恰好相等
                {
                    handler.Cancel();
                    Bank->Close(true);
                    return true;
                }

                if (retainerGils > avgAmount) // 雇员金币多于平均值
                {
                    handler.DepositInput((uint)(retainerGils - avgAmount));
                    handler.Confirm();
                    Bank->Close(true);
                    return true;
                }

                // 雇员金币少于平均值
                handler.Switch();
                handler.DepositInput((uint)(avgAmount - retainerGils));
                handler.Confirm();
                Bank->Close(true);
                return true;
            }, $"使用 1 号方法均分 {index} 号雇员的金币");
            TaskHelper.Enqueue(() =>
            {
                if (InterruptByConflictKey(TaskHelper, Module)) return true;
                return LeaveRetainer();
            }, "回到雇员列表");
        }

        private static void EnqueueRetainersGilShareMethodSecond(uint index)
        {
            TaskHelper.Enqueue(() =>
            {
                if (InterruptByConflictKey(TaskHelper, Module)) return true;
                return EnterRetainer(index);
            }, $"选择进入 {index} 号雇员");
            TaskHelper.Enqueue(() =>
            {
                if (InterruptByConflictKey(TaskHelper, Module)) return true;
                return ClickSelectString(["金币管理", "金幣管理", "Entrust or withdraw gil", "ギルの受け渡し"]);
            }, "选择进入金币管理");
            TaskHelper.Enqueue(() =>
            {
                if (InterruptByConflictKey(TaskHelper, Module)) return true;
                if (!IsAddonAndNodesReady(Bank)) return false;

                var retainerGils = Bank->AtkValues[6].Int;
                var handler = new ClickBank(Bank);

                if (retainerGils == 0)
                    handler.Cancel();
                else
                {
                    handler.DepositInput((uint)retainerGils);
                    handler.Confirm();
                }

                Bank->Close(true);
                return true;
            }, $"使用 2 号方法取出 {index} 号雇员的金币");

            // 回到雇员列表
            TaskHelper.Enqueue(() =>
            {
                if (InterruptByConflictKey(TaskHelper, Module)) return true;
                return LeaveRetainer();
            }, "回到雇员列表");
        }
    }

    public class EntrustDupsWorker : RetainerWorkerBase
    {
        public override bool DrawConfigCondition() => false;
        public override bool DrawOverlayCondition(string activeAddonName) => activeAddonName == "RetainerList";
        public override bool IsWorkerBusy() => TaskHelper?.IsBusy ?? false;
        public override string RunningMessage() => TaskHelper?.CurrentTaskName ?? string.Empty;

        private static TaskHelper? TaskHelper;

        public override void Init()
        {
            TaskHelper ??= new() { TimeLimitMS = 15_000 };

            DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerItemTransferList", OnEntrustDupsAddons);
            DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerItemTransferProgress", OnEntrustDupsAddons);
        }

        public override void Uninit()
        {
            DService.AddonLifecycle.UnregisterListener(OnEntrustDupsAddons);

            TaskHelper?.Abort();
            TaskHelper?.Dispose();
            TaskHelper = null;
        }

        public override void DrawOverlay(string activeAddonName)
        {
            using var node = ImRaii.TreeNode(GetLoc("AutoRetainerWork-EntrustDups-Title"));
            if (!node) return;

            if (ImGui.Button(GetLoc("Start")))
                EnqueueRetainersEntrust();

            ImGui.SameLine();
            if (ImGui.Button(GetLoc("Stop")))
                TaskHelper.Abort();
        }

        private static void EnqueueRetainersEntrust()
        {
            if (InterruptByConflictKey(TaskHelper, Module)) return;
            if (IsAnyOtherWorkerBusy(typeof(EntrustDupsWorker))) return;

            var count = GetValidRetainerCount(x => x.ItemCount > 0, out var validRetainers);
            if (count == 0) return;

            validRetainers.ForEach(index =>
            {
                TaskHelper.Enqueue(() =>
                                   {
                                       if (InterruptByConflictKey(TaskHelper, Module)) return true;
                                       return EnterRetainer(index);
                                   }, $"选择进入 {index} 号雇员");
                TaskHelper.Enqueue(() =>
                {
                    if (InterruptByConflictKey(TaskHelper, Module)) return true;
                    return ClickSelectString(["道具管理", "Entrust or withdraw items", "アイテムの受け渡し"]);
                }, "选择道具管理");
                TaskHelper.Enqueue(() =>
                {
                    if (!RetainerThrottler.Throttle("AutoRetainerEntrustDups", 100)) return false;
                    if (InterruptByConflictKey(TaskHelper, Module)) return true;

                    var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer);
                    if (agent == null || !agent->IsAgentActive()) return false;
                    SendEvent(AgentId.Retainer, 0, 0);
                    return true;
                }, "选择同类道具合并提交");
                TaskHelper.DelayNext(500, "等待同类道具合并提交开始");
                TaskHelper.Enqueue(() =>
                {
                    if (InterruptByConflictKey(TaskHelper, Module)) return true;
                    return ExitRetainerInventory();
                }, "离开雇员背包界面");
                TaskHelper.Enqueue(() =>
                {
                    if (InterruptByConflictKey(TaskHelper, Module)) return true;
                    return LeaveRetainer();
                }, "回到雇员列表");
            });
        }

        private static void OnEntrustDupsAddons(AddonEvent type, AddonArgs args)
        {
            if (!TaskHelper.IsBusy) return;
            switch (args.AddonName)
            {
                case "RetainerItemTransferList":
                    Callback((AtkUnitBase*)args.Addon.Address, true, 1);
                    break;
                case "RetainerItemTransferProgress":
                    TaskHelper.Enqueue(() =>
                    {
                        if (InterruptByConflictKey(TaskHelper, Module)) return true;
                        var addon = GetAddonByName("RetainerItemTransferProgress");
                        if (!IsAddonAndNodesReady(addon)) return false;

                        var progress = addon->AtkValues[2].Float;
                        if (progress == 1)
                        {
                            Callback(addon, true, -2);
                            addon->Close(true);
                            return true;
                        }

                        return false;
                    }, "等待同类道具合并提交完成", null, null, 2);
                    break;
            }
        }
    }

    public class RefreshWorker : RetainerWorkerBase
    {
        public override bool DrawConfigCondition() => false;
        public override bool DrawOverlayCondition(string activeAddonName) => activeAddonName == "RetainerList";
        public override bool IsWorkerBusy() => TaskHelper?.IsBusy ?? false;
        public override string RunningMessage() => TaskHelper?.CurrentTaskName ?? string.Empty;

        private static TaskHelper? TaskHelper;

        public override void Init() => TaskHelper ??= new() { TimeLimitMS = 15_000 };

        public override void Uninit()
        {
            TaskHelper?.Abort();
            TaskHelper?.Dispose();
            TaskHelper = null;
        }

        public override void DrawOverlay(string activeAddonName)
        {
            using var node = ImRaii.TreeNode(GetLoc("AutoRetainerWork-Refresh-Title"));
            if (!node) return;

            if (ImGui.Button(GetLoc("Start")))
                EnqueueRetainersRefresh();

            ImGui.SameLine();
            if (ImGui.Button(GetLoc("Stop")))
                TaskHelper.Abort();
        }

        private static void EnqueueRetainersRefresh()
        {
            if (IsAnyOtherWorkerBusy(typeof(RefreshWorker))) return;

            var count = GetValidRetainerCount(_ => true, out var validRetainers);
            if (count == 0) return;

            validRetainers.ForEach(index =>
            {
                TaskHelper.Enqueue(() =>
                {
                    if (InterruptByConflictKey(TaskHelper, Module)) return true;
                    return EnterRetainer(index);
                }, $"选择进入 {index} 号雇员");
                TaskHelper.Enqueue(() =>
                {
                    if (InterruptByConflictKey(TaskHelper, Module)) return true;
                    return LeaveRetainer();
                }, "回到雇员列表");
            });
        }
    }

    public class CollectWorker : RetainerWorkerBase
    {
        public override bool DrawConfigCondition() => false;
        public override bool DrawOverlayCondition(string activeAddonName) => activeAddonName == "RetainerList";
        public override bool IsWorkerBusy() => TaskHelper?.IsBusy ?? false;
        public override string RunningMessage() => TaskHelper?.CurrentTaskName ?? string.Empty;

        private static TaskHelper? TaskHelper;
        
        private static readonly string[] VentureCompleteTexts = ["结束", "Complete", "完了"];
        
        public override void Init()
        {
            TaskHelper ??= new() { TimeLimitMS = 15_000 };

            DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerList", OnRetainerList);
            DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "RetainerList", OnRetainerList);
        }

        public override void Uninit()
        {
            DService.AddonLifecycle.UnregisterListener(OnRetainerList);

            TaskHelper?.Abort();
            TaskHelper?.Dispose();
            TaskHelper = null;
        }

        public override void DrawOverlay(string activeAddonName)
        {
            using var node = ImRaii.TreeNode(GetLoc("AutoRetainerWork-Collect-Title"));
            if (!node) return;

            if (ImGui.Checkbox(GetLoc("AutoRetainerWork-Collect-AutoCollect"), ref ModuleConfig.AutoRetainerCollect))
            {
                if (ModuleConfig.AutoRetainerCollect) 
                    EnqueueRetainersCollect();
                ModuleConfig.Save(Module);
            }

            if (ImGui.Button(GetLoc("Start")))
                EnqueueRetainersCollect();

            ImGui.SameLine();
            if (ImGui.Button(GetLoc("Stop")))
                TaskHelper.Abort();
        }

        private static void OnRetainerList(AddonEvent type, AddonArgs args)
        {
            if (IsAnyOtherWorkerBusy(typeof(CollectWorker))) return;

            switch (type)
            {
                case AddonEvent.PostSetup:
                    ObtainPlayerRetainers();
                    if (TaskHelper.IsBusy) return;
                    if (!ModuleConfig.AutoRetainerCollect) break;
                    if (InterruptByConflictKey(TaskHelper, Module)) break;
                    EnqueueRetainersCollect();
                    break;
                case AddonEvent.PostDraw:
                    if (!ModuleConfig.AutoRetainerCollect) break;
                    if (!RetainerThrottler.Throttle("AutoRetainerCollect-AFK", 5_000)) return;

                    DService.Framework.RunOnTick(() =>
                    {
                        if (TaskHelper.IsBusy) return;
                        EnqueueRetainersCollect();
                    }, TimeSpan.FromSeconds(1));
                    break;
            }
        }

        private static void EnqueueRetainersCollect()
        {
            if (InterruptByConflictKey(TaskHelper, Module)) return;

            var serverTime = Framework.GetServerTime();
            var count = GetValidRetainerCount(
                x => x.VentureId != 0 && x.VentureComplete != 0 && x.VentureComplete + 1 <= serverTime,
                out var validRetainers);
            if (count == 0)
            {
                if (TaskHelper.IsBusy)
                    TaskHelper.Enqueue(() => LeaveRetainer(), "确保所有雇员均已返回");
                return;
            }

            foreach (var index in validRetainers)
            {
                TaskHelper.Enqueue(() =>
                {
                    if (InterruptByConflictKey(TaskHelper, Module)) return true;
                    return EnterRetainer(index);
                }, $"选择进入 {index} 号雇员");

                TaskHelper.Enqueue(() =>
                {
                    if (InterruptByConflictKey(TaskHelper, Module)) return true;
                    if (!IsAddonAndNodesReady(SelectString)) return false;
                    if (!TryScanSelectStringText(SelectString, VentureCompleteTexts, out var i))
                    {
                        TaskHelper.Abort();
                        TaskHelper.Enqueue(() => LeaveRetainer(), "回到雇员列表");
                        return true;
                    }
                    
                    return ClickSelectString(i);
                }, "确认雇员探险完成");

                TaskHelper.Enqueue(() =>
                {
                    if (InterruptByConflictKey(TaskHelper, Module)) return true;
                    if (!IsAddonAndNodesReady(RetainerTaskResult)) return false;
                    
                    Callback(RetainerTaskResult, true, 14);
                    return true;
                }, "重新派遣雇员探险");

                TaskHelper.Enqueue(() =>
                {
                    if (InterruptByConflictKey(TaskHelper, Module)) return true;
                    if (!IsAddonAndNodesReady(RetainerTaskAsk)) return false;
                    
                    Callback(RetainerTaskAsk, true, 12);
                    return true;
                }, "确认派遣雇员探险");

                TaskHelper.Enqueue(() =>
                {
                    if (InterruptByConflictKey(TaskHelper, Module)) return true;
                    return LeaveRetainer();
                }, "回到雇员列表");
            }

            TaskHelper.Enqueue(EnqueueRetainersCollect, "重新检查是否有其他雇员需要收取");
        }
    }

    public abstract class RetainerWorkerBase
    {
        protected static AutoRetainerWork Module => ModuleManager.GetModule<AutoRetainerWork>();

        public abstract bool IsWorkerBusy();

        public abstract string RunningMessage();

        public virtual bool DrawOverlayCondition(string activeAddonName) => true;

        public virtual bool DrawConfigCondition() => true;

        public abstract void Init();

        public virtual void DrawOverlay(string activeAddonName) { }

        public virtual void DrawConfig() { }

        public abstract void Uninit();
    }
    
    public class ClickBank(AtkUnitBase* Addon)
    {
        public void Switch() => Callback(Addon, true, 2, 0);

        public void DepositInput(uint amount) => Callback(Addon, true, 3, amount);

        public void Confirm() => Callback(Addon, true, 0, 0);

        public void Cancel() => Callback(Addon, true, 1, 0);

        public static ClickBank Using(AtkUnitBase* addon) => new(addon);
    }

    #region 预定义

    private static readonly InventoryType[] InventoryTypes =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3,
        InventoryType.Inventory4
    ];

    public enum AdjustBehavior
    {
        固定值,
        百分比
    }

    [Flags]
    public enum AbortCondition
    {
        无 = 1,
        低于最小值 = 2,
        低于预期值 = 4,
        低于收购价 = 8,
        大于可接受降价值 = 16,
        高于预期值 = 32,
        高于最大值 = 64
    }

    public enum AbortBehavior
    {
        无,
        收回至雇员,
        收回至背包,
        出售至系统商店,
        改价至最小值,
        改价至预期值,
        改价至最高值
    }
    
    public enum SortOrder
    {
        上架顺序,
        物品ID,
        物品类型,
    }
    
    private class PriceCheckCondition(
        AbortCondition                              condition,
        Func<ItemConfig, uint, uint, uint, bool> predicate)
    {
        public AbortCondition                           Condition { get; } = condition;
        public Func<ItemConfig, uint, uint, uint, bool> Predicate { get; } = predicate;
    }
    
    private static class PriceCheckConditions
    {
        private static readonly PriceCheckCondition[] conditions =
        [
            new(AbortCondition.高于最大值, 
                (cfg, _, modified, _) => 
                    modified > cfg.PriceMaximum),

            new(AbortCondition.高于预期值,
                (cfg, _, modified, _) => 
                    modified > cfg.PriceExpected),

            new(AbortCondition.大于可接受降价值,
                (cfg, orig, modified, _) => 
                    cfg.PriceMaxReduction != 0 && 
                    orig != 999999999          &&
                    orig - modified        > 0 && 
                    orig - modified        > cfg.PriceMaxReduction),

            new(AbortCondition.低于收购价,
                (cfg, _, modified, _) => 
                    LuminaGetter.TryGetRow<Item>(cfg.ItemID, out var itemRow) && 
                    modified <= itemRow.PriceMid),

            new(AbortCondition.低于最小值,
                (cfg, _, modified, _) => 
                    modified < cfg.PriceMinimum),

            new(AbortCondition.低于预期值,
                (cfg, _, modified, _) => 
                    modified < cfg.PriceExpected)
        ];

        /// <summary>
        /// 获取所有价格检查条件
        /// </summary>
        public static IEnumerable<PriceCheckCondition> GetAll() => conditions;

        /// <summary>
        /// 根据条件类型获取特定的检查条件
        /// </summary>
        public static PriceCheckCondition Get(AbortCondition condition) => 
            conditions.FirstOrDefault(x => x.Condition == condition);
    }

    private class Config : ModuleConfiguration
    {
        public Dictionary<string, ItemConfig> ItemConfigs = new()
        {
            { new ItemKey(0, false).ToString(), new ItemConfig(0, false) },
            { new ItemKey(0, true).ToString(), new ItemConfig(0, true) }
        };

        public int GilsShareMethod;

        public bool      SendPriceAdjustProcessMessage = true;
        public bool      AutoPriceAdjustWhenNewOnSale  = true;
        public float     MarketItemsWindowFontScale    = 0.8f;
        public SortOrder MarketItemsSortOrder          = SortOrder.上架顺序;

        public bool AutoRetainerCollect = true;
    }

    public class ItemKey : IEquatable<ItemKey>
    {
        public ItemKey() { }

        public ItemKey(uint itemID, bool isHQ)
        {
            ItemID = itemID;
            IsHQ = isHQ;
        }

        public uint ItemID { get; set; }
        public bool IsHQ   { get; set; }

        public bool Equals(ItemKey? other)
        {
            if (other is null || GetType() != other.GetType())
                return false;

            return ItemID == other.ItemID && IsHQ == other.IsHQ;
        }

        public override string ToString() => $"{ItemID}_{(IsHQ ? "HQ" : "NQ")}";

        public override bool Equals(object? obj) => Equals(obj as ItemKey);

        public override int GetHashCode() => HashCode.Combine(ItemID, IsHQ);

        public static bool operator ==(ItemKey? lhs, ItemKey? rhs)
        {
            if (lhs is null) return rhs is null;
            return lhs.Equals(rhs);
        }

        public static bool operator !=(ItemKey lhs, ItemKey rhs) => !(lhs == rhs);
    }

    public class ItemConfig : IEquatable<ItemConfig>
    {
        public ItemConfig() { }

        public ItemConfig(uint itemID, bool isHQ)
        {
            ItemID = itemID;
            IsHQ = isHQ;
            ItemName = itemID == 0
                           ? GetLoc("AutoRetainerWork-PriceAdjust-CommonItemPreset")
                           : LuminaGetter.GetRow<Item>(ItemID)?.Name.ExtractText() ?? string.Empty;
        }

        public uint   ItemID   { get; set; }
        public bool   IsHQ     { get; set; }
        public string ItemName { get; set; } = string.Empty;

        /// <summary>
        ///     改价行为
        /// </summary>
        public AdjustBehavior AdjustBehavior { get; set; } = AdjustBehavior.固定值;

        /// <summary>
        ///     改价具体值
        /// </summary>
        public Dictionary<AdjustBehavior, int> AdjustValues { get; set; } = new()
        {
            { AdjustBehavior.固定值, 1 },
            { AdjustBehavior.百分比, 10 }
        };

        /// <summary>
        ///     最低可接受价格 (最小值: 1)
        /// </summary>
        public int PriceMinimum { get; set; } = 100;

        /// <summary>
        ///     最大可接受价格
        /// </summary>
        public int PriceMaximum { get; set; } = 100000000;

        /// <summary>
        ///     预期价格 (最小值: PriceMinimum + 1)
        /// </summary>
        public int PriceExpected { get; set; } = 200;

        /// <summary>
        ///     最大可接受降价值 (设置为 0 以禁用)
        /// </summary>
        public int PriceMaxReduction { get; set; }
        
        /// <summary>
        /// 单次上架数量 (设置为 0 以禁用)
        /// </summary>
        public int UpshelfCount { get; set; }

        /// <summary>
        ///     意外情况逻辑
        /// </summary>
        public Dictionary<AbortCondition, AbortBehavior> AbortLogic { get; set; } = [];

        public bool Equals(ItemConfig? other)
        {
            if (other is null || GetType() != other.GetType())
                return false;

            return ItemID == other.ItemID && IsHQ == other.IsHQ;
        }

        public override bool Equals(object? obj) => Equals(obj as ItemConfig);

        public override int GetHashCode() => HashCode.Combine(ItemID, IsHQ);

        public static bool operator ==(ItemConfig? lhs, ItemConfig? rhs)
        {
            if (lhs is null) return rhs is null;
            return lhs.Equals(rhs);
        }

        public static bool operator !=(ItemConfig lhs, ItemConfig rhs) => !(lhs == rhs);
    }

    #endregion
}
