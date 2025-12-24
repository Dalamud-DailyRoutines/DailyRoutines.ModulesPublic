using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoClaimPVPRewards : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoClaimPVPRewardsTitle"),
        Description = GetLoc("AutoClaimPVPRewardsDescription"),
        Category    = ModuleCategories.UIOperation,
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static AtkEventWrapper? ClaimAllEvent;
    
    protected override void Init()
    {
        TaskHelper ??= new() { TimeLimitMS = 5_000 };
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,   "PvpReward", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "PvpReward", OnAddon);
        if (PvpReward != null) 
            OnAddon(AddonEvent.PostSetup, null);
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
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
                
                if (ClaimAllEvent == null)
                {
                    closeButton->OwnerNode->ClearEvents();
                    
                    ClaimAllEvent = new AtkEventWrapper((_, _, _) =>
                    {
                        for (var i = 0; i < 30; i++)
                        {
                            TaskHelper.Enqueue(() =>
                                               {
                                                   if (!IsTrophyCrystalAboutToReachLimit()) return;
                                                   TaskHelper.Abort();
                                               }, $"CheckTCAmount_Rank{i}");

                            TaskHelper.Enqueue(
                                () =>
                                {
                                    ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.CollectTrophyCrystal);
                                    ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.CollectTrophyCrystal, 1);
                                },
                                $"ClaimTC_Rank{i}");

                            TaskHelper.DelayNext(10, $"Delay_Rank{i}");
                        }
                    });
                    ClaimAllEvent.Add(PvpReward, (AtkResNode*)closeButton->OwnerNode, AtkEventType.ButtonClick);
                    
                    closeButton->SetText(GetLoc("AutoClaimPVPRewards-Button"));
                }

                closeButton->SetEnabledState(!TaskHelper.IsBusy);
                
                break;
            case AddonEvent.PreFinalize:
                ClaimAllEvent?.Dispose();
                ClaimAllEvent = null;
                break;
        }
    }
    
    private static bool IsTrophyCrystalAboutToReachLimit() => 
        InventoryManager.Instance()->GetInventoryItemCount(36656) > 1_9000;
}
