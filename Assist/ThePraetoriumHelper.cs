using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.OmenService;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic;

public unsafe class ThePraetoriumHelper : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("ThePraetoriumHelperTitle"),
        Description = Lang.Get("ThePraetoriumHelperDescription"),
        Category    = ModuleCategory.Assist,
        Author      = ["逆光"]
    };

    protected override void Init()
    {
        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
    }

    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        FrameworkManager.Instance().Unreg(OnUpdate);
    }

    private static void OnZoneChanged(uint u)
    {
        FrameworkManager.Instance().Unreg(OnUpdate);
        if (GameState.TerritoryType != 1044) return;

        FrameworkManager.Instance().Reg(OnUpdate, 1000);
    }

    private static void OnUpdate(IFramework framework)
    {
        if (!Throttler.Shared.Throttle("ThePraetoriumHelper-OnUpdate", 1_000)) return;

        if (GameState.TerritoryType != 1044)
        {
            FrameworkManager.Instance().Unreg(OnUpdate);
            return;
        }

        if (!DService.Instance().Condition[ConditionFlag.Mounted]                      ||
            DService.Instance().ObjectTable.LocalPlayer                        == null ||
            ActionManager.Instance()->GetActionStatus(ActionType.Action, 1128) != 0)
            return;

        var target = GetMostCanTargetObjects();
        if (target == null) return;

        UseActionManager.Instance().UseActionLocation(ActionType.Action, 1128, location: target.Position);
    }

    private static IGameObject? GetMostCanTargetObjects()
    {
        var allTargets = DService.Instance().ObjectTable.SearchObjects
            (o => o.IsTargetable && ActionManager.CanUseActionOnTarget(7, o.ToStruct()), IObjectTable.CharactersRange).ToList();
        if (allTargets.Count <= 0) return null;

        IGameObject? preObjects         = null;
        var          preObjectsAoECount = 0;

        foreach (var b in allTargets)
        {
            if (Vector3.DistanceSquared(DService.Instance().ObjectTable.LocalPlayer.Position, b.Position) - b.HitboxRadius > 900) continue;

            var aoeCount = GetTargetAoECount(b, allTargets);

            if (aoeCount > preObjectsAoECount)
            {
                preObjectsAoECount = aoeCount;
                preObjects         = b;
            }
        }

        return preObjects;
    }

    private static int GetTargetAoECount(IGameObject target, IEnumerable<IGameObject> allTarget)
    {
        var count = 0;

        foreach (var b in allTarget)
        {
            if (Vector3.DistanceSquared(target.Position, b.Position) - b.HitboxRadius <= 36)
                count++;
        }

        return count;
    }
}
