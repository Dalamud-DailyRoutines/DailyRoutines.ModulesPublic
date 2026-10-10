using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.OmenService.ZoneIndicator;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;

namespace DailyRoutines.ModulesPublic.Duty;

public unsafe class ChrysalisHelper : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("ChrysalisHelperTitle"),
        Description = Lang.Get("ChrysalisHelperDescription"),
        Category    = ModuleCategory.Duty
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private ZoneIndicatorHandle? magicHandle;
    private ZoneIndicatorHandle? physicalHandle;
    private ZoneIndicatorHandle? normalHandle;

    private List<nint> magicBalls    = [];
    private List<nint> physicalBalls = [];
    private List<nint> normalBalls   = [];

    protected override void Init()
    {
        magicHandle = ZoneIndicatorRenderer.Instance().RegPermanent
        (
            ZONE_ID,
            () => magicBalls,
            ptr => ((GameObject*)ptr)->Position,
            new()
            {
                TextGetter = _ => new()
                {
                    Text      = LuminaWrapper.GetBNPCName(BALL_THRID_NAME_ID),
                    TextScale = 1.4f,
                    TextColor = KnownColor.MediumPurple.ToVector4()
                }
            }
        );

        physicalHandle = ZoneIndicatorRenderer.Instance().RegPermanent
        (
            ZONE_ID,
            () => physicalBalls,
            ptr => ((GameObject*)ptr)->Position,
            new()
            {
                TextGetter = _ => new()
                {
                    Text      = LuminaWrapper.GetBNPCName(BALL_FIRST_NAME_ID),
                    TextScale = 1.4f,
                    TextColor = KnownColor.DeepSkyBlue.ToVector4()
                }
            }
        );

        normalHandle = ZoneIndicatorRenderer.Instance().RegPermanent
        (
            ZONE_ID,
            () => normalBalls,
            ptr => ((GameObject*)ptr)->Position,
            new()
            {
                TextGetter = _ => new()
                {
                    Text      = LuminaWrapper.GetBNPCName(BALL_SECOND_NAME_ID),
                    TextScale = 1.4f,
                    TextColor = KnownColor.GreenYellow.ToVector4()
                }
            }
        );

        IClientState.Instance().TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
    }

    protected override void Uninit()
    {
        IClientState.Instance().TerritoryChanged -= OnZoneChanged;
        FrameworkManager.Instance().Unreg(OnUpdate);
        CharacterStatusManager.Instance().Unreg(OnGainStatus);

        magicHandle?.Unreg();
        magicHandle = null;

        physicalHandle?.Unreg();
        physicalHandle = null;

        normalHandle?.Unreg();
        normalHandle = null;
    }

    private void OnZoneChanged
    (
        uint zone
    )
    {
        FrameworkManager.Instance().Unreg(OnUpdate);
        CharacterStatusManager.Instance().Unreg(OnGainStatus);

        if (GameState.TerritoryType != ZONE_ID) return;

        FrameworkManager.Instance().Reg(OnUpdate, 100);
        CharacterStatusManager.Instance().RegGain(OnGainStatus);
    }

    private void OnUpdate
    (
        IFramework framework
    )
    {
        if (LocalPlayerState.HasStatus(PHYSICAL_DAMAGE_UP_STATUS_ID, out _))
        {
            physicalBalls.Clear();
            magicBalls  = CharacterManager.Instance()->FindAll(&TryGetMagicBall);
            normalBalls = CharacterManager.Instance()->FindAll(&TryGetNormalBall);
        }
        else if (LocalPlayerState.HasStatus(MAGICAL_DAMAGE_DOWN_STATUS_ID, out _))
        {
            magicBalls.Clear();
            physicalBalls = CharacterManager.Instance()->FindAll(&TryGetPhysicalBall);
            normalBalls   = CharacterManager.Instance()->FindAll(&TryGetNormalBall);
        }
        else
        {
            magicBalls.Clear();
            physicalBalls.Clear();
        }
    }

    private void OnGainStatus
    (
        IBattleChara player,
        ushort       id,
        ushort       param,
        ushort       stackCount,
        TimeSpan     remainingTime,
        ulong        sourceID
    )
    {
        if (player.ContentID != LocalPlayerState.ContentID) return;
        if ((uint)id is not MAGICAL_DAMAGE_DOWN_STATUS_ID and PHYSICAL_DAMAGE_UP_STATUS_ID) return;

        OnUpdate(null);
    }

    private static bool TryGetMagicBall
    (
        BattleChara* chara
    ) =>
        chara         != null               &&
        chara->NameId == BALL_THRID_NAME_ID &&
        chara->Health > 0;

    private static bool TryGetPhysicalBall
    (
        BattleChara* chara
    ) =>
        chara         != null               &&
        chara->NameId == BALL_FIRST_NAME_ID &&
        chara->Health > 0;

    private static bool TryGetNormalBall
    (
        BattleChara* chara
    ) =>
        chara         != null                &&
        chara->NameId == BALL_SECOND_NAME_ID &&
        chara->Health > 0;

    #region 常量

    private const uint ZONE_ID = 426;

    // 暗以太·壹 → 物理受伤加重（去找暗以太·叁）
    private const uint BALL_FIRST_NAME_ID = 3289;

    // 暗以太·贰 → 真实伤害
    private const uint BALL_SECOND_NAME_ID = 3290;

    // 暗以太·叁 → 魔法受伤加重（去找暗以太·壹）
    private const uint BALL_THRID_NAME_ID = 3291;

    // 物理受伤加重
    private const uint PHYSICAL_DAMAGE_UP_STATUS_ID = 657;

    // 魔法受伤加重
    private const uint MAGICAL_DAMAGE_DOWN_STATUS_ID = 658;

    #endregion
}
