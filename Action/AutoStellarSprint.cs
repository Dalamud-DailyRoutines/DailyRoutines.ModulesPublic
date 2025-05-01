using DailyRoutines.Abstracts;
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
        Title = GetLoc("AutoStellarSprintTitle"), // 自动月球冲刺
        Description = GetLoc("AutoStellarSprintDescription"),
        Category = ModuleCategories.Action,
        Author = ["Due"]
    };

    private static readonly uint StellarSprint = 43357;
    private static readonly uint SprintStatus = 4398;

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

        if (!LuminaGetter.TryGetRow<TerritoryType>(zone, out var Item)) return;
        if (Item.TerritoryIntendedUse.RowId == 60)
            FrameworkManager.Register(OnFrameworkUpdate, throttleMS: 2_000);
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        TaskHelper.Abort();
        TaskHelper.Enqueue(UseSpirit);
    }

    private static bool? UseSpirit()
    {
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return false;
        var JobCategory = LuminaGetter.GetRow<ClassJob>(localPlayer->ClassJob)?.ClassJobCategory.RowId;
        if (JobCategory != 32 && JobCategory != 33) return true;
        if (!IsScreenReady() || BetweenAreas) return false;
        if (GameMain.Instance()->CurrentTerritoryIntendedUseId != 60) return true;
        if (localPlayer->StatusManager.HasStatus(SprintStatus)) return true;

        return UseActionManager.UseAction(ActionType.Action, StellarSprint);
    }

    public override void Uninit()
    {
        FrameworkManager.Unregister(OnFrameworkUpdate);
        DService.ClientState.TerritoryChanged -= OnTerritoryChange;
    }
}
