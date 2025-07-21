using System;
using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoCancelCast : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoCancelCastTitle"),
        Description = GetLoc("AutoCancelCastDescription"),
        Category    = ModuleCategories.Action,
    };
    
    private static readonly HashSet<ObjectKind> InvalidInterruptKinds = 
    [
        ObjectKind.Treasure, ObjectKind.Aetheryte, ObjectKind.GatheringPoint, ObjectKind.EventObj, ObjectKind.Mount,
        ObjectKind.Companion, ObjectKind.Retainer, ObjectKind.AreaObject, ObjectKind.HousingEventObject, ObjectKind.Cutscene,
        ObjectKind.MjiObject, ObjectKind.Ornament, ObjectKind.CardStand
    ];

    private static readonly CompSig CancelCastSig = new("48 83 EC 38 33 D2 C7 44 24 20 00 00 00 00 45 33 C9");
    private static readonly Action CancelCast = CancelCastSig.GetDelegate<Action>();

    private static HashSet<uint> TargetAreaActions { get; } = LuminaGetter.Get<Lumina.Excel.Sheets.Action>()
                                                                          .Where(x => x.TargetArea)
                                                                          .Select(x => x.RowId).ToHashSet();
    private static bool IsOnCasting;

    protected override void Init()
    {
        DService.Condition.ConditionChange += OnConditionChanged;
        FrameworkManager.Register(OnUpdate);
    }

    private static void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag is not (ConditionFlag.Casting or ConditionFlag.Casting87)) return;
        IsOnCasting = value;
    }

    private static void OnUpdate(IFramework _)
    {
        if (!IsOnCasting) return;
        if (!IsCasting)
        {
            IsOnCasting = false;
            return;
        }

        var player = DService.ObjectTable.LocalPlayer;
        if (player.CastActionType != ActionType.Action || TargetAreaActions.Contains(player.CastActionId))
        {
            IsOnCasting = false;
            return;
        }

        var obj = CharacterManager.Instance()->LookupBattleCharaByEntityId((uint)player.CastTargetObjectId);
        if (obj == null || 
            InvalidInterruptKinds.Contains(obj->ObjectKind) ||
            ActionManager.CanUseActionOnTarget(player.CastActionId, (GameObject*)obj)) 
            return;

        CancelCastCombined();
    }

    private static void CancelCastCombined()
    {
        if (!Throttler.Throttle("AutoCancelCast-CancelCast")) return;
        
        CancelCast();
        ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.CancelCast);
    }

    protected override void Uninit()
    {
        DService.Condition.ConditionChange -= OnConditionChanged;
        FrameworkManager.Unregister(OnUpdate);
    }
}
