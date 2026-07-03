using DailyRoutines.Common.KamiToolKit.Nodes;
using DailyRoutines.Extensions;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.OmenService;
using OmenTools.Threading.TaskHelper;

namespace DailyRoutines.ModulesPublic.Interface;

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

        public override TreeListCategoryNode CreateOverlayCategory(float width) =>
            CreateOverlayCategory
            (
                Lang.Get("AutoRetainerWork-GilsShare-Title"),
                width,
                CreateOverlayButtonRow(EnqueueRetainersGilShare, () => taskHelper?.Abort(), width)
            );

        private void EnqueueRetainersGilShare()
        {
            if (taskHelper.AbortByConflictKey(Module)) return;
            if (Module.IsAnyOtherWorkerBusy(typeof(GilsShareWorker))) return;

            var playerGil = LocalPlayerState.GetItemCount(1);

            if (playerGil >= MAX_PLAYER_GIL)
            {
                NotifyHelper.Instance().NotificationWarning
                (
                    Lang.Get("AutoRetainerWork-GilsShare-PlayerGilFull"),
                    Module.Info.Title
                );
                return;
            }

            var retainerManager = RetainerManager.Instance();
            var retainerCount   = retainerManager->GetRetainerCount();

            var totalGilAmount = 0U;
            for (var i = 0U; i < GetValidRetainerCount(_ => true, out _); i++)
                totalGilAmount += retainerManager->GetRetainerBySortedIndex(i)->Gil;

            var avgAmount = (uint)Math.Floor(totalGilAmount / (double)retainerCount);

            if (avgAmount <= 1)
            {
                NotifyHelper.Instance().NotificationInfo
                (
                    Lang.Get("AutoRetainerWork-GilsShare-NoNeedToShare"),
                    Module.Info.Title
                );
                return;
            }

            // 按金币盈余 / 不足分组
            var richRetainers = new List<(uint Index, uint Excess)>();
            var poorRetainers = new List<(uint Index, uint Deficit)>();

            for (var i = 0U; i < retainerCount; i++)
            {
                var gil = retainerManager->GetRetainerBySortedIndex(i)->Gil;
                if (gil > avgAmount)
                    richRetainers.Add((i, gil - avgAmount));
                else if (gil < avgAmount)
                    poorRetainers.Add((i, avgAmount - gil));
            }

            if (richRetainers.Count == 0)
            {
                NotifyHelper.Instance().NotificationInfo
                (
                    Lang.Get("AutoRetainerWork-GilsShare-NoNeedToShare"),
                    Module.Info.Title
                );
                return;
            }

            // 规划操作序列, 交替存取以避免玩家金币溢出
            var operations     = new List<(uint Index, uint Amount, bool IsWithdraw)>();
            var richIdx        = 0;
            var poorIdx        = 0;
            var pendingExcess  = richRetainers[0].Excess;
            var pendingDeficit = poorRetainers.Count > 0 ? poorRetainers[0].Deficit : 0U;

            while (richIdx < richRetainers.Count || poorIdx < poorRetainers.Count)
            {
                var madeProgress = false;

                // 先向金币不足的雇员存入金币, 降低玩家持有量以腾出取出空间
                while (poorIdx < poorRetainers.Count && playerGil > 0 && pendingDeficit > 0)
                {
                    var amount = Math.Min(playerGil, pendingDeficit);
                    operations.Add((poorRetainers[poorIdx].Index, amount, false));
                    playerGil      -= amount;
                    pendingDeficit -= amount;
                    madeProgress   =  true;

                    if (pendingDeficit == 0)
                    {
                        poorIdx++;
                        if (poorIdx < poorRetainers.Count)
                            pendingDeficit = poorRetainers[poorIdx].Deficit;
                    }
                }

                // 再从金币盈余的雇员取出金币
                if (richIdx < richRetainers.Count && pendingExcess > 0)
                {
                    var maxCanHold = MAX_PLAYER_GIL - playerGil;

                    if (maxCanHold > 0)
                    {
                        var amount = Math.Min(pendingExcess, maxCanHold);
                        operations.Add((richRetainers[richIdx].Index, amount, true));
                        playerGil     += amount;
                        pendingExcess -= amount;
                        madeProgress  =  true;

                        if (pendingExcess == 0)
                        {
                            richIdx++;
                            if (richIdx < richRetainers.Count)
                                pendingExcess = richRetainers[richIdx].Excess;
                        }
                    }
                }

                if (!madeProgress) break;
            }

            foreach (var (index, amount, isWithdraw) in operations)
                EnqueueRetainerGilOperation(index, amount, isWithdraw);

            taskHelper.Enqueue
            (
                () =>
                {
                    NotifyHelper.Instance().NotificationSuccess
                    (
                        Lang.Get("AutoRetainerWork-GilsShare-Complete"),
                        Module.Info.Title
                    );
                    return true;
                },
                "发送完成通知"
            );
        }

        private void EnqueueRetainerGilOperation(uint index, uint amount, bool isWithdraw)
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

                    if (!isWithdraw)
                        AddonBankEvent.SwitchMode();

                    AddonBankEvent.SetNumber(amount);
                    AddonBankEvent.ClickConfirm();
                    Bank->Close(true);
                    return true;
                },
                $"{(isWithdraw ? "取出" : "存入")} {amount} 金币 ({index} 号雇员)"
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
    }

    #region 常量

    private const uint MAX_PLAYER_GIL = 999_999_999U;

    #endregion
}
