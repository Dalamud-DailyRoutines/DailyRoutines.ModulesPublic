using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Duty;

public unsafe class Alphascape3Helper : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = Lang.Get("Alphascape3HelperTitle"),
        Description = Lang.Get
        (
            "Alphascape3HelperDescription",
            LuminaWrapper.GetContentName(589), // 欧米茄时空狭缝 阿尔法幻境3
            LuminaWrapper.GetBNPCName(7852),   // 雷力投射点
            LuminaWrapper.GetActionName(12911) // 欧米茄干扰器
        ),
        Category = ModuleCategory.Duty
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
        UseActionManager.Instance().Unreg(OnStartCast);
        UseActionManager.Instance().Unreg(OnCompleteCast);
    }

    private static void OnZoneChanged(uint u)
    {
        FrameworkManager.Instance().Unreg(OnUpdate);
        UseActionManager.Instance().Unreg(OnStartCast);
        UseActionManager.Instance().Unreg(OnCompleteCast);

        if (GameState.TerritoryType != DUTY_ZONE_ID) return;

        UseActionManager.Instance().RegPostCharacterStartCast(OnStartCast);
        UseActionManager.Instance().RegPostCharacterCompleteCast(OnCompleteCast);
    }

    private static void OnCompleteCast
    (
        bool         result,
        IBattleChara player,
        ActionType   type,
        uint         actionID,
        uint         spellID,
        GameObjectId animationTargetID,
        Vector3      location,
        float        rotation,
        short        lastUsedActionSequence,
        int          animationVariation,
        int          ballistaEntityID
    )
    {
        if (actionID != TRIANGLE_ATTACK_ACTION_ID) return;

        FrameworkManager.Instance().Unreg(OnUpdate);
    }

    private static void OnStartCast
    (
        bool         result,
        IBattleChara player,
        ActionType   type,
        uint         actionID,
        nint         a4,
        float        rotation,
        float        a6
    )
    {
        if (actionID != TRIANGLE_ATTACK_ACTION_ID) return;

        FrameworkManager.Instance().Unreg(OnUpdate);
        FrameworkManager.Instance().Reg(OnUpdate, 500);
    }

    private static void OnUpdate(IFramework framework)
    {
        var chara = CharacterManager.Instance()->FindFirst(&FindPoint);
        if (chara == null) return;

        UseActionManager.Instance().UseAction(ActionType.Action, QUEST_ACTION_ID, chara->EntityId);
        return;

        static bool FindPoint(BattleChara* chara) =>
            chara             != null                 &&
            chara->ObjectKind == ObjectKind.BattleNpc &&
            chara->BaseId     == POINT_DATA_ID;
    }

    #region 常量

    private const uint DUTY_ZONE_ID = 800;

    private const uint TRIANGLE_ATTACK_ACTION_ID = 12923;

    private const uint POINT_DATA_ID = 9638;

    private const uint QUEST_ACTION_ID = 12911;

    #endregion
}
