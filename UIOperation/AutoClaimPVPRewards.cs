using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Info.Game.Enums;
using OmenTools.OmenService;
using AtkEventWrapper = OmenTools.OmenService.AtkEventWrapper;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoClaimPVPRewards : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoClaimPVPRewardsTitle"),
        Description = Lang.Get("AutoClaimPVPRewardsDescription"),
        Category    = ModuleCategory.UIOperation
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private AtkEventWrapper? claimAllEvent;

    protected override void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 5_000 };

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "PvpReward", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "PvpReward", OnAddon);
        if (PvpReward != null)
            OnAddon(AddonEvent.PostSetup, null);
    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
        OnAddon(AddonEvent.PreFinalize, null);
    }

    private void OnAddon(AddonEvent type, AddonArgs? args)
    {
        switch (type)
        {
            case AddonEvent.PostDraw:
                if (PvpReward == null) return;

                var closeButton = PvpReward->GetComponentButtonById(124);
                if (closeButton == null) return;

                if (claimAllEvent == null)
                {
                    closeButton->OwnerNode->ClearEvents();

                    claimAllEvent = new AtkEventWrapper
                    ((_, _, _, _) =>
                        {
                            for (var i = 0; i < 30; i++)
                            {
                                TaskHelper.Enqueue
                                (
                                    () =>
                                    {
                                        if (!IsTrophyCrystalAboutToReachLimit()) return;
                                        TaskHelper.Abort();
                                    },
                                    $"检查战利水晶数量_等级{i}"
                                );

                                TaskHelper.Enqueue
                                (
                                    () =>
                                    {
                                        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.CollectTrophyCrystal);
                                        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.CollectTrophyCrystal, 1);
                                    },
                                    $"领取系列赛奖励_等级{i}"
                                );

                                TaskHelper.DelayNext(10, $"等待 10 毫秒以便回包_等级{i}");
                            }
                        }
                    );
                    claimAllEvent.Add(PvpReward, (AtkResNode*)closeButton->OwnerNode, AtkEventType.ButtonClick);

                    closeButton->SetText(Lang.Get("AutoClaimPVPRewards-Button"));
                }

                closeButton->SetEnabledState(!TaskHelper.IsBusy);

                break;
            case AddonEvent.PreFinalize:
                claimAllEvent?.Dispose();
                claimAllEvent = null;
                break;
        }
    }

    private static bool IsTrophyCrystalAboutToReachLimit() =>
        InventoryManager.Instance()->GetInventoryItemCount(36656) > 1_9000;
}
