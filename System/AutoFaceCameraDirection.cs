using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Interface.Components;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Camera = FFXIVClientStructs.FFXIV.Client.Game.Camera;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoFaceCameraDirection : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title            = GetLoc("AutoFaceCameraDirectionTitle"),
        Description      = GetLoc("AutoFaceCameraDirectionDescription"),
        Category         = ModuleCategories.System,
        ModulesRecommend = ["DisableGroundActionAutoFace", "IgnoreActionTargetBlocked"]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

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

    private static readonly CompSig                            CameraUpdateRotationSig = new("40 53 48 81 EC ?? ?? ?? ?? 8B 81 ?? ?? ?? ?? 48 8B D9 44 0F 29 54 24");
    private delegate        void                               CameraUpdateRotationDelegate(Camera* camera);
    private static          Hook<CameraUpdateRotationDelegate> CameraUpdateRotationHook;

    private static readonly CompSig                      UpdateVisualRotationSig = new("40 53 48 83 EC ?? 83 B9 ?? ?? ?? ?? ?? 48 8B D9 0F 85 ?? ?? ?? ?? F6 81");
    private delegate        void*                        UpdateVisualRotationDelegate(GameObject* gameObject);
    private static readonly UpdateVisualRotationDelegate UpdateVisualRotation = UpdateVisualRotationSig.GetDelegate<UpdateVisualRotationDelegate>();

    private static readonly CompSig SetRotationSig = new("40 53 48 83 EC ?? F3 0F 10 81 ?? ?? ?? ?? 48 8B D9 0F 2E C1");
    private delegate void SetRotationDelegate(GameObject* gameObject, float rotation);
    private static Hook<SetRotationDelegate>? SetRotationHook;
    
    private static Config ModuleConfig = null!;

    private static float LocalPlayerRotationInput;

    private static Camera* CacheCamera;

    private static bool  LockOn;
    private static float LockOnRotation;
    
    private static float CameraCharaRotation;
    private static float LastSendedRotation;

    private static bool IsAllow;
    private static long LastUpdateTick;
    
    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        CameraUpdateRotationHook ??= CameraUpdateRotationSig.GetHook<CameraUpdateRotationDelegate>(CameraUpdateRotationDetour);
        CameraUpdateRotationHook.Enable();

        SetRotationHook ??= SetRotationSig.GetHook<SetRotationDelegate>(SetRotationDetour);
        SetRotationHook.Enable();
        
        GamePacketManager.Instance().RegPreSendPacket(OnPreSendPacket);
        FrameworkManager.Instance().Reg(OnUpdate);

        UseActionManager.Instance().RegPostUseActionLocation(OnPostUseAction);
        
        CommandManager.AddCommand(COMMAND, new(OnCommand) { HelpMessage = GetLoc("AutoFaceCameraDirection-CommandHelp", COMMAND) });
    }

    protected override void Uninit()
    {
        UseActionManager.Instance().Unreg(OnPostUseAction);
        FrameworkManager.Instance().Unreg(OnUpdate);
        GamePacketManager.Instance().Unreg(OnPreSendPacket);
        CommandManager.RemoveCommand(COMMAND);
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("Command")}");

        using (ImRaii.PushIndent())
        {
            ImGui.TextUnformatted($"{COMMAND} → {GetLoc("AutoFaceCameraDirection-CommandHelp", COMMAND)}");

            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"<{GetLoc("Type")}>");

            using (ImRaii.PushIndent())
            {
                ImGui.TextUnformatted($"ground ({GetLoc("AutoFaceCameraDirection-GroundDirection")})");

                using (ImRaii.PushIndent())
                    ImGui.TextUnformatted($"({GroundValuesString})");

                ImGui.TextUnformatted($"chara ({GetLoc("AutoFaceCameraDirection-CharacterRotation")})");

                ImGui.TextUnformatted($"camera ({GetLoc("AutoFaceCameraDirection-CameraRotation")})");
            }
        }

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("WorkMode")}");

        ImGui.SameLine();
        if (ImGuiComponents.ToggleButton("WorkMode", ref ModuleConfig.WorkMode))
            SaveConfig(ModuleConfig);

        using (ImRaii.PushIndent())
            ImGui.TextWrapped($"{GetLoc($"AutoFaceCameraDirection-WorkMode{ModuleConfig.WorkMode}")}");

        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return;

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("AutoFaceCameraDirection-GroundDirection")}");

        using (ImRaii.PushIndent())
        {
            foreach (var kvp in WorldDirectionToNormalizedDirection)
            {
                if (ImGui.Button($"{kvp.Key}##WorldDirectionToNormalizedDirection"))
                    SetLocalRotation((GameObject*)localPlayer.Address, WorldDirHToCharaRotation(kvp.Value));
                ImGui.SameLine();
            }

            ImGui.NewLine();
        }

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("AutoFaceCameraDirection-CharacterRotation")}");

        using (ImRaii.ItemWidth(200f * GlobalFontScale))
        using (ImRaii.PushIndent())
        {
            ImGui.InputFloat($"{GetLoc("Settings")}##SetCharaRotation", ref LocalPlayerRotationInput, format: "%.2f");
            if (ImGui.IsItemDeactivatedAfterEdit())
                SetLocalRotation((GameObject*)localPlayer.Address, LocalPlayerRotationInput);

            var currentRotation = localPlayer.Rotation;
            ImGui.InputFloat($"{GetLoc("Current")}##CurrentCharaRotation", ref currentRotation, format: "%.2f", flags: ImGuiInputTextFlags.ReadOnly);
        }

        if (CacheCamera == null) return;

        ImGui.TextColored
        (
            KnownColor.LightSkyBlue.ToVector4(),
            $"{GetLoc("AutoFaceCameraDirection-CameraRotation")} → " +
            $"{GetLoc("AutoFaceCameraDirection-CharacterRotation")}:"
        );

        using (ImRaii.PushIndent())
            ImGui.TextUnformatted($"{CacheCamera->DirH:F2} → {CameraDirHToCharaRotation(CacheCamera->DirH):F2}");
    }

    private static void OnCommand(string command, string args)
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
                LockOnRotation = WorldDirHToCharaRotation(dirGround);
                LockOn         = true;
                SetLocalRotation((GameObject*)localPlayer.Address, LockOnRotation);
                break;

            case "chara" when float.TryParse(valueRaw, out var rotation):
                LockOnRotation = rotation;
                LockOn         = true;
                break;

            case "camera" when float.TryParse(valueRaw, out var dirCamera):
                LockOnRotation = CameraDirHToCharaRotation(dirCamera);
                LockOn         = true;
                SetLocalRotation((GameObject*)localPlayer.Address, LockOnRotation);
                break;

            case "off":
                LockOn         = false;
                LockOnRotation = 0;
                break;

            default:
                NotifyCommandError();
                return;
        }

        if (!LockOn) return;

        SetLocalRotation((GameObject*)localPlayer.Address, LockOnRotation);

        var moveState = MovementManager.CurrentZoneMoveState;
        if (GameState.ContentFinderCondition != 0)
        {
            var moveType = (PositionUpdateInstancePacket.MoveType)(moveState << 16);
            new PositionUpdateInstancePacket(LockOnRotation, localPlayer.Position, moveType).Send();
        }
        else
        {
            if (!Throttler.Throttle("AutoFaceCameraDirection-UpdateRotation", 20)) return;

            var moveType = (PositionUpdatePacket.MoveType)(moveState << 16);
            new PositionUpdatePacket(LockOnRotation, localPlayer.Position, moveType).Send();
        }

        return;

        void NotifyCommandError() =>
            NotificationError(GetLoc("Commands-InvalidArgs", command, args));
    }
    
    private static void OnPostUseAction(bool result, ActionType actionType, uint actionID, ulong targetID, Vector3 location, uint extraParam, byte a7) =>
        OnUpdate(DService.Instance().Framework);

    private static void SetRotationDetour(GameObject* gameObject, float rotation)
    {
        if (gameObject == null || gameObject->EntityId != LocalPlayerState.EntityID || ShouldSkipUpdate())
        {
            SetRotationHook.Original(gameObject, rotation);
            return;
        }
        
        gameObject->Rotation = rotation;
        IsAllow              = true;
    }
    
    // 主动发包
    private static void OnUpdate(IFramework framework)
    {
        if (MathF.Abs(LastSendedRotation - CameraCharaRotation) < 0.001f) return;

        if (CacheCamera == null) return;
        
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null || localPlayer->Health <= 0) return;

        if (ShouldSkipUpdate() || DService.Instance().Condition[ConditionFlag.Casting]) return;

        var currentTick = Environment.TickCount64;
        var isDuty      = GameState.ContentFinderCondition != 0;
        var interval    = isDuty ? 33 : 100;

        SetLocalRotation((GameObject*)localPlayer, CameraCharaRotation);
        
        if (currentTick - LastUpdateTick < interval) return;
        LastUpdateTick = currentTick;

        var moveState = MovementManager.CurrentZoneMoveState;
        if (isDuty)
        {
            var moveType = (PositionUpdateInstancePacket.MoveType)(moveState << 16);
            new PositionUpdateInstancePacket(CameraCharaRotation, localPlayer->Position, moveType).Send();
        }
        else
        {
            var moveType = (PositionUpdatePacket.MoveType)(moveState << 16);
            new PositionUpdatePacket(CameraCharaRotation, localPlayer->Position, moveType).Send();
        }
    }
    
    // 获取摄像机到人物的旋转角度
    private static void CameraUpdateRotationDetour(Camera* camera)
    {
        CameraUpdateRotationHook.Original(camera);
        CacheCamera = camera;

        CameraCharaRotation = LockOn ? LockOnRotation : CameraDirHToCharaRotation(camera->DirH);
    }
    
    private static void OnPreSendPacket(ref bool isPrevented, int  opcode, ref nint packet, ref bool isPrioritize)
    {
        if (CacheCamera == null || !ValidOpcodes.Contains(opcode) || ShouldSkipUpdate()) return;
        
        if (opcode == UpstreamOpcode.PositionUpdateOpcode)
        {
            var data = (PositionUpdatePacket*)packet;
            if (!ValidMoveTypes.Contains(data->Move)) return;
            
            if (!IsAllow)
            {
                isPrevented = true;
                return;
            }
            
            IsAllow = false;
            
            LastSendedRotation = data->Rotation;
            return;
        }

        if (opcode == UpstreamOpcode.PositionUpdateInstanceOpcode)
        {
            var data = (PositionUpdateInstancePacket*)packet;
            if (!ValidInstanceMoveTypes.Contains(data->Move)) return;
            
            if (!IsAllow)
            {
                isPrevented = true;
                return;
            }

            IsAllow = false;
            
            LastSendedRotation = data->RotationNew;
            return;
        }
    }

    private static void SetLocalRotation(GameObject* gameObject, float value)
    {
        if (MathF.Abs(gameObject->Rotation - value) < 0.001f) return;
        
        gameObject->Rotation = value;
        UpdateVisualRotation(gameObject);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ShouldSkipUpdate()
    {
        if (MovementManager.IsManagerBusy) return true;
        
        var isConflict = IsConflictKeyPressed();
        return ModuleConfig.WorkMode switch
        {
            false => isConflict, // WorkMode=false: 按下打断键时跳过 (即不工作)
            true  => !isConflict // WorkMode=true:  没按下打断键时跳过 (即不工作)
        };
    }
    

    private class Config : ModuleConfiguration
    {
        // true - 按下打断热键才让人物面向与摄像机一致
        // false - 按下打断热键则不保持人物面向与摄像机一致
        public bool WorkMode;
    }

    [IPCProvider("DailyRoutines.Modules.AutoFaceCameraDirection.SetWorkMode")]
    public static void SetWorkModeIPC(bool workMode) => ModuleConfig.WorkMode = workMode;

    [IPCProvider("DailyRoutines.Modules.AutoFaceCameraDirection.CancelLockOn")]
    public static void CancelLockOnIPC()
    {
        LockOn         = false;
        LockOnRotation = 0;
    }

    [IPCProvider("DailyRoutines.Modules.AutoFaceCameraDirection.LockOnGround")]
    public static bool LockOnGroundIPC(string rotation)
    {
        if (!WorldDirectionToNormalizedDirection.TryGetValue(rotation, out var dirGround))
            return false;

        LockOnRotation = WorldDirHToCharaRotation(dirGround);
        LockOn         = true;

        return true;
    }

    [IPCProvider("DailyRoutines.Modules.AutoFaceCameraDirection.LockOnChara")]
    public static void LockOnCharaIPC(float rotation)
    {
        LockOnRotation = rotation;
        LockOn         = true;
    }

    [IPCProvider("DailyRoutines.Modules.AutoFaceCameraDirection.LockOnCamera")]
    public static void LockOnCameraIPC(float rotation)
    {
        LockOnRotation = CameraDirHToCharaRotation(rotation);
        LockOn         = true;
    }

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
}
