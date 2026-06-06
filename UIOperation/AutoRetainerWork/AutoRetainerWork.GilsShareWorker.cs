using DailyRoutines.Extensions;
using FFXIVClientStructs.FFXIV.Client.Game;
using KamiToolKit.Nodes;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Threading.TaskHelper;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class AutoRetainerWork
{
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

                    var gils = AddonBankEvent.RetainerGilAmount;

                    if (gils < 0 || gils == avgAmount) // 金币恰好相等
                    {
                        AddonBankEvent.ClickCancel();
                        Bank->Close(true);
                        return true;
                    }

                    if (gils > avgAmount) // 雇员金币多于平均值
                    {
                        AddonBankEvent.SetNumber((uint)(gils - avgAmount));
                        AddonBankEvent.ClickConfirm();
                        Bank->Close(true);
                        return true;
                    }

                    // 雇员金币少于平均值
                    AddonBankEvent.SwitchMode();
                    AddonBankEvent.SetNumber((uint)(avgAmount - gils));
                    AddonBankEvent.ClickConfirm();
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
}
