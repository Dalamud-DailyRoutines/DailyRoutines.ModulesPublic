using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.ClientState.Keys;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.OmenService;
using PlayerController = FFXIVClientStructs.Expansion.FFXIV.Client.Game.Control.PlayerController;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class AutoCastMovementLock : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoCastMovementLockTitle"),
        Description = Lang.Get("AutoCastMovementLockDescription"),
        Category    = ModuleCategory.Action,
        Author      = ["逆光"]
    };

    private Config config = null!;

    private JobSelectCombo jobSelectCombo = null!;

    private CastFlow         castFlow         = null!;
    private InputInterceptor inputInterceptor = null!;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        jobSelectCombo = new("职业选择")
        {
            SelectedIDs = config.EnabledJobs
        };

        castFlow         = new();
        inputInterceptor = new(config, castFlow);

        ReadWalkInputHook ??= ReadWalkInputSig.GetHook<ReadWalkInputDelegate>(ReadWalkInputDetour);
        ReadFlyInputHook  ??= ReadFlyInputSig.GetHook<ReadFlyInputDelegate>(ReadFlyInputDetour);
        MouseMovementHook ??= MouseMovementSig.GetHook<MouseMovementDelegate>(MouseMovementDetour);

        UseActionManager.Instance().RegPreUseAction(OnPreUseAction);
        UseActionManager.Instance().RegPostUseAction(OnPostUseAction);
        UseActionManager.Instance().RegPreCharacterStartCast(OnPreCharacterStartCast);

        IClientState.Instance().TerritoryChanged += OnZoneChanged;
        GameState.Instance().Logout              += OnLogout;

        ReadWalkInputHook.Enable();
        ReadFlyInputHook.Enable();
        MouseMovementHook.Enable();
    }

    protected override void Uninit()
    {
        UseActionManager.Instance().Unreg(OnPreUseAction);
        UseActionManager.Instance().Unreg(OnPostUseAction);
        UseActionManager.Instance().Unreg(OnPreCharacterStartCast);

        IClientState.Instance().TerritoryChanged -= OnZoneChanged;
        GameState.Instance().Logout              -= OnLogout;

        castFlow.Reset();
    }

    private static bool IsMoveInputRequested()
    {
        if (LocalPlayerState.Instance().IsMoving)
            return true;
        if (PlayerController.Instance()->IsAutoMove != 0)
            return true;
        if (!GameState.IsForeground)
            return false;
        if (IGamepadState.Instance().LeftStick.LengthSquared() > GAMEPAD_MOVEMENT_DEAD_ZONE_SQUARED)
            return true;
        if (InputQuery.IsKeyPressed(VirtualKey.LBUTTON) && InputQuery.IsKeyPressed(VirtualKey.RBUTTON))
            return true;

        return MOVE_INPUT_IDS.Any(IsMoveInputHeld);

        static bool IsMoveInputHeld
        (
            InputId id
        ) =>
            InputIDManager.Instance().IsInputIDDown(id) || InputIDManager.Instance().IsInputIDHeld(id);
    }

    private sealed class Config : ModuleConfig
    {
        public bool InCombatOnlyMoveWhenSlideCast;

        public bool MouseButtonsToTempUnlock;

        public VirtualKey KeyToTempUnlock = VirtualKey.NO_KEY;
        public VirtualKey KeyToTempLock   = VirtualKey.NO_KEY;

        public GamepadButtons ButtonToTempUnlock = GamepadButtons.None;
        public GamepadButtons ButtonToTempLock   = GamepadButtons.None;

        public bool IsEnabledPVP;

        public bool LockTeleportCast;
        public bool LockReturnCast;

        public HashSet<uint> EnabledJobs = [.. CASTING_JOBS];
    }

    #region 常量

    private const float GCD_CUTOFF                         = 0f;
    private const float GAMEPAD_MOVEMENT_DEAD_ZONE_SQUARED = 0.01f;
    private const int   GCD_RECAST_GROUP                   = 57;

    private const uint TELEPORT_ACTION     = 5;
    private const uint RETURN_ACTION       = 6;
    private const uint WALKABLE_ACTION_ONE = 29391;
    private const uint WALKABLE_ACTION_TWO = 29402;

    private static readonly uint[] CASTING_JOBS =
    [
        6,
        7,
        20,
        24,
        26,
        27,
        28,
        33,
        34,
        35,
        36,
        39,
        40,
        42,
        44
    ];

    private static readonly InputId[] MOVE_INPUT_IDS =
    [
        InputId.MOVE_FORE,
        InputId.MOVE_BACK,
        InputId.MOVE_LEFT,
        InputId.MOVE_RIGHT,
        InputId.MOVE_STRIFE_L,
        InputId.MOVE_STRIFE_R,
        InputId.MOVE_AND_STEER
    ];

    #endregion
}
