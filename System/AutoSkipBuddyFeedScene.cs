using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DailyRoutines.Modules;

public unsafe class AutoSkipBuddyFeedScene : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title       = GetLoc("AutoSkipBuddyFeedSceneTitle"),
        Description = GetLoc("AutoSkipBuddyFeedSceneDescription"),
        Category    = ModuleCategories.System
    };

    private static readonly CompSig PlayFeedBuddySceneSig =
        new("E8 ?? ?? ?? ?? 48 8B 5C 24 ?? 48 8D 4C 24 ?? E8 ?? ?? ?? ?? 33 C0 48 83 C4 ?? C3 CC CC CC CC CC CC CC CC CC CC CC 48 83 EC");
    private delegate void PlayFeedBuddySceneDelegate(HousingManager* manager);
    private static Hook<PlayFeedBuddySceneDelegate>? PlayFeedBuddySceneHook;

    public override void Init()
    {
        PlayFeedBuddySceneHook ??= PlayFeedBuddySceneSig.GetHook<PlayFeedBuddySceneDelegate>(PlayFeedBuddySceneDetour);
        PlayFeedBuddySceneHook.Enable();
    }
    
    private static void PlayFeedBuddySceneDetour(HousingManager* manager) { }
}
