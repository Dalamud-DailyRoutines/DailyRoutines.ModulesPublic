using DailyRoutines.Abstracts;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI;
using System;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace DailyRoutines.ModulesPublic;

public unsafe class NoAutoClosePartyFinder : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("NoAutoClosePartyFinderTitle", "防止招募板自动关闭"),
        Description = GetLoc("NoAutoClosePartyFinderDescription", "当小队成员变化时阻止招募板自动关闭。"),
        Category = ModuleCategories.UIOptimization,
        Author      = ["Nyy,YLCHEN"]
    };

    private delegate void ShowLogMessageDelegate(RaptureLogModule* thisPtr, uint logMessageId);
    private delegate void LookingForGroupHideDelegate(AgentLookingForGroup* thisPtr);

    private static readonly CompSig showLogMessageSig = new("E8 ?? ?? ?? ?? 33 C0 EB ?? 73");
    private static readonly CompSig lookingForGroupHideSig = new("48 89 5C 24 ?? 57 48 83 EC 20 83 A1 ?? ?? ?? ?? ??");

    private Hook<ShowLogMessageDelegate>? showLogMessageHook;
    private Hook<LookingForGroupHideDelegate>? lfgHideHook;

    private DateTime hookEndsAt;

    public override void Init()
    {
        try
        {
            showLogMessageHook = showLogMessageSig.GetHook<ShowLogMessageDelegate>(ShowLogMessageDetour);
            lfgHideHook = lookingForGroupHideSig.GetHook<LookingForGroupHideDelegate>(LFGHideDetour);

            if (showLogMessageHook == null) DService.Log.Error("[NoAutoClosePartyFinder] Failed to hook ShowLogMessage.");
            if (lfgHideHook == null) DService.Log.Error("[NoAutoClosePartyFinder] Failed to hook LookingForGroupHide.");

            showLogMessageHook?.Enable();
            lfgHideHook?.Enable();
        }
        catch (Exception e)
        {
            DService.Log.Error(e, "[NoAutoClosePartyFinder] Failed to initialize");
        }
    }

    private void ShowLogMessageDetour(RaptureLogModule* thisPtr, uint logMessageId)
    {
        if (logMessageId == 947)
        {
            hookEndsAt = DateTime.UtcNow.AddSeconds(1);
            return;
        }
        showLogMessageHook?.Original(thisPtr, logMessageId);
    }

    private void LFGHideDetour(AgentLookingForGroup* thisPtr)
    {
        if (DateTime.UtcNow < hookEndsAt)
        {
            return;
        }
        lfgHideHook?.Original(thisPtr);
    }

    public override void Uninit()
    {
        showLogMessageHook?.Disable();
        showLogMessageHook?.Dispose();
        showLogMessageHook = null;

        lfgHideHook?.Disable();
        lfgHideHook?.Dispose();
        lfgHideHook = null;

        base.Uninit();
    }
}