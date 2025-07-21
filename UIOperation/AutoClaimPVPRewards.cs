using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoClaimPVPRewards : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoClaimPVPRewardsTitle"),
        Description = GetLoc("AutoClaimPVPRewardsDescription"),
        Category    = ModuleCategories.UIOperation,
    };

    protected override void Init()
    {
        TaskHelper ??= new() { TimeLimitMS = 5_000 };
        Overlay    ??= new(this);
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "PvpReward", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "PvpReward", OnAddon);
        if (PvpReward != null) 
            OnAddon(AddonEvent.PostSetup, null);
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        base.Uninit();
    }

    private void OnAddon(AddonEvent type, AddonArgs? args)
    {
        Overlay.IsOpen = type switch
        {
            AddonEvent.PostSetup   => true,
            AddonEvent.PreFinalize => false,
            _                      => Overlay.IsOpen
        };
    }

    protected override void OverlayUI()
    {
        var addon = PvpReward;
        if (addon == null)
        {
            Overlay.IsOpen = false;
            return;
        }
        
        if (!IsAddonAndNodesReady(addon)) return;
        
        var pos   = new Vector2(addon->GetX() + 6, addon->GetY() - ImGui.GetWindowHeight());
        ImGui.SetWindowPos(pos);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, GetLoc("AutoClaimPVPRewardsTitle"));

        ImGui.SameLine();
        ImGui.Spacing();
        
        ImGui.SameLine();
        using (ImRaii.Disabled(TaskHelper.IsBusy))
        {
            if (ImGui.Button(GetLoc("Start")))
            {
                var currentRank = PvpReward->AtkValues[7].UInt;
                if (currentRank <= 1 || IsTrophyCrystalAboutToReachLimit()) return;

                for (var i = 0; i < currentRank; i++)
                {
                    TaskHelper.Enqueue(() =>
                                       {
                                           if (IsTrophyCrystalAboutToReachLimit())
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
            }
        }
       
        ImGui.SameLine();
        if (ImGui.Button(GetLoc("Stop")))
            TaskHelper.Abort();
    }

    private static bool IsTrophyCrystalAboutToReachLimit() 
        => InventoryManager.Instance()->GetInventoryItemCount(36656) > 1_9000;
}
