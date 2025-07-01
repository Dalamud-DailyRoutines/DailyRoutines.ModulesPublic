using DailyRoutines.Abstracts;
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

    public override void Init()
    {
        DService.Framework.Update += OnFrameworkUpdate;
    }

    public override void Uninit()
    {
        DService.Framework.Update -= OnFrameworkUpdate;
        base.Uninit();
    }

    private void OnFrameworkUpdate(object framework)
    {
        if (!IsValidState()) return;

        var localPlayer = DService.ObjectTable.LocalPlayer;
        if (localPlayer is null) return;

        var statusManager = localPlayer.ToStruct()->StatusManager;
        var statusIndex = statusManager.GetStatusIndex(StarContributorBuffId);
        
        if (statusIndex != -1)
            StatusManager.ExecuteStatusOff(StarContributorBuffId);
    }

    private static unsafe bool IsValidState() =>
        DService.ObjectTable.LocalPlayer != null &&
        !BetweenAreas &&
        !OccupiedInEvent &&
        IsScreenReady();
}
