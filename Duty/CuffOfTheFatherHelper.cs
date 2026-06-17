using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.OmenService.ZoneIndicator;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;

namespace DailyRoutines.ModulesPublic.Duty;

public unsafe class CuffOfTheFatherHelper : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = Lang.Get("CuffOfTheFatherHelperTitle"),
        Description = Lang.Get
        (
            "CuffOfTheFatherHelperDescription",
            LuminaWrapper.GetContentName(113), // 启动之章 2
            LuminaWrapper.GetBNPCName(3751),   // 7 号哥布林战车
            LuminaWrapper.GetBNPCName(2667)    // 炸弹
        ),
        Category = ModuleCategory.Duty
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private ZoneIndicatorHandle? handle;
    
    private List<nint> bombObjects = [];

    protected override void Init()
    {
        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);

        handle = ZoneIndicatorRenderer.Instance().RegPermanent
        (
            443,
            () => bombObjects,
            ptr => ((GameObject*)ptr)->Position,
            new()
            {
                TextGetter = ptr => new()
                {
                    Text      = $"→ {((GameObject*)ptr)->NameString} ←",
                    TextScale = 1.4f,
                    TextColor = ColorHelper.GetColor(518)
                },
                Surrounding = new()
                {
                    Type      = ZoneIndicatorSurrounding.Shape.Circle,
                    Radius    = 25,
                    Thickness = 4f
                }
            }
        );
    }

    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
        
        handle?.Unreg();
        handle = null;
        
        bombObjects.Clear();
    }

    private void OnZoneChanged(uint u)
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
        bombObjects.Clear();

        if (GameState.TerritoryType != 443) return;

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, "_EnemyList", OnAddon);
    }

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        var enemyListArray = AtkStage.Instance()->GetNumberArrayData(NumberArrayType.EnemyList);
        if (enemyListArray == null) return;

        var enemyCount = enemyListArray->IntArray[1];
        if (enemyCount < 1) return;
        
        var director = EventFramework.Instance()->GetContentDirector();
        if (director == null) return;

        // 还没到关底
        var todos = director->DirectorTodos;
        if (todos[2].CurrentCount != 1) return;

        List<nint> bombs = [];
        for (var i = 0; i < enemyCount; i++)
        {
            var offset = 8 + (i * 6);

            var gameObjectID = (ulong)enemyListArray->IntArray[offset];
            if (gameObjectID is 0 or 0xE0000000) continue;

            var chara = CharacterManager.Instance()->FindFirst
            (ptr =>
                {
                    var chara = (BattleChara*)ptr;
                    if (chara == null) return false;

                    return chara->GetGameObjectId() == gameObjectID         &&
                           chara->ObjectKind        == ObjectKind.BattleNpc &&
                           chara->BaseId            == 3865;
                }
            );
            
            bombs.Add((nint)chara);

            if (DService.Instance().Condition[ConditionFlag.Mounted])
                chara->TargetableStatus |= ObjectTargetableFlags.IsTargetable;
            else
                chara->TargetableStatus &= ~ObjectTargetableFlags.IsTargetable;
        }

        bombObjects = bombs;
    }
}
