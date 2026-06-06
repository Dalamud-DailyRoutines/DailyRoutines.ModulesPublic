using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using KamiToolKit.Nodes;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Threading.TaskHelper;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class AutoRetainerWork
{
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
}
