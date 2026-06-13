using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models.Packets.Upstream;
using OmenTools.OmenService;
using OmenTools.OmenService.ImGuiZoneObject;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;

namespace DailyRoutines.ModulesPublic.Duty;

public unsafe class NeverreapHelper : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = Lang.Get("NeverreapHelperTitle"),
        Description = Lang.Get
        (
            "NeverreapHelperDescription",
            LuminaWrapper.GetContentName(33),                      // 空中神域不获岛
            LuminaWrapper.GetENPCName(1013331),                    // 托努瓦努                      
            LuminaWrapper.GetItemName(2001568),                    // 云卵石
            LuminaWrapper.GetBNPCName(3726),                       // 奴涅努克怪鸟
            LuminaWrapper.GetBNPCName(SHADOW_MONSTER_BNPC_NAME_ID) // 奴涅努克之影
        ),
        Category            = ModuleCategory.Duty,
        ModulesPrerequisite = ["AutoTalkSkip"]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true, AllDefaultEnabled = true };

    private Config config = null!;

    private ZoneIndicatorHandle? handle;

    protected override void Init()
    {
        config     =   Config.Load(this) ?? new();
        TaskHelper ??= new() { TimeoutMS = 30_000 };

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);

        handle = ImGuiZoneObjectIndicator.Instance().RegisterPermanent
        (
            420,
            () =>
            {
                var director = EventFramework.Instance()->GetContentDirector();
                if (director == null) return [];

                if (!DService.Instance().Condition[ConditionFlag.InCombat])
                    return [];

                var todos = director->DirectorTodos;
                if (todos[1].CurrentCount != 1)
                    return [];

                if (LocalPlayerState.DistanceTo2DSquared(FirstBossCenter) > 625)
                    return [];

                var gameObject = CharacterManager.Instance()->FindFirst(&FindFirstShadow);
                if (gameObject == null)
                    return [];

                return [IGameObject.Create((nint)gameObject)];
            },
            _ => new()
            {
                Text      = LuminaWrapper.GetBNPCName(SHADOW_MONSTER_BNPC_NAME_ID),
                TextScale = 1.6f
            }
        );

        return;

        static bool FindFirstShadow(BattleChara* chara) =>
            chara             != null                 &&
            chara->ObjectKind == ObjectKind.BattleNpc &&
            chara->NameId     == SHADOW_MONSTER_BNPC_NAME_ID;
    }

    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        FrameworkManager.Instance().Unreg(OnUpdate);

        handle?.Unregister();
        handle = null;
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("OnlyValidWhenSolo"), ref config.ValidWhenSolo))
            config.Save(this);
    }

    private static void OnZoneChanged(uint u)
    {
        FrameworkManager.Instance().Unreg(OnUpdate);

        if (GameState.TerritoryType != 420) return;

        FrameworkManager.Instance().Reg(OnUpdate, 500);
    }

    private static void OnUpdate(IFramework framework)
    {
        var director = EventFramework.Instance()->GetContentDirector();
        if (director == null) return;

        // 获取云卵石
        if (director->DirectorTodos[0].Type != TodoType.Number)
        {
            var manager = CharacterManager.Instance();

            foreach (var battleCharaPtr in manager->BattleCharas)
            {
                if (battleCharaPtr == null) continue;

                var battleChara = battleCharaPtr.Value;
                if (battleChara == null) continue;

                if (battleChara->ObjectKind != ObjectKind.EventNpc ||
                    battleChara->BaseId     != STONE_NPC_DATA_ID)
                    continue;

                if (battleChara->YalmDistanceFromPlayerX > 4 ||
                    battleChara->YalmDistanceFromPlayerZ > 4)
                    break;

                new EventStartPackt(battleChara->EntityId, STONE_NPC_EVENT_ID).Send();
                break;
            }
        }
    }

    private class Config : ModuleConfig
    {
        public bool ValidWhenSolo = true;
    }

    #region

    private const uint STONE_NPC_DATA_ID  = 1013331;
    private const uint STONE_NPC_EVENT_ID = 0x190007;

    private const uint SHADOW_MONSTER_BNPC_NAME_ID = 3727;

    private static readonly Vector2 FirstBossCenter = new(53.6f, 222.7f);

    #endregion
}
