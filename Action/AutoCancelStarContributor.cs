using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;


namespace DailyRoutines.Modules;

public unsafe class AutoCancelStarContributor : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("AutoCancelStarContributorTitle"),
        Description = GetLoc("AutoCancelStarContributorDescription"),
        Category = ModuleCategories.General,
        Author = ["Shiyuvi"]
    };
    
    private const uint StarContributorBuffId = 4409;
    
    
    public override void Init() => FrameworkManager.Register(OnUpdate, throttleMS: 1500);
    
    public override void Uninit() => FrameworkManager.Unregister(OnUpdate);
    
    private static void OnUpdate(IFramework framework)
    {
        if (!IsValidState()) return;

        var localPlayer = DService.ObjectTable.LocalPlayer;
        if (localPlayer is null) return;

        var statusManager = localPlayer.ToStruct()->StatusManager;
        var statusIndex = statusManager.GetStatusIndex(StarContributorBuffId);
        
        if (statusIndex != -1)
            StatusManager.ExecuteStatusOff(StarContributorBuffId);
    }

    
    private static bool IsValidState() =>
               DService.ObjectTable.LocalPlayer != null && 
               GameState.TerritoryIntendedUse == 60 &&
               !BetweenAreas &&
               !OccupiedInEvent &&
               IsScreenReady();
}
