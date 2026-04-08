using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.Interop.Game.Models;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoSkipBuddyFeedScene : ModuleBase
{
    private static readonly CompSig PlayFeedBuddySceneSig =
        new("E8 ?? ?? ?? ?? 48 8B 5C 24 ?? 48 8D 4C 24 ?? E8 ?? ?? ?? ?? 33 C0 48 83 C4 ?? C3 CC CC CC CC CC CC CC CC CC CC CC 48 83 EC");

    private static Hook<PlayFeedBuddySceneDelegate>? PlayFeedBuddySceneHook;

    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoSkipBuddyFeedSceneTitle"),
        Description = Lang.Get("AutoSkipBuddyFeedSceneDescription"),
        Category    = ModuleCategory.System
    };

    protected override void Init()
    {
        PlayFeedBuddySceneHook ??= PlayFeedBuddySceneSig.GetHook<PlayFeedBuddySceneDelegate>(PlayFeedBuddySceneDetour);
        PlayFeedBuddySceneHook.Enable();
    }

    private static void PlayFeedBuddySceneDetour(HousingManager* manager) { }

    private delegate void PlayFeedBuddySceneDelegate(HousingManager* manager);
}
