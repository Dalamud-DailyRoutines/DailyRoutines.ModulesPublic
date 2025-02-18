using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game;
using System.Numerics;

namespace DailyRoutines.Modules;

public unsafe class AutoClaimPVPRewards : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoClaimPVPRewardsTitle"),
        Description = GetLoc("AutoClaimPVPRewardsDescription"),
        Category = ModuleCategories.UIOperation,
    };

    public override void Init()
    {
        TaskHelper ??= new() { TimeLimitMS = 5_000 };
        Overlay    ??= new(this);
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "PvpReward", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "PvpReward", OnAddon);

        if (PvpReward != null) OnAddon(AddonEvent.PostSetup, null);
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

    public override void OverlayUI()
    {
        var addon = PvpReward;
        if (addon == null)
        {
            Overlay.IsOpen = false;
            return;
        }
        
        var pos   = new Vector2(addon->GetX() + 6, addon->GetY() - ImGui.GetWindowHeight());
        ImGui.SetWindowPos(pos);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(ImGuiColors.DalamudYellow, Lang.Get("AutoClaimPVPRewardsTitle"));

        ImGui.SameLine();
        ImGui.Spacing();
        
        ImGui.SameLine();
        using (ImRaii.Disabled(TaskHelper.IsBusy))
        {
            if (ImGui.Button(Lang.Get("Start")))
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
                        () => ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.CollectTrophyCrystal),
                        $"ClaimTC_Rank{i}");

                    TaskHelper.DelayNext(200, $"Delay_Rank{i}");
                }
            }
        }
       
        ImGui.SameLine();
        if (ImGui.Button(Lang.Get("Stop")))
            TaskHelper.Abort();
    }

    private static bool IsTrophyCrystalAboutToReachLimit() 
        => InventoryManager.Instance()->GetInventoryItemCount(36656) > 1_9000;
}
