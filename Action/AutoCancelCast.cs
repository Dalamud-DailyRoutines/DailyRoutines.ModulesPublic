using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace DailyRoutines.Modules;

public unsafe class AutoCancelCast : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoCancelCastTitle"),
        Description = GetLoc("AutoCancelCastDescription"),
        Category = ModuleCategories.Action,
    };

    private static readonly CompSig CancelCastSig = new("48 83 EC 38 33 D2 C7 44 24 20 00 00 00 00 45 33 C9");
    private static Action? CancelCast;

    private static HashSet<uint>? TargetAreaActions;
    private static bool IsOnCasting;

    private static readonly HashSet<ObjectKind> InvalidInterruptKinds = 
    [
        ObjectKind.Treasure, ObjectKind.Aetheryte, ObjectKind.GatheringPoint, ObjectKind.EventObj, ObjectKind.Mount,
        ObjectKind.Companion, ObjectKind.Retainer, ObjectKind.AreaObject, ObjectKind.HousingEventObject, ObjectKind.Cutscene,
        ObjectKind.MjiObject, ObjectKind.Ornament, ObjectKind.CardStand
    ];

    public override void Init()
    {
        CancelCast ??= Marshal.GetDelegateForFunctionPointer<Action>(CancelCastSig.ScanText());

        TargetAreaActions ??= LuminaCache.Get<Lumina.Excel.Sheets.Action>()
                                         .Where(x => x.TargetArea)
                                         .Select(x => x.RowId).ToHashSet();

        DService.Condition.ConditionChange += OnConditionChanged;
        FrameworkManager.Register(false, OnUpdate);
    }

    private static void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag is ConditionFlag.Casting or ConditionFlag.Casting87)
        {
            IsOnCasting = value;
        }
    }

    private static void OnUpdate(IFramework _)
    {
        if (!IsOnCasting) return;
        if (!IsCasting)
        {
            IsOnCasting = false;
            return;
        }

        var player = DService.ClientState.LocalPlayer;
        if (player.CastActionType != 1 || TargetAreaActions.Contains(player.CastActionId))
        {
            IsOnCasting = false;
            return;
        }

        var obj = (GameObject*)CharacterManager.Instance()->LookupBattleCharaByEntityId((uint)player.CastTargetObjectId);
        if (obj == null || InvalidInterruptKinds.Contains(obj->ObjectKind)) return;
        if (ActionManager.CanUseActionOnTarget(player.CastActionId, obj)) return;

        CancelCastCombined();
    }

    private static void CancelCastCombined()
    {
        CancelCast();
        ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.CancelCast);
        CancelCast();
        ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.CancelCast);
    }

    public override void Uninit()
    {
        DService.Condition.ConditionChange -= OnConditionChanged;
        FrameworkManager.Unregister(OnUpdate);
    }
}
