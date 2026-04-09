using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using OmenTools.Interop.Game.Models;

namespace DailyRoutines.ModulesPublic;

public class DisregardFollowQuest : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("DisregardFollowQuestTitle"),
        Description = Lang.Get("DisregardFollowQuestDescription"),
        Category    = ModuleCategory.System,
        Author      = ["Errer"]
    };
    
    public override ModulePermission Permission { get; } = new() { NeedAuth = true };
    
    private static readonly CompSig FollowTargetRecastSig = new("48 89 5C 24 ?? 57 48 81 EC ?? ?? ?? ?? F3 41 0F 10 00");
    private delegate        bool    FollowTargetRecastDelegate(nint a1, nint a2, nint a3, nint a4, nint a5, nint a6);
    private                 Hook<FollowTargetRecastDelegate>? FollowTargetRecastHook;
    
    protected override void Init()
    {
        FollowTargetRecastHook ??= FollowTargetRecastSig.GetHook<FollowTargetRecastDelegate>(FollowTargetRecastDetour);
        FollowTargetRecastHook.Enable();
    }

    private static bool FollowTargetRecastDetour(nint a1, nint a2, nint a3, nint a4, nint a5, nint a6) => false;
}
