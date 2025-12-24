using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoCancelStarContributor : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoCancelStarContributorTitle"),
        Description = GetLoc("AutoCancelStarContributorDescription"),
        Category    = ModuleCategories.General,
        Author      = ["Shiyuvi"]
    };
    
    private const uint StarContributorBuffID = 4409;

    protected override void Init()
    {
        DService.ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
    }

    protected override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        DService.ClientState.ClassJobChanged  -= OnClassJobChanged;
        
        FrameworkManager.Unreg(OnUpdate);
    }
    
    private static void OnZoneChanged(ushort zone)
    {
        FrameworkManager.Unreg(OnUpdate);
        DService.ClientState.ClassJobChanged -= OnClassJobChanged;
        
        if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.CosmicExploration) return;
        
        FrameworkManager.Reg(OnUpdate, throttleMS: 10_000);
        DService.ClientState.ClassJobChanged  += OnClassJobChanged;
    }
    
    private static void OnClassJobChanged(uint classJobID) => 
        OnUpdate(DService.Framework);

    private static void OnUpdate(IFramework framework)
    {
        if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.CosmicExploration)
        {
            FrameworkManager.Unreg(OnUpdate);
            return;
        }
        
        if (BetweenAreas || DService.ObjectTable.LocalPlayer is not { } localPlayer) return;
        
        var statusManager = localPlayer.ToStruct()->StatusManager;
        if (!statusManager.HasStatus(StarContributorBuffID)) return;
        
        StatusManager.ExecuteStatusOff(StarContributorBuffID);
    }
}
