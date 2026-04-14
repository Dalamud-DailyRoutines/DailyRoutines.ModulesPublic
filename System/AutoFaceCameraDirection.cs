using System.Collections.Frozen;
using System.Numerics;
using System.Runtime.CompilerServices;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Internal;
using DailyRoutines.Manager;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Interface.Components;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using OmenTools.Dalamud.Attributes;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Models;
using OmenTools.Interop.Game.Models.Packets.Upstream;
using OmenTools.OmenService;
using OmenTools.Threading;
using Camera = FFXIVClientStructs.FFXIV.Client.Game.Camera;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoFaceCameraDirection : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title            = Lang.Get("AutoFaceCameraDirectionTitle"),
        Description      = Lang.Get("AutoFaceCameraDirectionDescription"),
        Category         = ModuleCategory.System,
        ModulesRecommend = ["DisableGroundActionAutoFace", "IgnoreActionTargetBlocked"]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };
    
    private static readonly CompSig                            CameraUpdateRotationSig = new("40 53 48 81 EC ?? ?? ?? ?? 8B 81 ?? ?? ?? ?? 48 8B D9 44 0F 29 54 24");
    private delegate        void                               CameraUpdateRotationDelegate(Camera* camera);
    private                 Hook<CameraUpdateRotationDelegate> CameraUpdateRotationHook;

    private static readonly CompSig                      UpdateVisualRotationSig = new("40 53 48 83 EC ?? 83 B9 ?? ?? ?? ?? ?? 48 8B D9 0F 85 ?? ?? ?? ?? F6 81");
    private delegate        void*                        UpdateVisualRotationDelegate(GameObject* gameObject);
    private readonly        UpdateVisualRotationDelegate UpdateVisualRotation    = UpdateVisualRotationSig.GetDelegate<UpdateVisualRotationDelegate>();

    private static readonly CompSig                    SetRotationSig = new("40 53 48 83 EC ?? F3 0F 10 81 ?? ?? ?? ?? 48 8B D9 0F 2E C1");
    private delegate        void                       SetRotationDelegate(GameObject* gameObject, float rotation);
    private                 Hook<SetRotationDelegate>? SetRotationHook;

    private Config config = null!;

    private float localPlayerRotationInput;

    private Camera* cacheCamera;

    private bool  lockOn;
    private float lockOnRotation;

    private float cameraCharaRotation;
    private float lastSendedRotation;

    private bool isAllow;
    private long lastUpdateTick;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        CameraUpdateRotationHook ??= CameraUpdateRotationSig.GetHook<CameraUpdateRotationDelegate>(CameraUpdateRotationDetour);
        CameraUpdateRotationHook.Enable();

        SetRotationHook ??= SetRotationSig.GetHook<SetRotationDelegate>(SetRotationDetour);
        SetRotationHook.Enable();

        GamePacketManager.Instance().RegPreSendPacket(OnPreSendPacket);
        FrameworkManager.Instance().Reg(OnUpdate);

        UseActionManager.Instance().RegPostUseActionLocation(OnPostUseAction);

        CommandManager.Instance().AddCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("AutoFaceCameraDirection-CommandHelp", COMMAND) });
    }

    protected override void Uninit()
    {
        UseActionManager.Instance().Unreg(OnPostUseAction);
        FrameworkManager.Instance().Unreg(OnUpdate);
        GamePacketManager.Instance().Unreg(OnPreSendPacket);
        CommandManager.Instance().RemoveCommand(COMMAND);
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Command")}");

        using (ImRaii.PushIndent())
        {
            ImGui.TextUnformatted($"{COMMAND} → {Lang.Get("AutoFaceCameraDirection-CommandHelp", COMMAND)}");

            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"<{Lang.Get("Type")}>");

            using (ImRaii.PushIndent())
            {
                ImGui.TextUnformatted($"ground ({Lang.Get("AutoFaceCameraDirection-GroundDirection")})");

                using (ImRaii.PushIndent())
                    ImGui.TextUnformatted($"({GroundValuesString})");

                ImGui.TextUnformatted($"chara ({Lang.Get("AutoFaceCameraDirection-CharacterRotation")})");

                ImGui.TextUnformatted($"camera ({Lang.Get("AutoFaceCameraDirection-CameraRotation")})");
            }
        }

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("WorkMode")}");

        ImGui.SameLine();
        if (ImGuiComponents.ToggleButton("WorkMode", ref config.WorkMode))
            config.Save(this);

        using (ImRaii.PushIndent())
            ImGui.TextWrapped($"{Lang.Get($"AutoFaceCameraDirection-WorkMode{config.WorkMode}")}");

        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return;

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoFaceCameraDirection-GroundDirection")}");

        using (ImRaii.PushIndent())
        {
            foreach (var kvp in WorldDirectionToNormalizedDirection)
            {
                if (ImGui.Button($"{kvp.Key}##WorldDirectionToNormalizedDirection"))
                    SetLocalRotation((GameObject*)localPlayer.Address, RotationHelper.WorldDirHToChara(kvp.Value));
                ImGui.SameLine();
            }

            ImGui.NewLine();
        }

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoFaceCameraDirection-CharacterRotation")}");

        using (ImRaii.ItemWidth(200f * GlobalUIScale))
        using (ImRaii.PushIndent())
        {
            ImGui.InputFloat($"{Lang.Get("Settings")}##SetCharaRotation", ref localPlayerRotationInput, format: "%.2f");
            if (ImGui.IsItemDeactivatedAfterEdit())
                SetLocalRotation((GameObject*)localPlayer.Address, localPlayerRotationInput);

            var currentRotation = localPlayer.Rotation;
            ImGui.InputFloat($"{Lang.Get("Current")}##CurrentCharaRotation", ref currentRotation, format: "%.2f", flags: ImGuiInputTextFlags.ReadOnly);
        }

        if (cacheCamera == null) return;

        ImGui.TextColored
        (
            KnownColor.LightSkyBlue.ToVector4(),
            $"{Lang.Get("AutoFaceCameraDirection-CameraRotation")} → " +
            $"{Lang.Get("AutoFaceCameraDirection-CharacterRotation")}:"
        );

        using (ImRaii.PushIndent())
            ImGui.TextUnformatted($"{cacheCamera->DirH:F2} → {RotationHelper.CameraDirHToChara(cacheCamera->DirH):F2}");
    }

    private void OnCommand(string command, string args)
    {
        args = args.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(args))
        {
            NotifyCommandError();
            return;
        }

        var arguments = args.Split(' ');

        if (arguments.Length is not (1 or 2) || DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer)
        {
            NotifyCommandError();
            return;
        }

        var typeRaw  = arguments[0];
        var valueRaw = arguments.Length == 2 ? arguments[1] : string.Empty;

        switch (typeRaw)
        {
            case "ground" when WorldDirectionToNormalizedDirection.TryGetValue(valueRaw, out var dirGround):
                lockOnRotation = RotationHelper.WorldDirHToChara(dirGround);
                lockOn         = true;
                SetLocalRotation((GameObject*)localPlayer.Address, lockOnRotation);
                break;

            case "chara" when float.TryParse(valueRaw, out var rotation):
                lockOnRotation = rotation;
                lockOn         = true;
                break;

            case "camera" when float.TryParse(valueRaw, out var dirCamera):
                lockOnRotation = RotationHelper.CameraDirHToChara(dirCamera);
                lockOn         = true;
                SetLocalRotation((GameObject*)localPlayer.Address, lockOnRotation);
                break;

            case "off":
                lockOn         = false;
                lockOnRotation = 0;
                break;

            default:
                NotifyCommandError();
                return;
        }

        if (!lockOn) return;

        SetLocalRotation((GameObject*)localPlayer.Address, lockOnRotation);

        var moveState = MovementManager.Instance().CurrentZoneMoveState;

        if (GameState.ContentFinderCondition != 0)
        {
            var moveType = (PositionUpdateInstancePacket.MoveType)(moveState << 16);
            new PositionUpdateInstancePacket(lockOnRotation, localPlayer.Position, moveType).Send();
        }
        else
        {
            if (!Throttler.Shared.Throttle("AutoFaceCameraDirection-UpdateRotation", 20)) return;

            var moveType = (PositionUpdatePacket.MoveType)(moveState << 16);
            new PositionUpdatePacket(lockOnRotation, localPlayer.Position, moveType).Send();
        }

        return;

        void NotifyCommandError()
        {
            NotifyHelper.Instance().NotificationError(Lang.Get("Commands-InvalidArgs", command, args));
        }
    }

    private void OnPostUseAction(bool result, ActionType actionType, uint actionID, ulong targetID, Vector3 location, uint extraParam, byte a7) =>
        OnUpdate(DService.Instance().Framework);

    private void SetRotationDetour(GameObject* gameObject, float rotation)
    {
        if (gameObject == null || gameObject->EntityId != LocalPlayerState.EntityID || ShouldSkipUpdate())
        {
            SetRotationHook.Original(gameObject, rotation);
            return;
        }

        gameObject->Rotation = rotation;
        isAllow              = true;
    }

    // 主动发包
    private void OnUpdate(IFramework framework)
    {
        if (MathF.Abs(lastSendedRotation - cameraCharaRotation) < 0.001f) return;

        if (cacheCamera == null) return;

        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null || localPlayer->Health <= 0) return;

        if (ShouldSkipUpdate() || DService.Instance().Condition[ConditionFlag.Casting]) return;

        var currentTick = Environment.TickCount64;
        var isDuty      = GameState.ContentFinderCondition != 0;
        var interval    = isDuty ? 33 : 100;

        SetLocalRotation((GameObject*)localPlayer, cameraCharaRotation);

        if (currentTick - lastUpdateTick < interval) return;
        lastUpdateTick = currentTick;

        var moveState = MovementManager.Instance().CurrentZoneMoveState;

        if (isDuty)
        {
            var moveType = (PositionUpdateInstancePacket.MoveType)(moveState << 16);
            new PositionUpdateInstancePacket(cameraCharaRotation, localPlayer->Position, moveType).Send();
        }
        else
        {
            var moveType = (PositionUpdatePacket.MoveType)(moveState << 16);
            new PositionUpdatePacket(cameraCharaRotation, localPlayer->Position, moveType).Send();
        }
    }

    // 获取摄像机到人物的旋转角度
    private void CameraUpdateRotationDetour(Camera* camera)
    {
        CameraUpdateRotationHook.Original(camera);
        cacheCamera = camera;

        cameraCharaRotation = lockOn ? lockOnRotation : RotationHelper.CameraDirHToChara(camera->DirH);
    }

    private void OnPreSendPacket(ref bool isPrevented, int opcode, ref nint packet, ref bool isPrioritize)
    {
        if (cacheCamera == null || !ValidOpcodes.Contains(opcode) || ShouldSkipUpdate()) return;

        if (opcode == UpstreamOpcode.PositionUpdateOpcode)
        {
            var data = (PositionUpdatePacket*)packet;
            if (!ValidMoveTypes.Contains(data->Move)) return;

            if (!isAllow)
            {
                isPrevented = true;
                return;
            }

            isAllow = false;

            lastSendedRotation = data->Rotation;
            return;
        }

        if (opcode == UpstreamOpcode.PositionUpdateInstanceOpcode)
        {
            var data = (PositionUpdateInstancePacket*)packet;
            if (!ValidInstanceMoveTypes.Contains(data->Move)) return;

            if (!isAllow)
            {
                isPrevented = true;
                return;
            }

            isAllow = false;

            lastSendedRotation = data->RotationNew;
        }
    }

    private void SetLocalRotation(GameObject* gameObject, float value)
    {
        if (MathF.Abs(gameObject->Rotation - value) < 0.001f) return;

        gameObject->Rotation = value;
        UpdateVisualRotation(gameObject);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool ShouldSkipUpdate()
    {
        if (MovementManager.Instance().IsManagerBusy) return true;

        var isConflict = PluginConfig.Instance().ConflictKeyBinding.IsPressed();
        return config.WorkMode switch
        {
            false => isConflict, // WorkMode=false: 按下打断键时跳过 (即不工作)
            true  => !isConflict // WorkMode=true:  没按下打断键时跳过 (即不工作)
        };
    }

    #region IPC

    [IPCProvider("DailyRoutines.Modules.AutoFaceCameraDirection.SetWorkMode")]
    private void SetWorkModeIPC(bool workMode) => 
        config.WorkMode = workMode;

    [IPCProvider("DailyRoutines.Modules.AutoFaceCameraDirection.CancelLockOn")]
    private void CancelLockOnIPC()
    {
        lockOn         = false;
        lockOnRotation = 0;
    }

    [IPCProvider("DailyRoutines.Modules.AutoFaceCameraDirection.LockOnGround")]
    private bool LockOnGroundIPC(string rotation)
    {
        if (!WorldDirectionToNormalizedDirection.TryGetValue(rotation, out var dirGround))
            return false;

        lockOnRotation = RotationHelper.WorldDirHToChara(dirGround);
        lockOn         = true;

        return true;
    }

    [IPCProvider("DailyRoutines.Modules.AutoFaceCameraDirection.LockOnChara")]
    private void LockOnCharaIPC(float rotation)
    {
        lockOnRotation = rotation;
        lockOn         = true;
    }

    [IPCProvider("DailyRoutines.Modules.AutoFaceCameraDirection.LockOnCamera")]
    private void LockOnCameraIPC(float rotation)
    {
        lockOnRotation = RotationHelper.CameraDirHToChara(rotation);
        lockOn         = true;
    }

    #endregion
    

    private class Config : ModuleConfig
    {
        // true - 按下打断热键才让人物面向与摄像机一致
        // false - 按下打断热键则不保持人物面向与摄像机一致
        public bool WorkMode;
    }
    
    #region 常量

    private const string COMMAND = "/pdrface";

    private static readonly FrozenSet<int> ValidOpcodes =
    [
        UpstreamOpcode.PositionUpdateInstanceOpcode,
        UpstreamOpcode.PositionUpdateOpcode
    ];

    private static readonly FrozenSet<PositionUpdatePacket.MoveType> ValidMoveTypes =
    [
        PositionUpdatePacket.MoveType.NormalMove0,
        PositionUpdatePacket.MoveType.NormalMove1,
        PositionUpdatePacket.MoveType.NormalMove2,
        PositionUpdatePacket.MoveType.NormalMove3
    ];

    private static readonly FrozenSet<PositionUpdateInstancePacket.MoveType> ValidInstanceMoveTypes =
    [
        PositionUpdateInstancePacket.MoveType.NormalMove0,
        PositionUpdateInstancePacket.MoveType.NormalMove1,
        PositionUpdateInstancePacket.MoveType.NormalMove2,
        PositionUpdateInstancePacket.MoveType.NormalMove3
    ];
    
    private static readonly FrozenDictionary<string, Vector2> WorldDirectionToNormalizedDirection = new Dictionary<string, Vector2>
    {
        ["south"]     = new(0, 1),
        ["north"]     = new(0, -1),
        ["west"]      = new(-1, 0),
        ["east"]      = new(1, 0),
        ["northeast"] = new(0.707f, -0.707f),
        ["southeast"] = new(0.707f, 0.707f),
        ["northwest"] = new(-0.707f, -0.707f),
        ["southwest"] = new(-0.707f, 0.707f)
    }.ToFrozenDictionary();
    
    private static readonly string GroundValuesString = string.Join(" / ", WorldDirectionToNormalizedDirection.Keys);

    #endregion
}
