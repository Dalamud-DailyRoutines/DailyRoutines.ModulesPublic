using DailyRoutines.Common.KamiToolKit.Nodes;
using DailyRoutines.Extensions;
using KamiToolKit.Nodes;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Threading.TaskHelper;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe partial class AutoRetainerWork
{
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

                            var gils = AddonBankEvent.RetainerGilAmount;

                            if (gils <= 0)
                                AddonBankEvent.ClickCancel();
                            else
                            {
                                AddonBankEvent.SetNumber((uint)gils);
                                AddonBankEvent.ClickConfirm();
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
}
