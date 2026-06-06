using DailyRoutines.Extensions;
using KamiToolKit.Nodes;
using OmenTools.Threading.TaskHelper;

namespace DailyRoutines.ModulesPublic;

public partial class AutoRetainerWork
{
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
}
