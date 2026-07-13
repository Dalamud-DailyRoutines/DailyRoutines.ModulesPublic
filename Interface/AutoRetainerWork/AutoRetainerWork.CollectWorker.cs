using DailyRoutines.Common.KamiToolKit.Nodes;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Threading.TaskHelper;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe partial class AutoRetainerWork
{
    private class CollectWorker
    (
        AutoRetainerWork module
    ) : RetainerWorkerBase(module)
    {
        private TaskHelper? taskHelper;

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
                CreateOverlayCheckbox
                (
                    Lang.Get("AutoRetainerWork-Collect-AutoPriceAdjustAfterCollect"),
                    Module.config.AutoPriceAdjustAfterCollect,
                    isChecked =>
                    {
                        Module.config.AutoPriceAdjustAfterCollect = isChecked;
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
                {
                    taskHelper.Enqueue(LeaveRetainer, "确保所有雇员均已返回");

                    if (Module.config.AutoPriceAdjustAfterCollect)
                    {
                        taskHelper.Enqueue
                        (
                            () =>
                            {
                                if (taskHelper.AbortByConflictKey(Module)) return true;
                                DService.Instance().Framework.RunOnTick
                                (
                                    () =>
                                    {
                                        if (!Module.config.AutoPriceAdjustAfterCollect) return;

                                        var priceAdjustWorker = Array.Find(Module.workers, w => w is PriceAdjustWorker) as PriceAdjustWorker;
                                        priceAdjustWorker?.EnqueuePriceAdjustAll();
                                    }
                                );
                                return true;
                            },
                            "收取完成后触发自动改价"
                        );
                    }
                }
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
        
        private static readonly string[] VentureCompleteTexts =
        [
            "结束",
            "結束",
            "Complete",
            "完了",
            "완료",
            "Abgeschlossen",
            "Terminée"
        ];
    }
}
