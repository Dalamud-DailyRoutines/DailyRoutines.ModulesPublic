using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using TerritoryIntendedUse = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace DailyRoutines.ModulesPublic;

public class AutoStellarSprint : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoStellarSprintTitle"),
        Description = Lang.Get("AutoStellarSprintDescription"),
        Category    = ModuleCategory.Action,
        Author      = ["Due"]
    };

    protected override void Init()
    {
        DService.Instance().ClientState.TerritoryChanged += OnZoneChange;
        OnZoneChange(0);
    }

    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChange;
        FrameworkManager.Instance().Unreg(OnUpdate);
        CharacterStatusManager.Instance().Unreg(OnLoseStatus);
    }

    private static void OnZoneChange(ushort zone)
    {
        FrameworkManager.Instance().Unreg(OnUpdate);
        CharacterStatusManager.Instance().Unreg(OnLoseStatus);

        if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.CosmicExploration) return;

        FrameworkManager.Instance().Reg(OnUpdate, 2_000);
        CharacterStatusManager.Instance().RegLose(OnLoseStatus);
    }

    private static void OnLoseStatus(IBattleChara player, ushort id, ushort param, ushort stackCount, ulong sourceID)
    {
        if (player.EntityID != LocalPlayerState.EntityID) return;

        if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.CosmicExploration)
        {
            CharacterStatusManager.Instance().Unreg(OnLoseStatus);
            return;
        }

        CharacterStatusManager.Instance().Unreg(OnLoseStatus);

        FrameworkManager.Instance().Unreg(OnUpdate);
        FrameworkManager.Instance().Reg(OnUpdate, 2_000);
    }

    private static void OnUpdate(IFramework _)
    {
        if (DService.Instance().Condition.IsBetweenAreas || DService.Instance().Condition.IsOccupiedInEvent) return;

        if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.CosmicExploration)
        {
            FrameworkManager.Instance().Unreg(OnUpdate);
            return;
        }

        if (LocalPlayerState.HasStatus(SPRINT_STATUS, out var _))
        {
            FrameworkManager.Instance().Unreg(OnUpdate);

            CharacterStatusManager.Instance().Unreg(OnLoseStatus);
            CharacterStatusManager.Instance().RegLose(OnLoseStatus);
            return;
        }

        var jobCategory = LuminaGetter.GetRowOrDefault<ClassJob>(LocalPlayerState.ClassJob).ClassJobCategory.RowId;
        if (jobCategory is not (32 or 33)) return;

        UseActionManager.Instance().UseAction(ActionType.Action, STELLAR_SPRINT);
    }
    
    #region 常量

    private const uint STELLAR_SPRINT = 43357;
    private const uint SPRINT_STATUS  = 4398;

    #endregion
}
