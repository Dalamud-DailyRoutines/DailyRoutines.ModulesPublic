using System;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace DailyRoutines.ModulesPublic;

public unsafe class NoAutoClosePartyFinder : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("NoAutoClosePartyFinderTitle"),
        Description = GetLoc("NoAutoClosePartyFinderDescription"),
        Category    = ModuleCategories.UIOptimization,
        Author      = ["Nyy", "YLCHEN"]
    };

    private delegate        void                               LookingForGroupHideDelegate(AgentLookingForGroup* agent);
    private static readonly CompSig                            LookingForGroupHideSig = new("48 89 5C 24 ?? 57 48 83 EC 20 83 A1 ?? ?? ?? ?? ??");
    private static          Hook<LookingForGroupHideDelegate>? LookingForGroupHideHook;

    private static DateTime LastPartyMemberChangeTime;
    private static DateTime LastViewTime;

    protected override void Init()
    {
        LookingForGroupHideHook = LookingForGroupHideSig.GetHook<LookingForGroupHideDelegate>(LookingForGroupHideDetour);
        LookingForGroupHideHook.Enable();

        LogMessageManager.Register(OnPreReceiveMessage);
    }

    private static void OnPreReceiveMessage(ref bool isPrevented, ref uint logMessageID)
    {
        if (logMessageID != 947) return;
        
        isPrevented = true;
        
        LastPartyMemberChangeTime = DateTime.UtcNow.AddSeconds(1);
        if (IsAddonAndNodesReady(LookingForGroupDetail))
            LastViewTime = DateTime.UtcNow.AddSeconds(1);
    }

    private static void LookingForGroupHideDetour(AgentLookingForGroup* agent)
    {
        if (DateTime.UtcNow < LastPartyMemberChangeTime)
        {
            if (DateTime.UtcNow < LastViewTime)
            {
                if (IsAddonAndNodesReady(LookingForGroupDetail))
                    LookingForGroupDetail->Close(true);

                DService.Framework.RunOnTick(() => agent->OpenListing(agent->LastViewedListing.ListingId), TimeSpan.FromMilliseconds(100));
            }
            
            return;
        }
        
        LookingForGroupHideHook.Original(agent); 
    }

    protected override void Uninit()
    {
        LogMessageManager.Unregister(OnPreReceiveMessage);
        base.Uninit();
    }
}
