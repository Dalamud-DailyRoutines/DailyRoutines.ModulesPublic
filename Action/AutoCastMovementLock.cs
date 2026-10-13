using System.Numerics;
using System.Runtime.InteropServices;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Hooking;
using Dalamud.IoC;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.Packets.Upstream;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;
using LuminaAction = Lumina.Excel.Sheets.Action;
using PlayerController = FFXIVClientStructs.Expansion.FFXIV.Client.Game.Control.PlayerController;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoCastMovementLock : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = Lang.Get("AutoCastMovementLockTitle"),
        Description = Lang.Get("AutoCastMovementLockDescription"),
        Category = ModuleCategory.Action,
        Author = ["逆光"]
    };

    public override ModulePermission Permission { get; } = new();

    private static readonly CompSig ReadWalkInputSig = new("E8 ?? ?? ?? ?? 80 7B 3E 00 48 8D 3D");
    private delegate void ReadWalkInputDelegate(nint self, float* left, float* forward, float* turn, byte* backwardOrStrafe, byte* a6, byte additive);
    private Hook<ReadWalkInputDelegate> ReadWalkInputHook;

    private static readonly CompSig ReadFlyInputSig = new("E8 ?? ?? ?? ?? 0F B6 0D ?? ?? ?? ?? B8");
    private delegate void ReadFlyInputDelegate(nint self, FlyInput* input);
    private Hook<ReadFlyInputDelegate> ReadFlyInputHook;

    private static readonly CompSig MouseMovementSig = new("48 8B C4 48 89 70 18 48 89 78 20 55 41 56");
    private delegate void MouseMovementDelegate(nint self, float horizontal, float vertical, char a4, byte alignWithCamera, Vector3* direction);
    private Hook<MouseMovementDelegate> MouseMovementHook;

    private Config config = null!;
    private GamepadServices gamepadServices = null!;
    private PendingAction pendingAction;
    private long blockMovementUntil;
    private bool releaseWasHeld;
    private bool replayingAction;
    private readonly CastSequenceState castSequence = new();
    private long lastInputFrame = -1;
    private bool released;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();
        gamepadServices = new();
        IDalamudPluginInterface.Instance().Inject(gamepadServices);
        ReadWalkInputHook ??= ReadWalkInputSig.GetHook<ReadWalkInputDelegate>(ReadWalkInputDetour);
        ReadFlyInputHook ??= ReadFlyInputSig.GetHook<ReadFlyInputDelegate>(ReadFlyInputDetour);
        MouseMovementHook ??= MouseMovementSig.GetHook<MouseMovementDelegate>(MouseMovementDetour);

        UseActionManager.Instance().RegPreUseAction(OnPreUseAction);
        UseActionManager.Instance().RegPostUseAction(OnPostUseAction);
        UseActionManager.Instance().RegPreCharacterStartCast(OnPreCharacterStartCast);
        GamePacketManager.Instance().RegPostSendPacket(OnPostSendPacket);
        LocalPlayerState.Instance().PlayerMoveStateChanged += OnPlayerMoveStateChanged;
        IClientState.Instance().TerritoryChanged += OnTerritoryChanged;
        IClientState.Instance().Logout += OnLogout;

        ReadWalkInputHook.Enable();
        ReadFlyInputHook.Enable();
        MouseMovementHook.Enable();
    }

    protected override void Uninit()
    {
        UseActionManager.Instance().Unreg(OnPreUseAction);
        UseActionManager.Instance().Unreg(OnPostUseAction);
        UseActionManager.Instance().Unreg(OnPreCharacterStartCast);
        GamePacketManager.Instance().Unreg(OnPostSendPacket);
        LocalPlayerState.Instance().PlayerMoveStateChanged -= OnPlayerMoveStateChanged;
        IClientState.Instance().TerritoryChanged -= OnTerritoryChanged;
        IClientState.Instance().Logout -= OnLogout;
        ResetRuntimeState();
    }

    private void ReadWalkInputDetour(nint self, float* left, float* forward, float* turn, byte* backwardOrStrafe, byte* a6, byte additive)
    {
        ReadWalkInputHook.Original(self, left, forward, turn, backwardOrStrafe, a6, additive);
        if (additive != 0 || !ShouldBlockMovement()) return;
        *left = 0;
        *forward = 0;
    }

    private void ReadFlyInputDetour(nint self, FlyInput* input)
    {
        ReadFlyInputHook.Original(self, input);
        if (!ShouldBlockMovement()) return;
        input->Forward = 0;
        input->Left = 0;
        input->Up = 0;
    }

    private void MouseMovementDetour(nint self, float horizontal, float vertical, char a4, byte alignWithCamera, Vector3* direction)
    {
        if (*(byte*)(self + MOUSE_MOVEMENT_FLAG_OFFSET) != 0 && ShouldBlockMovement())
        {
            return;
        }
        MouseMovementHook.Original(self, horizontal, vertical, a4, alignWithCamera, direction);
    }

    private bool CanOperate()
    {
        var player = Control.GetLocalPlayer();
        return player != null && player->Health > 0 &&
               !ICondition.Instance().IsBetweenAreas &&
               (!GameState.IsInPVPArea || config.PVP) &&
               config.EnabledJobs.Contains(player->ClassJob);
    }

    private bool IsReleased()
    {
        var frame = (long)Framework.Instance()->FrameCounter;
        if (lastInputFrame != frame)
        {
            lastInputFrame = frame;
            var held = IsBindingHeld(config.ReleaseKey, config.ReleaseButton);
            if (!config.IsHoldToRelease && held && !releaseWasHeld)
                config.UnlockPermanently = !config.UnlockPermanently;
            releaseWasHeld = held;
            released = config.UnlockPermanently || config.IsHoldToRelease && held ||
                       GameState.IsForeground && !config.ControllerMode && config.MouseButtonRelease &&
                       IsKeyHeld(VirtualKey.LBUTTON) && IsKeyHeld(VirtualKey.RBUTTON);
        }
        return released;
    }

    private bool IsBindingHeld(VirtualKey key, GamepadButtons button)
    {
        if (!GameState.IsForeground) return false;
        return config.ControllerMode
                   ? button != GamepadButtons.None && gamepadServices.State.Raw(button) != 0
                   : key != VirtualKey.NO_KEY && IsKeyHeld(key);
    }

    private bool IsKeyHeld(VirtualKey key) => (GetAsyncKeyState((int)key) & KEY_HELD_MASK) != 0;

    private bool ShouldBlockMovement()
    {
        if (!CanOperate() || IsReleased())
        {
            ClearPendingAction();
            return false;
        }

        var player = IObjectTable.Instance().LocalPlayer;
        var manager = ActionManager.Instance();
        if (player == null || manager == null) return false;

        var combatLockActive = config.ForceStopMoveCombat && ICondition.Instance()[ConditionFlag.InCombat];

        if (pendingAction != null || Environment.TickCount64 < blockMovementUntil || HasProtectedStatus() ||
            config.BlockAllMovement && IsBindingHeld(config.BlockKey, config.BlockButton))
            return true;

        if (!combatLockActive && !config.Buffer && castSequence.StartedWhileMoving &&
            (player.IsCasting || ICondition.Instance().IsCasting))
            return false;

        if (IsProtectedCast(player)) return true;

        var castRemaining = player.IsCasting ? Math.Max(0, player.TotalCastTime - player.CurrentCastTime) : 0;
        if (Math.Max(castRemaining, GetGCD()) >= GCD_CUTOFF) return false;

        var queued = manager->ActionQueued && manager->QueuedActionId != 0;
        var castable = queued && manager->QueuedActionType == ActionType.Action && IsCastableAction(manager->QueuedActionId);
        var canStopForQueuedCast = castable && (combatLockActive || config.Buffer ||
                                               castSequence.IsProtectedQueue(manager->GetAdjustedActionId(manager->QueuedActionId)) ||
                                               !IsMovementRequested());
        return canStopForQueuedCast || combatLockActive && (!queued || castable);
    }

    private void OnPreCharacterStartCast(ref bool isPrevented, ref IBattleChara player, ref ActionType type,
                                         ref uint actionID, ref nint a4, ref float rotation, ref float a6)
    {
        if (isPrevented || player.ToStruct() != Control.GetLocalPlayer()) return;
        castSequence.StartCast(IsMovementRequested());
    }

    private bool IsMovementRequested()
    {
        if (AgentMap.Instance()->IsPlayerMoving) return true;

        var controller = PlayerController.Instance();
        if (controller != null && controller->IsAutoMove != 0) return true;
        if (!GameState.IsForeground) return false;

        if (gamepadServices.State.LeftStick.LengthSquared() > GAMEPAD_MOVEMENT_DEADZONE_SQUARED ||
            IsKeyHeld(VirtualKey.LBUTTON) && IsKeyHeld(VirtualKey.RBUTTON))
            return true;

        return IsMovementInputHeld(InputId.MOVE_FORE) || IsMovementInputHeld(InputId.MOVE_BACK) ||
               IsMovementInputHeld(InputId.MOVE_LEFT) || IsMovementInputHeld(InputId.MOVE_RIGHT) ||
               IsMovementInputHeld(InputId.MOVE_STRIFE_L) || IsMovementInputHeld(InputId.MOVE_STRIFE_R) ||
               IsMovementInputHeld(InputId.MOVE_AND_STEER);
    }

    private bool IsMovementInputHeld(InputId id) =>
        InputIDManager.Instance().IsInputIDDown(id) || InputIDManager.Instance().IsInputIDHeld(id);

    private bool IsProtectedCast(IPlayerCharacter player)
    {
        if (player.IsCasting && IsExcludedCast(player.CastActionType, player.CastActionID)) return false;
        return config.IsSlideAuto
                   ? ICondition.Instance().IsCasting
                   : player.IsCasting && player.TotalCastTime - player.CurrentCastTime > config.Threshold;
    }

    private bool IsExcludedCast(ActionType type, uint actionID) =>
        type == ActionType.Mount || type == ActionType.Action &&
        (actionID is WALKABLE_ACTION_ONE or WALKABLE_ACTION_TWO ||
         actionID == TELEPORT_ACTION && !config.BlockTP || actionID == RETURN_ACTION && !config.BlockReturn);

    private bool IsCastableAction(uint actionID)
    {
        var manager = ActionManager.Instance();
        if (manager == null) return false;
        actionID = manager->GetAdjustedActionId(actionID);
        if (IsExcludedCast(ActionType.Action, actionID)) return false;
        if (IsProtectedAction(actionID)) return true;
        return LuminaGetter.TryGetRow(actionID, out LuminaAction row) && row.Cast100ms > 0 &&
               ActionManager.GetAdjustedCastTime(ActionType.Action, actionID) > 0;
    }

    private bool IsProtectedAction(uint actionID) => actionID switch
    {
        PASSAGE_ACTION => config.PreventPassage,
        FLAMETHROWER_ACTION => config.PreventFlame,
        MEDITATE_ACTION => config.PreventMeditate,
        IMPROVISATION_ACTION => config.PreventImprov,
        PHANTOM_FLURRY_ACTION => config.PreventPhantom,
        _ => false
    };

    private bool HasProtectedStatus()
    {
        var player = Control.GetLocalPlayer();
        if (player == null) return false;
        return config.PreventPassage && player->StatusManager.HasStatus(PASSAGE_STATUS) ||
               config.PreventFlame && player->StatusManager.HasStatus(FLAMETHROWER_STATUS) ||
               config.PreventMeditate && player->StatusManager.HasStatus(MEDITATE_STATUS) ||
               config.PreventImprov && player->StatusManager.HasStatus(IMPROVISATION_STATUS) ||
               config.PreventPhantom && player->StatusManager.HasStatus(PHANTOM_FLURRY_STATUS);
    }

    private float GetGCD()
    {
        var manager = ActionManager.Instance();
        if (manager == null) return 0;
        var recast = manager->GetRecastGroupDetail(GCD_RECAST_GROUP);
        return recast == null || !recast->IsActive ? 0 : Math.Max(0, recast->Total - recast->Elapsed);
    }

    private void OnPreUseAction(ref bool isPrevented, ref ActionType actionType, ref uint actionID,
                                ref ulong targetID, ref uint extraParam,
                                ref ActionManager.UseActionMode queueState, ref uint comboRouteID)
    {
        if (isPrevented || replayingAction || actionType != ActionType.Action || !CanOperate() || IsReleased()) return;
        if (IsExcludedCast(actionType, actionID)) return;

        var manager = ActionManager.Instance();
        if (manager == null) return;

        var adjustedID = manager->GetAdjustedActionId(actionID);
        castSequence.PrepareAction(adjustedID, queueState == ActionManager.UseActionMode.Queue);
        if (LuminaGetter.TryGetRow(adjustedID, out LuminaAction row) && row.TargetArea && row.Cast100ms > 0)
        {
            blockMovementUntil = Environment.TickCount64 + (long)(config.GroundedHold * 1000);
            return;
        }

        if (!config.Buffer || pendingAction != null || !AgentMap.Instance()->IsPlayerMoving ||
            manager->ActionQueued || GetGCD() > 0 || !IsCastableAction(actionID)) return;

        pendingAction = new(actionID, targetID, extraParam, queueState, comboRouteID,
                            LocalPlayerState.ClassJob, Environment.TickCount64);
        isPrevented = true;
        var action = pendingAction;
        _ = IFramework.Instance().RunOnTick
        (
            () =>
            {
                if (ReferenceEquals(pendingAction, action)) ClearPendingAction();
            },
            delay: TimeSpan.FromMilliseconds(BUFFER_TIMEOUT_MS)
        );
    }

    private void OnPostUseAction(bool result, ActionType actionType, uint actionID, ulong targetID,
                                 uint extraParam, ActionManager.UseActionMode queueState, uint comboRouteID)
    {
        castSequence.FinishAction();
        var manager = ActionManager.Instance();
        var player = IObjectTable.Instance().LocalPlayer;
        if (manager == null || player == null) return;

        var protectedCast = CanOperate() && !IsReleased() && player.IsCasting &&
                            player.CurrentCastTime < player.TotalCastTime && !castSequence.StartedWhileMoving &&
                            !IsExcludedCast(player.CastActionType, player.CastActionID);
        var queuedActionID = manager->ActionQueued && manager->QueuedActionType == ActionType.Action
                                 ? manager->GetAdjustedActionId(manager->QueuedActionId)
                                 : 0;
        castSequence.RecordQueue(queuedActionID, protectedCast);
    }

    private void OnPlayerMoveStateChanged(bool isMoving)
    {
        if (isMoving || pendingAction is not { } action) return;

        if (!CanOperate() || IsReleased() || !config.Buffer || LocalPlayerState.ClassJob != action.Job ||
            Environment.TickCount64 - action.StartedAt >= BUFFER_TIMEOUT_MS)
        {
            ClearPendingAction();
            return;
        }

        if (AgentMap.Instance()->IsPlayerMoving) return;
        ClearPendingAction();
        var manager = ActionManager.Instance();
        if (manager == null) return;

        replayingAction = true;
        try
        {
            manager->UseAction(ActionType.Action, action.ActionID, action.TargetID,
                               action.ExtraParam, action.QueueState, action.ComboRouteID);
        }
        finally
        {
            replayingAction = false;
        }
    }

    private void OnPostSendPacket(int opcode, nint packet, bool isPrioritize)
    {
        if (opcode != UpstreamOpcode.UseActionOpcode || !CanOperate() || IsReleased()) return;
        var action = (UseActionPacket*)packet;
        if (action->ActionType == (byte)ActionType.Action && IsProtectedAction(action->ActionID))
            blockMovementUntil = Environment.TickCount64 + CHANNEL_START_HOLD_MS;
    }

    private void OnTerritoryChanged(uint territory) => ResetRuntimeState();

    private void OnLogout(int type, int code) => ResetRuntimeState();

    private void ClearPendingAction() => pendingAction = null;

    private void ResetRuntimeState()
    {
        ClearPendingAction();
        blockMovementUntil = 0;
        castSequence.Reset();
        releaseWasHeld = false;
        lastInputFrame = -1;
        released = false;
    }

    protected override void ConfigUI()
    {
        var changed = ImGui.Checkbox(Lang.Get("AutoCastMovementLock-ControllerMode"), ref config.ControllerMode);
        changed |= ImGui.Checkbox(Lang.Get("AutoCastMovementLock-AutoSlidecast"), ref config.IsSlideAuto);
        if (!config.IsSlideAuto)
            changed |= ImGui.SliderFloat(Lang.Get("AutoCastMovementLock-CastRemaining"), ref config.Threshold, 0.1f, 1f);
        changed |= ImGui.Checkbox(Lang.Get("AutoCastMovementLock-CombatLock"), ref config.ForceStopMoveCombat);
        ImGui.TextDisabled(Lang.Get("AutoCastMovementLock-CombatLockHelp"));
        changed |= ImGui.Checkbox(Lang.Get("AutoCastMovementLock-Buffer"), ref config.Buffer);
        ImGui.TextDisabled(Lang.Get("AutoCastMovementLock-BufferHelp"));
        changed |= ImGui.Checkbox(Lang.Get("AutoCastMovementLock-BlockAllMovement"), ref config.BlockAllMovement);
        changed |= ImGui.Checkbox(Lang.Get("AutoCastMovementLock-HoldToRelease"), ref config.IsHoldToRelease);
        changed |= ImGui.Checkbox(Lang.Get("AutoCastMovementLock-UnlockPermanently"), ref config.UnlockPermanently);
        if (config.ControllerMode)
        {
            changed |= DrawGamepadBinding(Lang.Get("AutoCastMovementLock-ReleaseButton"), ref config.ReleaseButton);
            if (config.BlockAllMovement) changed |= DrawGamepadBinding(Lang.Get("AutoCastMovementLock-BlockButton"), ref config.BlockButton);
        }
        else
        {
            changed |= DrawKeyBinding(Lang.Get("AutoCastMovementLock-ReleaseKey"), ref config.ReleaseKey);
            if (config.BlockAllMovement) changed |= DrawKeyBinding(Lang.Get("AutoCastMovementLock-BlockKey"), ref config.BlockKey);
            changed |= ImGui.Checkbox(Lang.Get("AutoCastMovementLock-MouseButtonRelease"), ref config.MouseButtonRelease);
        }
        changed |= ImGui.SliderFloat(Lang.Get("AutoCastMovementLock-GroundTargetHold"), ref config.GroundedHold, 0f, 3f);

        ImGui.Separator();
        ImGui.TextUnformatted(Lang.Get("AutoCastMovementLock-ChannelProtection"));
        changed |= ImGui.Checkbox(Lang.Get("AutoCastMovementLock-PreventPassage"), ref config.PreventPassage);
        changed |= ImGui.Checkbox(Lang.Get("AutoCastMovementLock-PreventFlamethrower"), ref config.PreventFlame);
        changed |= ImGui.Checkbox(Lang.Get("AutoCastMovementLock-PreventMeditate"), ref config.PreventMeditate);
        changed |= ImGui.Checkbox(Lang.Get("AutoCastMovementLock-PreventImprovisation"), ref config.PreventImprov);
        changed |= ImGui.Checkbox(Lang.Get("AutoCastMovementLock-PreventPhantomFlurry"), ref config.PreventPhantom);
        changed |= ImGui.Checkbox(Lang.Get("AutoCastMovementLock-ProtectTeleport"), ref config.BlockTP);
        changed |= ImGui.Checkbox(Lang.Get("AutoCastMovementLock-ProtectReturn"), ref config.BlockReturn);
        changed |= ImGui.Checkbox(Lang.Get("AutoCastMovementLock-EnablePVP"), ref config.PVP);

        ImGui.Separator();
        ImGui.TextUnformatted(Lang.Get("AutoCastMovementLock-EnabledJobs"));
        if (ImGui.Button(Lang.Get("All"))) { SetJobs(0); changed = true; }
        ImGui.SameLine();
        if (ImGui.Button(Lang.Get("AutoCastMovementLock-CastingJobs"))) { SetJobs(1); changed = true; }
        ImGui.SameLine();
        if (ImGui.Button(Lang.Get("AutoCastMovementLock-CraftingJobs"))) { SetJobs(2); changed = true; }
        ImGui.SameLine();
        if (ImGui.Button(Lang.Get("AutoCastMovementLock-GatheringJobs"))) { SetJobs(3); changed = true; }
        if (ImGui.BeginTable("###AutoCastMovementLockJobs", 4))
        {
            foreach (var job in LuminaGetter.Get<ClassJob>().Where(x => x.RowId != 0 && x.ClassJobCategory.RowId != 0))
            {
                ImGui.TableNextColumn();
                var enabled = config.EnabledJobs.Contains(job.RowId);
                if (ImGui.Checkbox($"{job.Name}##Job{job.RowId}", ref enabled))
                {
                    if (enabled) config.EnabledJobs.Add(job.RowId);
                    else config.EnabledJobs.Remove(job.RowId);
                    changed = true;
                }
            }
            ImGui.EndTable();
        }

        if (changed) SaveSettings();
    }

    private bool DrawKeyBinding(string label, ref VirtualKey value)
    {
        using var combo = ImRaii.Combo(label, value.ToString());
        if (!combo) return false;
        foreach (var key in Enum.GetValues<VirtualKey>().Distinct())
        {
            if (!ImGui.Selectable(key.ToString(), key == value)) continue;
            value = key;
            return true;
        }
        return false;
    }

    private bool DrawGamepadBinding(string label, ref GamepadButtons value)
    {
        using var combo = ImRaii.Combo(label, value.ToString());
        if (!combo) return false;
        foreach (var button in Enum.GetValues<GamepadButtons>())
        {
            if (!ImGui.Selectable(button.ToString(), button == value)) continue;
            value = button;
            return true;
        }
        return false;
    }

    private void SetJobs(int group)
    {
        var jobs = LuminaGetter.Get<ClassJob>().Where(x => x.RowId != 0 && x.ClassJobCategory.RowId != 0 && (group switch
        {
            1 => CASTING_JOBS.Contains(x.RowId),
            2 => x.RowId is >= 8 and <= 15,
            3 => x.RowId is >= 16 and <= 18,
            _ => true
        })).Select(x => x.RowId).ToArray();
        if (jobs.All(config.EnabledJobs.Contains)) config.EnabledJobs.ExceptWith(jobs);
        else config.EnabledJobs.UnionWith(jobs);
    }

    private void SaveSettings()
    {
        var startedWhileMoving = castSequence.StartedWhileMoving;
        ResetRuntimeState();
        castSequence.StartedWhileMoving = startedWhileMoving;
        releaseWasHeld = IsBindingHeld(config.ReleaseKey, config.ReleaseButton);
        config.Save(this);
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    private sealed class CastSequenceState
    {
        public bool StartedWhileMoving;
        private uint protectedQueuedActionID;
        private bool startingProtectedQueue;

        public bool IsProtectedQueue(uint actionID) => actionID != 0 && actionID == protectedQueuedActionID;

        public void PrepareAction(uint actionID, bool fromQueue)
        {
            startingProtectedQueue = fromQueue && IsProtectedQueue(actionID);
            if (!fromQueue) protectedQueuedActionID = 0;
        }

        public void StartCast(bool movementRequested)
        {
            StartedWhileMoving = movementRequested && !startingProtectedQueue;
            protectedQueuedActionID = 0;
            startingProtectedQueue = false;
        }

        public void FinishAction() => startingProtectedQueue = false;

        public void RecordQueue(uint actionID, bool protectedCast) =>
            protectedQueuedActionID = protectedCast ? actionID : 0;

        public void Reset()
        {
            StartedWhileMoving = false;
            protectedQueuedActionID = 0;
            startingProtectedQueue = false;
        }
    }

    private sealed class GamepadServices
    {
        [PluginService] public IGamepadState State { get; private set; } = null!;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct FlyInput
    {
        [FieldOffset(0)] public float Forward;
        [FieldOffset(4)] public float Left;
        [FieldOffset(8)] public float Up;
    }

    private sealed record PendingAction(uint ActionID, ulong TargetID, uint ExtraParam,
                                        ActionManager.UseActionMode QueueState, uint ComboRouteID,
                                        uint Job, long StartedAt);

    private sealed class Config : ModuleConfig
    {
        public bool ControllerMode;
        public bool ForceStopMoveCombat;
        public bool IsSlideAuto = true;
        public bool Buffer;
        public bool BlockAllMovement;
        public bool IsHoldToRelease = true;
        public bool UnlockPermanently;
        public bool MouseButtonRelease;
        public bool PreventPassage;
        public bool PreventFlame;
        public bool PreventMeditate;
        public bool PreventImprov;
        public bool PreventPhantom;
        public bool BlockTP;
        public bool BlockReturn;
        public bool PVP;
        public float Threshold = 0.5f;
        public float GroundedHold = 1f;
        public VirtualKey ReleaseKey = VirtualKey.LCONTROL;
        public VirtualKey BlockKey = VirtualKey.XBUTTON1;
        public GamepadButtons ReleaseButton = GamepadButtons.L1;
        public GamepadButtons BlockButton = GamepadButtons.L2;
        public HashSet<uint> EnabledJobs = [.. CASTING_JOBS];
    }

    #region 常量

    private const float GCD_CUTOFF = 0.1f;
    private const float GAMEPAD_MOVEMENT_DEADZONE_SQUARED = 0.01f;
    private const int GCD_RECAST_GROUP = 57;
    private const int BUFFER_TIMEOUT_MS = 2_000;
    private const int CHANNEL_START_HOLD_MS = 1_000;
    private const int MOUSE_MOVEMENT_FLAG_OFFSET = 63;
    private const int KEY_HELD_MASK = 32768;
    private const uint TELEPORT_ACTION = 5;
    private const uint RETURN_ACTION = 6;
    private const uint WALKABLE_ACTION_ONE = 29391;
    private const uint WALKABLE_ACTION_TWO = 29402;
    private const uint PASSAGE_ACTION = 7385;
    private const uint FLAMETHROWER_ACTION = 7418;
    private const uint MEDITATE_ACTION = 7497;
    private const uint IMPROVISATION_ACTION = 16014;
    private const uint PHANTOM_FLURRY_ACTION = 23288;
    private const ushort PASSAGE_STATUS = 1175;
    private const ushort FLAMETHROWER_STATUS = 1205;
    private const ushort MEDITATE_STATUS = 1231;
    private const ushort IMPROVISATION_STATUS = 1827;
    private const ushort PHANTOM_FLURRY_STATUS = 2502;
    private static readonly uint[] CASTING_JOBS = [6, 7, 20, 24, 26, 27, 28, 33, 34, 35, 36, 39, 40, 42, 44];

    #endregion
}
