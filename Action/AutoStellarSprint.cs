using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoStellarSprint : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoStellarSprintTitle"), // 自动月球冲刺
        Description = GetLoc("AutoStellarSprintDescription"),
        Category    = ModuleCategories.Action,
        Author      = ["Due"]
    };

    private const uint StellarSprint = 43357;
    private const uint SprintStatus  = 4398;

    public override void Init()
    {
        TaskHelper ??= new();

        DService.ClientState.TerritoryChanged += OnTerritoryChange;
        OnTerritoryChange(DService.ClientState.TerritoryType);
    }

    private void OnTerritoryChange(ushort zone)
    {
        TaskHelper.Abort();
        FrameworkManager.Unregister(OnFrameworkUpdate);

        if (!LuminaGetter.TryGetRow<TerritoryType>(zone, out var zoneData) || zoneData is not { TerritoryIntendedUse.RowId: 60 }) return;
        FrameworkManager.Register(OnFrameworkUpdate, throttleMS: 2_000);
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        TaskHelper.Abort();
        TaskHelper.Enqueue(UseSprint);
    }

    private bool? UseSprint()
    {
        if (!IsScreenReady() || BetweenAreas || OccupiedInEvent) return false;

        if (GameState.TerritoryIntendedUse != 60)
        {
            FrameworkManager.Unregister(OnFrameworkUpdate);
            return true;
        }
        
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return false;
        if (localPlayer->StatusManager.HasStatus(SprintStatus)) return true;
        
        var jobCategory = LuminaGetter.GetRow<ClassJob>(localPlayer->ClassJob)?.ClassJobCategory.RowId;
        if (jobCategory is not (32 or 33)) return true;
        
        return UseActionManager.UseActionLocation(ActionType.Action, StellarSprint);
    }

    public override void Uninit()
    {
        FrameworkManager.Unregister(OnFrameworkUpdate);
        DService.ClientState.TerritoryChanged -= OnTerritoryChange;
        
        base.Uninit();
    }
}
