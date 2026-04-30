using System.Numerics;
using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.KamiToolKit.Addons;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Classes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;
using OmenTools.Threading.TaskHelper;
using Action = System.Action;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class AutoRetainerWork : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = Lang.Get("AutoRetainerWorkTitle"),
        Description         = Lang.Get("AutoRetainerWorkDescription"),
        Category            = ModuleCategory.UIOperation,
        ModulesPrerequisite = ["AutoTalkSkip", "AutoRefreshMarketSearchResult"]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private          Config            config            = null!;
    private readonly Throttler<string> retainerThrottler = new();
    private readonly HashSet<ulong>    playerRetainers   = [];

    private DRAutoRetainerWork? addon;

    private readonly RetainerWorkerBase[] workers;

    public AutoRetainerWork()
    {
        workers =
        [
            new CollectWorker(this),
            new EntrustDupsWorker(this),
            new GilsShareWorker(this),
            new GilsWithdrawWorker(this),
            new RefreshWorker(this),
            new TownDispatchWorker(this),
            new PriceAdjustWorker(this)
        ];
    }

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        foreach (var worker in workers)
            worker.Init();

        addon ??= new(this)
        {
            InternalName          = "DRAutoRetainerWork",
            Title                 = Info.Title,
            Size                  = new(260f, 320f),
            RememberClosePosition = true
        };
    }

    protected override void Uninit()
    {
        addon?.Dispose();
        addon = null;

        foreach (var worker in workers)
            worker.Uninit();
    }

    private class TownDispatchWorker
    (
        AutoRetainerWork module
    ) : RetainerWorkerBase(module)
    {
        private TaskHelper? TaskHelper;

        public override bool DrawConfigCondition() => true;

        public override bool IsWorkerBusy() => TaskHelper?.IsBusy ?? false;

        public override void Init() => TaskHelper ??= new() { TimeoutMS = 15_000 };

        public override void Uninit()
        {
            TaskHelper?.Abort();
            TaskHelper?.Dispose();
            TaskHelper = null;
        }

        public override void DrawConfig()
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("AutoRetainerWork-Dispatch-Title"));

            var imageState = ImageHelper.Instance().TryGetImage
            (
                "https://gh.atmoomen.top/StaticAssets/main/DailyRoutines/image/AutoRetainersDispatch-1.png",
                out var imageHandle
            );
            ImGui.SameLine();
            ImGui.TextDisabled(FontAwesomeIcon.InfoCircle.ToIconString());

            if (ImGui.IsItemHovered())
            {
                using (ImRaii.Tooltip())
                {
                    ImGui.TextUnformatted(Lang.Get("AutoRetainerWork-Dispatch-Description"));
                    if (imageState)
                        ImGui.Image(imageHandle.Handle, imageHandle.Size * 0.8f);
                }
            }

            using var indent = ImRaii.PushIndent();

            if (ImGui.Button(Lang.Get("Start")))
                EnqueueRetainersDispatch();

            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("Stop")))
                TaskHelper.Abort();
        }

        private void EnqueueRetainersDispatch()
        {
            if (TaskHelper.AbortByConflictKey(Module)) return;
            if (Module.IsAnyOtherWorkerBusy(typeof(TownDispatchWorker))) return;

            var addon = (AddonSelectString*)SelectString;
            if (addon == null) return;

            var entryCount = addon->PopupMenu.PopupMenu.EntryCount;
            if (entryCount - 1 <= 0) return;

            for (var i = 0; i < entryCount - 1; i++)
            {
                var tempI = i;
                TaskHelper.Enqueue
                (
                    () =>
                    {
                        if (TaskHelper.AbortByConflictKey(Module)) return true;
                        return AddonSelectStringEvent.Select(tempI);
                    },
                    $"点击第 {tempI} 位雇员, 拉起市场变更请求"
                );
                TaskHelper.Enqueue
                (
                    () =>
                    {
                        if (TaskHelper.AbortByConflictKey(Module)) return true;
                        return AddonSelectYesnoEvent.ClickYes();
                    },
                    "确认市场变更"
                );
            }
        }
    }

    private class GilsWithdrawWorker
    (
        AutoRetainerWork module
    ) : RetainerWorkerBase(module)
    {
        private TaskHelper? TaskHelper;

        public override bool DrawConfigCondition() => false;

        public override bool IsWorkerBusy() => TaskHelper?.IsBusy ?? false;

        public override void Init() => TaskHelper ??= new() { TimeoutMS = 15_000 };

        public override void Uninit()
        {
            TaskHelper?.Abort();
            TaskHelper?.Dispose();
            TaskHelper = null;
        }

        public override TreeListCategoryNode CreateOverlayCategory(float width) =>
            CreateOverlayCategory
            (
                Lang.Get("AutoRetainerWork-GilsWithdraw-Title"),
                width,
                CreateOverlayButtonRow(EnqueueRetainersGilWithdraw, () => TaskHelper?.Abort(), width)
            );

        private void EnqueueRetainersGilWithdraw()
        {
            if (TaskHelper.AbortByConflictKey(Module)) return;
            if (Module.IsAnyOtherWorkerBusy(typeof(GilsWithdrawWorker))) return;

            var count = GetValidRetainerCount(x => x.Gil > 0, out var validRetainers);
            if (count == 0) return;

            validRetainers.ForEach
            (index =>
                {
                    TaskHelper.Enqueue
                    (
                        () =>
                        {
                            if (TaskHelper.AbortByConflictKey(Module)) return true;
                            return Module.EnterRetainer(index);
                        },
                        $"选择进入 {index} 号雇员"
                    );
                    TaskHelper.Enqueue
                    (
                        () =>
                        {
                            if (TaskHelper.AbortByConflictKey(Module)) return true;
                            return AddonSelectStringEvent.Select(["金币管理", "金幣管理", "Entrust or withdraw gil", "ギルの受け渡し"]);
                        },
                        "选择进入金币管理"
                    );
                    TaskHelper.Enqueue
                    (
                        () =>
                        {
                            if (TaskHelper.AbortByConflictKey(Module)) return true;
                            if (!Bank->IsAddonAndNodesReady()) return false;

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
                        },
                        "取出所有的金币"
                    );
                    TaskHelper.Enqueue
                    (
                        () =>
                        {
                            if (TaskHelper.AbortByConflictKey(Module)) return true;
                            return LeaveRetainer();
                        },
                        "回到雇员列表"
                    );
                }
            );
        }
    }

    private class GilsShareWorker
    (
        AutoRetainerWork module
    ) : RetainerWorkerBase(module)
    {
        private TaskHelper? taskHelper;

        public override bool DrawConfigCondition() => false;

        public override bool IsWorkerBusy() => taskHelper?.IsBusy ?? false;

        public override void Init() => taskHelper ??= new() { TimeoutMS = 15_000 };

        public override void Uninit()
        {
            taskHelper?.Abort();
            taskHelper?.Dispose();
            taskHelper = null;
        }

        public override TreeListCategoryNode CreateOverlayCategory(float width)
        {
            CheckboxNode? methodOneNode   = null;
            CheckboxNode? methodTwoNode   = null;
            var           methodNodeWidth = width / 2f;

            methodOneNode = CreateOverlayCheckbox
            (
                $"{Lang.Get("Method")} 1",
                Module.config.GilsShareMethod == 0,
                isChecked =>
                {
                    if (!isChecked)
                    {
                        methodOneNode!.IsChecked = true;
                        return;
                    }

                    Module.config.GilsShareMethod = 0;
                    Module.config.Save(Module);
                    methodTwoNode!.IsChecked = false;
                },
                methodNodeWidth,
                Lang.Get("AutoRetainerWork-GilsShare-MethodsHelp")
            );

            methodTwoNode = CreateOverlayCheckbox
            (
                $"{Lang.Get("Method")} 2",
                Module.config.GilsShareMethod == 1,
                isChecked =>
                {
                    if (!isChecked)
                    {
                        methodTwoNode!.IsChecked = true;
                        return;
                    }

                    Module.config.GilsShareMethod = 1;
                    Module.config.Save(Module);
                    methodOneNode!.IsChecked = false;
                },
                methodNodeWidth,
                Lang.Get("AutoRetainerWork-GilsShare-MethodsHelp")
            );

            var methodRow = new HorizontalListNode
            {
                IsVisible          = true,
                Size               = new(width, 24f),
                ItemSpacing        = 4f,
                FitToContentHeight = true
            };
            methodRow.AddNode([methodOneNode, methodTwoNode]);

            return CreateOverlayCategory
            (
                Lang.Get("AutoRetainerWork-GilsShare-Title"),
                width,
                methodRow,
                CreateOverlayButtonRow(EnqueueRetainersGilShare, () => taskHelper?.Abort(), width)
            );
        }

        private void EnqueueRetainersGilShare()
        {
            if (taskHelper.AbortByConflictKey(Module)) return;
            if (Module.IsAnyOtherWorkerBusy(typeof(GilsShareWorker))) return;

            var retainerManager = RetainerManager.Instance();
            var retainerCount   = retainerManager->GetRetainerCount();

            var totalGilAmount = 0U;
            for (var i = 0U; i < GetValidRetainerCount(_ => true, out _); i++)
                totalGilAmount += retainerManager->GetRetainerBySortedIndex(i)->Gil;

            var avgAmount = (uint)Math.Floor(totalGilAmount / (double)retainerCount);
            if (avgAmount <= 1) return;

            switch (Module.config.GilsShareMethod)
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

        private void EnqueueRetainersGilShareMethodFirst(uint index, uint avgAmount)
        {
            taskHelper.Enqueue
            (
                () =>
                {
                    if (taskHelper.AbortByConflictKey(Module)) return true;
                    return Module.EnterRetainer(index);
                },
                $"选择进入 {index} 号雇员"
            );
            taskHelper.Enqueue
            (
                () =>
                {
                    if (taskHelper.AbortByConflictKey(Module)) return true;
                    return AddonSelectStringEvent.Select(["金币管理", "金幣管理", "Entrust or withdraw gil", "ギルの受け渡し"]);
                },
                "选择进入金币管理"
            );
            taskHelper.Enqueue
            (
                () =>
                {
                    if (taskHelper.AbortByConflictKey(Module)) return true;
                    if (!Bank->IsAddonAndNodesReady()) return false;

                    var retainerGils = Bank->AtkValues[6].Int;
                    var handler      = new ClickBank(Bank);

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
                },
                $"使用 1 号方法均分 {index} 号雇员的金币"
            );
            taskHelper.Enqueue
            (
                () =>
                {
                    if (taskHelper.AbortByConflictKey(Module)) return true;
                    return LeaveRetainer();
                },
                "回到雇员列表"
            );
        }

        private void EnqueueRetainersGilShareMethodSecond(uint index)
        {
            taskHelper.Enqueue
            (
                () =>
                {
                    if (taskHelper.AbortByConflictKey(Module)) return true;
                    return Module.EnterRetainer(index);
                },
                $"选择进入 {index} 号雇员"
            );
            taskHelper.Enqueue
            (
                () =>
                {
                    if (taskHelper.AbortByConflictKey(Module)) return true;
                    return AddonSelectStringEvent.Select(["金币管理", "金幣管理", "Entrust or withdraw gil", "ギルの受け渡し"]);
                },
                "选择进入金币管理"
            );
            taskHelper.Enqueue
            (
                () =>
                {
                    if (taskHelper.AbortByConflictKey(Module)) return true;
                    if (!Bank->IsAddonAndNodesReady()) return false;

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
                },
                $"使用 2 号方法取出 {index} 号雇员的金币"
            );

            // 回到雇员列表
            taskHelper.Enqueue
            (
                () =>
                {
                    if (taskHelper.AbortByConflictKey(Module)) return true;
                    return LeaveRetainer();
                },
                "回到雇员列表"
            );
        }
    }

    private class EntrustDupsWorker
    (
        AutoRetainerWork module
    ) : RetainerWorkerBase(module)
    {
        private TaskHelper? taskHelper;

        public override bool DrawConfigCondition() => false;

        public override bool IsWorkerBusy() => taskHelper?.IsBusy ?? false;

        public override void Init()
        {
            taskHelper ??= new() { TimeoutMS = 15_000 };

            DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerItemTransferList",     OnEntrustDupsAddons);
            DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerItemTransferProgress", OnEntrustDupsAddons);
        }

        public override void Uninit()
        {
            DService.Instance().AddonLifecycle.UnregisterListener(OnEntrustDupsAddons);

            taskHelper?.Abort();
            taskHelper?.Dispose();
            taskHelper = null;
        }

        public override TreeListCategoryNode CreateOverlayCategory(float width) =>
            CreateOverlayCategory
            (
                Lang.Get("AutoRetainerWork-EntrustDups-Title"),
                width,
                CreateOverlayButtonRow(EnqueueRetainersEntrust, () => taskHelper?.Abort(), width)
            );

        private void EnqueueRetainersEntrust()
        {
            if (taskHelper.AbortByConflictKey(Module)) return;
            if (Module.IsAnyOtherWorkerBusy(typeof(EntrustDupsWorker))) return;

            var count = GetValidRetainerCount(x => x.ItemCount > 0, out var validRetainers);
            if (count == 0) return;

            validRetainers.ForEach
            (index =>
                {
                    taskHelper.Enqueue
                    (
                        () =>
                        {
                            if (taskHelper.AbortByConflictKey(Module)) return true;
                            return Module.EnterRetainer(index);
                        },
                        $"选择进入 {index} 号雇员"
                    );
                    taskHelper.Enqueue
                    (
                        () =>
                        {
                            if (taskHelper.AbortByConflictKey(Module)) return true;
                            return AddonSelectStringEvent.Select(["道具管理", "Entrust or withdraw items", "アイテムの受け渡し"]);
                        },
                        "选择道具管理"
                    );
                    taskHelper.Enqueue
                    (
                        () =>
                        {
                            if (!Module.retainerThrottler.Throttle("AutoRetainerEntrustDups", 100)) return false;
                            if (taskHelper.AbortByConflictKey(Module)) return true;

                            var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer);
                            if (agent == null || !agent->IsAgentActive()) return false;
                            AgentId.Retainer.SendEvent(0, 0);
                            return true;
                        },
                        "选择同类道具合并提交"
                    );
                    taskHelper.DelayNext(500, "等待同类道具合并提交开始");
                    taskHelper.Enqueue
                    (
                        () =>
                        {
                            if (taskHelper.AbortByConflictKey(Module)) return true;
                            return ExitRetainerInventory();
                        },
                        "离开雇员背包界面"
                    );
                    taskHelper.Enqueue
                    (
                        () =>
                        {
                            if (taskHelper.AbortByConflictKey(Module)) return true;
                            return LeaveRetainer();
                        },
                        "回到雇员列表"
                    );
                }
            );
        }

        private void OnEntrustDupsAddons(AddonEvent type, AddonArgs args)
        {
            if (!taskHelper.IsBusy) return;

            switch (args.AddonName)
            {
                case "RetainerItemTransferList":
                    args.Addon.ToStruct()->Callback(1);
                    break;
                case "RetainerItemTransferProgress":
                    taskHelper.Enqueue
                    (
                        () =>
                        {
                            if (taskHelper.AbortByConflictKey(Module)) return true;
                            var addon = AddonHelper.GetByName("RetainerItemTransferProgress");
                            if (!addon->IsAddonAndNodesReady()) return false;

                            var progress = addon->AtkValues[2].Float;

                            if (progress == 1)
                            {
                                addon->Callback(-2);
                                addon->Close(true);
                                return true;
                            }

                            return false;
                        },
                        "等待同类道具合并提交完成",
                        weight: 2
                    );
                    break;
            }
        }
    }

    private class RefreshWorker
    (
        AutoRetainerWork module
    ) : RetainerWorkerBase(module)
    {
        private TaskHelper? taskHelper;

        public override bool DrawConfigCondition() => false;

        public override bool IsWorkerBusy() => taskHelper?.IsBusy ?? false;

        public override void Init() => taskHelper ??= new() { TimeoutMS = 15_000 };

        public override void Uninit()
        {
            taskHelper?.Abort();
            taskHelper?.Dispose();
            taskHelper = null;
        }

        public override TreeListCategoryNode CreateOverlayCategory(float width) =>
            CreateOverlayCategory
            (
                Lang.Get("AutoRetainerWork-Refresh-Title"),
                width,
                CreateOverlayButtonRow(EnqueueRetainersRefresh, () => taskHelper?.Abort(), width)
            );

        private void EnqueueRetainersRefresh()
        {
            if (Module.IsAnyOtherWorkerBusy(typeof(RefreshWorker))) return;

            var count = GetValidRetainerCount(_ => true, out var validRetainers);
            if (count == 0) return;

            validRetainers.ForEach
            (index =>
                {
                    taskHelper.Enqueue
                    (
                        () =>
                        {
                            if (taskHelper.AbortByConflictKey(Module)) return true;
                            return Module.EnterRetainer(index);
                        },
                        $"选择进入 {index} 号雇员"
                    );
                    taskHelper.Enqueue
                    (
                        () =>
                        {
                            if (taskHelper.AbortByConflictKey(Module)) return true;
                            return LeaveRetainer();
                        },
                        "回到雇员列表"
                    );
                }
            );
        }
    }

    private class CollectWorker
    (
        AutoRetainerWork module
    ) : RetainerWorkerBase(module)
    {
        private TaskHelper? taskHelper;

        private static readonly string[] VentureCompleteTexts = ["结束", "Complete", "完了"];

        public override bool DrawConfigCondition() => false;

        public override bool IsWorkerBusy() => taskHelper?.IsBusy ?? false;

        public override void Init()
        {
            taskHelper ??= new() { TimeoutMS = 15_000, ShowDebug = true };

            DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerList", OnRetainerList);
            DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,  "RetainerList", OnRetainerList);
        }

        public override void Uninit()
        {
            DService.Instance().AddonLifecycle.UnregisterListener(OnRetainerList);

            taskHelper?.Abort();
            taskHelper?.Dispose();
            taskHelper = null;
        }

        public override TreeListCategoryNode CreateOverlayCategory(float width) =>
            CreateOverlayCategory
            (
                Lang.Get("AutoRetainerWork-Collect-Title"),
                width,
                CreateOverlayCheckbox
                (
                    Lang.Get("AutoRetainerWork-Collect-AutoCollect"),
                    Module.config.AutoRetainerCollect,
                    isChecked =>
                    {
                        Module.config.AutoRetainerCollect = isChecked;
                        if (Module.config.AutoRetainerCollect)
                            EnqueueRetainersCollect();
                        Module.config.Save(Module);
                    },
                    width
                ),
                CreateOverlayButtonRow(EnqueueRetainersCollect, () => taskHelper?.Abort(), width)
            );

        private void OnRetainerList(AddonEvent type, AddonArgs args)
        {
            if (Module.IsAnyOtherWorkerBusy(typeof(CollectWorker))) return;

            switch (type)
            {
                case AddonEvent.PostSetup:
                    Module.ObtainPlayerRetainers();
                    if (taskHelper.IsBusy) return;
                    if (!Module.config.AutoRetainerCollect) break;
                    if (taskHelper.AbortByConflictKey(Module)) break;
                    EnqueueRetainersCollect();
                    break;
                case AddonEvent.PostDraw:
                    if (!Module.config.AutoRetainerCollect) break;
                    if (!Module.retainerThrottler.Throttle("AutoRetainerCollect-AFK", 5_000)) return;

                    DService.Instance().Framework.RunOnTick
                    (
                        () =>
                        {
                            if (taskHelper.IsBusy) return;
                            EnqueueRetainersCollect();
                        },
                        TimeSpan.FromSeconds(1)
                    );
                    break;
            }
        }

        private void EnqueueRetainersCollect()
        {
            if (taskHelper.AbortByConflictKey(Module)) return;

            var serverTime = Framework.GetServerTime();
            var count = GetValidRetainerCount
            (
                x => x.VentureId != 0 && x.VentureComplete != 0 && x.VentureComplete + 1 <= serverTime,
                out var validRetainers
            );

            if (count == 0)
            {
                if (taskHelper.IsBusy)
                    taskHelper.Enqueue(LeaveRetainer, "确保所有雇员均已返回");
                return;
            }

            foreach (var index in validRetainers)
            {
                taskHelper.Enqueue
                (
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(Module)) return true;
                        return Module.EnterRetainer(index);
                    },
                    $"选择进入 {index} 号雇员"
                );

                taskHelper.Enqueue
                (
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(Module)) return true;
                        if (!SelectString->IsAddonAndNodesReady()) return false;
                        if (RetainerList != null) return false;

                        if (!AddonSelectStringEvent.TryScanSelectStringText(VentureCompleteTexts, out var i))
                        {
                            taskHelper.Abort();
                            taskHelper.Enqueue(LeaveRetainer, "回到雇员列表");
                            return true;
                        }

                        return AddonSelectStringEvent.Select(i);
                    },
                    "确认雇员探险完成"
                );

                taskHelper.Enqueue
                (
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(Module)) return true;
                        if (!RetainerTaskResult->IsAddonAndNodesReady()) return false;

                        RetainerTaskResult->Callback(14);
                        return true;
                    },
                    "重新派遣雇员探险"
                );

                taskHelper.Enqueue
                (
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(Module)) return true;
                        if (!RetainerTaskAsk->IsAddonAndNodesReady()) return false;

                        RetainerTaskAsk->Callback(12);
                        return true;
                    },
                    "确认派遣雇员探险"
                );

                taskHelper.Enqueue
                (
                    () =>
                    {
                        if (taskHelper.AbortByConflictKey(Module)) return true;
                        return LeaveRetainer();
                    },
                    "回到雇员列表"
                );
            }

            taskHelper.Enqueue(EnqueueRetainersCollect, "重新检查是否有其他雇员需要收取");
        }
    }

    private abstract class RetainerWorkerBase
    (
        AutoRetainerWork module
    )
    {
        protected AutoRetainerWork Module = module;

        public abstract bool IsWorkerBusy();

        public virtual bool DrawConfigCondition() => true;

        public abstract void Init();

        public virtual TreeListCategoryNode? CreateOverlayCategory(float width) => null;

        public virtual void DrawConfig() { }

        public abstract void Uninit();

        protected static TreeListCategoryNode CreateOverlayCategory(string title, float width, params NodeBase[] nodes)
        {
            var contentNode = new VerticalListNode
            {
                IsVisible        = true,
                Size             = new(width, 0f),
                FitContents      = true,
                FitWidth         = true,
                FirstItemSpacing = 4f,
                ItemSpacing      = 4f
            };
            contentNode.AddNode(nodes);

            var categoryNode = new TreeListCategoryNode
            {
                IsVisible = true,
                Size      = new(width, 28f),
                String    = title
            };
            categoryNode.AddNode(contentNode);
            categoryNode.IsCollapsed = true;

            return categoryNode;
        }

        protected static HorizontalFlexNode CreateOverlayButtonRow(Action startAction, Action stopAction, float width)
        {
            var row = new HorizontalFlexNode
            {
                IsVisible      = true,
                Size           = new(width, 28f),
                AlignmentFlags = FlexFlags.FitContentHeight | FlexFlags.FitWidth,
                FitPadding     = 4f
            };
            row.AddNode
            (
                [
                    new TextButtonNode
                    {
                        IsVisible = true,
                        IsEnabled = true,
                        Size      = new(100f, 28f),
                        String    = Lang.Get("Start"),
                        OnClick   = startAction
                    },
                    new TextButtonNode
                    {
                        IsVisible = true,
                        IsEnabled = true,
                        Size      = new(100f, 28f),
                        String    = Lang.Get("Stop"),
                        OnClick   = stopAction
                    }
                ]
            );

            return row;
        }

        protected static CheckboxNode CreateOverlayCheckbox(string title, bool isChecked, Action<bool> onClick, float width, string? tooltip = null)
        {
            var node = new CheckboxNode
            {
                IsVisible = true,
                IsEnabled = true,
                Size      = new(width, 24f),
                IsChecked = isChecked,
                String    = title,
                OnClick   = onClick
            };

            if (!string.IsNullOrWhiteSpace(tooltip))
                node.TextTooltip = tooltip;

            return node;
        }

        protected static TextNode CreateOverlayText(string text, float width)
        {
            var node = new TextNode
            {
                IsVisible     = true,
                Size          = new(width, 24f),
                FontSize      = 14,
                String        = text,
                AlignmentType = AlignmentType.Left,
            };
            node.AutoAdjustTextSize();

            return node;
        }
    }

    public class ClickBank
    (
        AtkUnitBase* addon
    )
    {
        public void Switch() => addon->Callback(2, 0);

        public void DepositInput(uint amount) => addon->Callback(3, amount);

        public void Confirm() => addon->Callback(0, 0);

        public void Cancel() => addon->Callback(1, 0);

        public static ClickBank Using(AtkUnitBase* addon) => new(addon);
    }

    private class DRAutoRetainerWork
    (
        AutoRetainerWork module
    ) : AttachedAddon("RetainerList")
    {
        private TreeListNode? treeListNode;

        protected override Vector2 PositionOffset =>
            new(0f, 6f);

        protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
        {
            if (WindowNode is WindowNode windowNode)
                windowNode.CloseButtonNode.IsVisible = false;

            FlagHelper.UpdateFlag(ref addon->Flags1A1, 0x4,  true);
            FlagHelper.UpdateFlag(ref addon->Flags1A0, 0x80, true);
            FlagHelper.UpdateFlag(ref addon->Flags1A1, 0x40, true);
            FlagHelper.UpdateFlag(ref addon->Flags1A3, 0x1,  true);

            var width = ContentSize.X;
            treeListNode = new()
            {
                IsVisible               = true,
                Position                = ContentStartPosition,
                Size                    = new(width, 0f),
                CategoryVerticalSpacing = 4f,
                OnLayoutUpdate = height =>
                {
                    SetWindowSize(Size.X, ContentStartPosition.Y + height + 16f);
                    if (treeListNode == null) return;

                    treeListNode.Position = ContentStartPosition;
                    treeListNode.Height   = height;
                }
            };

            foreach (var worker in module.workers)
            {
                var categoryNode = worker.CreateOverlayCategory(width);
                if (categoryNode == null) continue;

                treeListNode.AddCategoryNode(categoryNode);
            }

            treeListNode.AttachNode(addon);
            
            treeListNode.RefreshLayout();
        }

        protected override bool CanCloseHostAddon(AtkUnitBase* hostAddon) => false;

        protected override bool CanOpenAddon => module.IsAnyWorkerBusy();
    }

    #region 模块界面

    protected override void ConfigUI()
    {
        foreach (var worker in workers)
        {
            if (!worker.DrawConfigCondition()) continue;

            worker.DrawConfig();

            ImGui.NewLine();
        }
    }

    #endregion

    #region 单独操作

    /// <summary>
    ///     打开指定索引对应的雇员
    /// </summary>
    private bool EnterRetainer(uint index)
    {
        if (!retainerThrottler.Throttle("EnterRetainer", 100)) return false;

        if (!RetainerList->IsAddonAndNodesReady()) return false;

        RetainerList->Callback(2, (int)index, 0, 0);
        return true;
    }

    /// <summary>
    ///     离开雇员界面
    /// </summary>
    private static bool LeaveRetainer()
    {
        // 如果存在
        if (SelectYesno->IsAddonAndNodesReady())
        {
            SelectYesno->Callback(0);
            return false;
        }

        if (SelectString->IsAddonAndNodesReady())
        {
            SelectString->Callback(-1);
            return false;
        }

        return RetainerList->IsAddonAndNodesReady();
    }

    /// <summary>
    ///     根据条件获取符合要求的雇员数量
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
    ///     离开雇员背包界面, 防止右键菜单残留
    /// </summary>
    private static bool ExitRetainerInventory()
    {
        var agent  = AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer);
        var agent2 = AgentModule.Instance()->GetAgentByInternalId(AgentId.Inventory);
        if (agent == null || agent2 == null || !agent->IsAgentActive()) return false;

        var addon  = RaptureAtkUnitManager.Instance()->GetAddonById((ushort)agent->GetAddonId());
        var addon2 = RaptureAtkUnitManager.Instance()->GetAddonById((ushort)agent2->GetAddonId());

        if (addon != null)
            addon->Close(true);
        if (addon2 != null)
            addon2->Callback(-1);

        AgentId.Retainer.SendEvent(0, -1);
        return true;
    }

    /// <summary>
    ///     搜索背包物品
    /// </summary>
    private static bool TrySearchItemInInventory(uint itemID, bool isHQ, out List<InventoryItem> foundItem)
    {
        foundItem = [];
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null) return false;

        foreach (var type in Inventories.Player)
        {
            var container = inventoryManager->GetInventoryContainer(type);
            if (container == null) return false;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0) continue;
                if (slot->ItemId == itemID &&
                    (!isHQ || isHQ && slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality)))
                    foundItem.Add(*slot);
            }
        }

        return foundItem.Count > 0;
    }

    /// <summary>
    ///     将雇员 ID 添加至列表
    /// </summary>
    private void ObtainPlayerRetainers()
    {
        var retainerManager = RetainerManager.Instance();
        if (retainerManager == null) return;

        for (var i = 0U; i < retainerManager->GetRetainerCount(); i++)
        {
            var retainer = retainerManager->GetRetainerBySortedIndex(i);
            if (retainer == null) break;

            playerRetainers.Add(retainer->RetainerId);
        }
    }

    /// <summary>
    ///     是否有其他 Worker 正在运行
    /// </summary>
    private bool IsAnyOtherWorkerBusy(Type current)
    {
        foreach (var worker in workers)
        {
            if (!worker.IsWorkerBusy()) continue;
            if (current == worker.GetType()) continue;
            
            return true;
        }

        return false;
    }
    
    /// <summary>
    ///     是否有 Worker 正在运行
    /// </summary>
    private bool IsAnyWorkerBusy()
    {
        foreach (var worker in workers)
        {
            if (!worker.IsWorkerBusy()) continue;
            
            return true;
        }

        return false;
    }

    #endregion

    #region 预定义

    private enum AdjustBehavior
    {
        固定值,
        百分比
    }

    [Flags]
    private enum AbortCondition
    {
        无        = 1,
        低于最小值    = 2,
        低于预期值    = 4,
        低于收购价    = 8,
        大于可接受降价值 = 16,
        高于预期值    = 32,
        高于最大值    = 64
    }

    private enum AbortBehavior
    {
        无,
        收回至雇员,
        收回至背包,
        出售至系统商店,
        改价至最小值,
        改价至预期值,
        改价至最高值
    }

    private enum SortOrder
    {
        上架顺序,
        物品ID,
        物品类型
    }

    private class PriceCheckCondition
    (
        AbortCondition                           condition,
        Func<ItemConfig, uint, uint, uint, bool> predicate
    )
    {
        public AbortCondition                           Condition { get; } = condition;
        public Func<ItemConfig, uint, uint, uint, bool> Predicate { get; } = predicate;
    }

    private static class PriceCheckConditions
    {
        private static readonly PriceCheckCondition[] Conditions =
        [
            new
            (
                AbortCondition.高于最大值,
                (cfg, _, modified, _) =>
                    modified > cfg.PriceMaximum
            ),

            new
            (
                AbortCondition.高于预期值,
                (cfg, _, modified, _) =>
                    modified > cfg.PriceExpected
            ),

            new
            (
                AbortCondition.大于可接受降价值,
                (cfg, orig, modified, _) =>
                    cfg.PriceMaxReduction != 0         &&
                    orig                  != 999999999 &&
                    orig - modified       > 0          &&
                    orig - modified       > cfg.PriceMaxReduction
            ),

            new
            (
                AbortCondition.低于收购价,
                (cfg, _, modified, _) =>
                    LuminaGetter.TryGetRow<Item>(cfg.ItemID, out var itemRow) &&
                    modified <= itemRow.PriceMid
            ),

            new
            (
                AbortCondition.低于最小值,
                (cfg, _, modified, _) =>
                    modified < cfg.PriceMinimum
            ),

            new
            (
                AbortCondition.低于预期值,
                (cfg, _, modified, _) =>
                    modified < cfg.PriceExpected
            )
        ];

        /// <summary>
        ///     获取所有价格检查条件
        /// </summary>
        public static IEnumerable<PriceCheckCondition> GetAll() => Conditions;

        /// <summary>
        ///     根据条件类型获取特定的检查条件
        /// </summary>
        public static PriceCheckCondition Get(AbortCondition condition) =>
            Conditions.FirstOrDefault(x => x.Condition == condition);
    }

    private class Config : ModuleConfig
    {
        public bool AutoPriceAdjustWhenNewOnSale = true;

        public bool AutoRetainerCollect = true;

        public int GilsShareMethod;

        public Dictionary<string, ItemConfig> ItemConfigs = new()
        {
            { new ItemKey(0, false).ToString(), new ItemConfig(0, false) },
            { new ItemKey(0, true).ToString(), new ItemConfig(0,  true) }
        };

        public SortOrder MarketItemsSortOrder       = SortOrder.上架顺序;
        public float     MarketItemsWindowFontScale = 0.8f;

        public bool SendPriceAdjustProcessMessage = true;
    }

    private class ItemKey : IEquatable<ItemKey>
    {
        public ItemKey() { }

        public ItemKey(uint itemID, bool isHQ)
        {
            ItemID = itemID;
            IsHQ   = isHQ;
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

    private class ItemConfig : IEquatable<ItemConfig>
    {
        public ItemConfig() { }

        public ItemConfig(uint itemID, bool isHQ)
        {
            ItemID = itemID;
            IsHQ   = isHQ;
            ItemName = itemID == 0
                           ? Lang.Get("AutoRetainerWork-PriceAdjust-CommonItemPreset")
                           : LuminaGetter.GetRow<Item>(ItemID)?.Name.ToString() ?? string.Empty;
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
        ///     单次上架数量 (设置为 0 以禁用)
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
