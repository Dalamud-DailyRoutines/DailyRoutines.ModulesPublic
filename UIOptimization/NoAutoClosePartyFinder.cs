using DailyRoutines.Abstracts;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System;
using Dalamud.Hooking;
using DailyRoutines.Managers;

namespace DailyRoutines.ModulesPublic;

public unsafe class NoAutoClosePartyFinder : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("NoAutoClosePartyFinderTitle", "防止招募板自动关闭"),
        Description = GetLoc("NoAutoClosePartyFinderDescription", "当小队成员变化时阻止招募板自动关闭。"),
        Category = ModuleCategories.UIOptimization,
        Author = ["Nyy", "YLCHEN"]
    };

    private delegate void LookingForGroupHideDelegate(AgentLookingForGroup* thisPtr);

    private static readonly CompSig LookingForGroupHideSig = new("48 89 5C 24 ?? 57 48 83 EC 20 83 A1 ?? ?? ?? ?? ??");

    private static Hook<LookingForGroupHideDelegate>? LookingForGroupHideHook;

    private static DateTime HookEndsAt;

    public override void Init()
    {
        LookingForGroupHideHook = LookingForGroupHideSig.GetHook<LookingForGroupHideDelegate>(LookingForGroupHideDetour);
        LookingForGroupHideHook?.Enable();

        LogMessageManager.Register(OnPreReceiveMessage);
    }

    private static void OnPreReceiveMessage(ref bool isPrevented, ref uint logMessageID)
    {
        if (logMessageID != 947) return;

        isPrevented = true;
        HookEndsAt = DateTime.UtcNow.AddSeconds(1);
    }

    private static void LookingForGroupHideDetour(AgentLookingForGroup* thisPtr)
    {
        if (DateTime.UtcNow < HookEndsAt) return;

        LookingForGroupHideHook?.Original(thisPtr);
    }

    public override void Uninit()
    {
        LogMessageManager.Unregister(OnPreReceiveMessage);
        base.Uninit();
    }
}