using System.Collections.Frozen;
using System.Numerics;
using System.Runtime.InteropServices;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Internal;
using DailyRoutines.Manager;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using OmenTools.Dalamud;
using OmenTools.Interop.Game;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class RightClickToMoveMode : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("RightClickToMoveModeTitle"),
        Description = Lang.Get("RightClickToMoveModeDescription"),
        Category    = ModuleCategory.General
    };

    private static Config ModuleConfig = null!;

    private static volatile bool IsModuleActive;

    private static MovementInputController? MovementController;

    protected override void Init()
    {
        ModuleConfig = Config.Load(this) ?? new();

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;

        if (IsModuleActive) return;
        IsModuleActive = true;

        MovementController ??= new()
        {
            Precision  = 0.15f,
            IsAutoMove = true
        };

        InputIDManager.Instance().RegPostPressed(OnPostPressed);
        WindowManager.Instance().PostDraw += OnDraw;
    }

    protected override void Uninit()
    {
        if (!IsModuleActive) return;

        InputIDManager.Instance().UnregPostPressed(OnPostPressed);
        
        SessionManager.Stop();
        TargetIndicatorRenderer.Reset();
        
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        WindowManager.Instance().PostDraw                -= OnDraw;
        
        if (MovementController != null)
        {
            MovementController.Dispose();
            MovementController = null;
        }
        
        IsModuleActive = false;
    }

    #region 事件

    private static void OnPostPressed(bool result, InputId id)
    {
        // 右键
        if (id != InputId.MOUSE_CANCEL) return;
        if (!result) return;
        
        OnMouseClickCaptured(ImGui.GetMousePos());
    }
    
    private static void OnDraw()
    {
        if (!IsModuleActive) return;

        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer)
        {
            SessionManager.Stop();
            TargetIndicatorRenderer.Draw(null);
            return;
        }

        if (IsInterruptKeysPressed())
            SessionManager.Stop();

        if (SessionManager.Current is { } session)
        {
            switch (session.Driver)
            {
                case MoveDriver.Game:
                    SessionManager.UpdateGame(session, localPlayer.Position);
                    break;
                case MoveDriver.Navmesh:
                    SessionManager.UpdateNavmesh(session, localPlayer.Position);
                    break;
            }
        }

        TargetIndicatorRenderer.Draw(SessionManager.Current);
    }
    
    private static void OnZoneChanged(ushort _) 
    {
        SessionManager.Stop();
        TargetIndicatorRenderer.Reset();
    }
    
    private static void OnMouseClickCaptured(Vector2 clientPosition)
    {
        if (!IsModuleActive) return;
        if (DService.Instance().ObjectTable.LocalPlayer is null || MovementController == null) return;
        if (!ClickTriggerEvaluator.ShouldHandle(ModuleConfig)) return;
        if (!ClickPointResolver.TryResolve(clientPosition, out var targetPosition)) return;

        SessionManager.Start(targetPosition);
    }

    #endregion
    
    #region 绘制
    
    protected override void ConfigUI()
    {
        ImGuiOm.ConflictKeyText();

        ImGui.NewLine();
        DrawMoveModeSection();

        ImGui.NewLine();
        DrawControlModeSection();

        ImGui.NewLine();
        DrawIndicatorSection();

        if (ImGui.Checkbox($"{Lang.Get("RightClickToMoveMode-WASDToInterrupt")}###WASDToInterrupt", ref ModuleConfig.WASDToInterrupt))
            ModuleConfig.Save(this);
    }
    
    private static void DrawMoveModeSection()
    {
        var navmeshAvailable = IsNavmeshAvailable();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("RightClickToMoveMode-MoveMode"));

        using (ImRaii.PushIndent())
        {
            ImGui.Spacing();

            foreach (var moveMode in Enum.GetValues<MoveMode>())
            {
                var unavailable = moveMode is MoveMode.Navmesh or MoveMode.Smart && !navmeshAvailable;
                using var disabled = ImRaii.Disabled(unavailable || moveMode == ModuleConfig.MoveMode);

                ImGui.SameLine();

                if (ImGui.RadioButton(MoveModeTitles[moveMode], moveMode == ModuleConfig.MoveMode))
                {
                    ModuleConfig.MoveMode = moveMode;
                    ModuleConfig.Save(ModuleManager.Instance().GetModule<RightClickToMoveMode>());
                }
            }

            ImGui.TextUnformatted(MoveModeDescriptions[ModuleConfig.MoveMode]);

            if (!navmeshAvailable)
                ImGui.TextDisabled(Lang.Get("RightClickToMoveMode-NavmeshUnavailable"));
        }
    }

    private static void DrawControlModeSection()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("RightClickToMoveMode-ControlMode"));

        using (ImRaii.PushIndent())
        {
            ImGui.Spacing();

            foreach (var controlMode in Enum.GetValues<ControlMode>())
            {
                using var disabled = ImRaii.Disabled(controlMode == ModuleConfig.ControlMode);

                ImGui.SameLine();

                if (ImGui.RadioButton(ControlModeTitles[controlMode], controlMode == ModuleConfig.ControlMode))
                {
                    ModuleConfig.ControlMode = controlMode;
                    ModuleConfig.Save(ModuleManager.Instance().GetModule<RightClickToMoveMode>());
                }
            }

            ImGui.TextUnformatted(ControlModeDescriptions[ModuleConfig.ControlMode]);

            if (ModuleConfig.ControlMode != ControlMode.KeyRightClick) return;

            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted($"{Lang.Get("RightClickToMoveMode-ComboKey")}:");

            ImGui.SameLine();
            ImGui.SetNextItemWidth(200f * GlobalUIScale);
            using var combo = ImRaii.Combo("###ComboKeyCombo", ModuleConfig.ComboKey.GetFancyName());

            if (!combo) return;

            var validKeys = DService.Instance().KeyState.GetValidVirtualKeys();
            foreach (var keyToSelect in validKeys)
            {
                using var disabled = ImRaii.Disabled(PluginConfig.Instance().ConflictKeyBinding.Keyboard == keyToSelect);

                if (ImGui.Selectable(keyToSelect.GetFancyName()))
                {
                    ModuleConfig.ComboKey = keyToSelect;
                    ModuleConfig.Save(ModuleManager.Instance().GetModule<RightClickToMoveMode>());
                }
            }
        }
    }

    private static void DrawIndicatorSection()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("RightClickToMoveMode-IndicatorStyle"));

        using (ImRaii.PushIndent())
        {
            ImGui.Spacing();

            foreach (var indicatorStyle in Enum.GetValues<IndicatorStyle>())
            {
                using var disabled = ImRaii.Disabled(indicatorStyle == ModuleConfig.IndicatorStyle);

                ImGui.SameLine();

                if (ImGui.RadioButton(IndicatorStyleTitles[indicatorStyle], indicatorStyle == ModuleConfig.IndicatorStyle))
                {
                    ModuleConfig.IndicatorStyle = indicatorStyle;
                    ModuleConfig.Save(ModuleManager.Instance().GetModule<RightClickToMoveMode>());
                }
            }

            ImGui.TextUnformatted(IndicatorStyleDescriptions[ModuleConfig.IndicatorStyle]);
        }
    }

    #endregion

    #region 辅助方法

    private static bool ShouldUseGameMove(Vector3 localPlayerPosition, Vector3 targetPosition)
    {
        if (!IsNavmeshAvailable()) 
            return true;
        
        if (MathF.Abs(localPlayerPosition.Y - targetPosition.Y) > SMART_GAME_HEIGHT_DELTA)
            return false;
        
        var isBlocked = MovementManager.TryDetectTwoPoints(localPlayerPosition, targetPosition, out _) ?? true;
        return !isBlocked && Vector2.DistanceSquared(localPlayerPosition.ToVector2(), targetPosition.ToVector2()) <= SMART_GAME_DISTANCE_SQ;
    }
    
    private static MoveDriver ResolveMoveDriver(Vector3 localPlayerPosition, Vector3 targetPosition) =>
        ModuleConfig.MoveMode switch
        {
            MoveMode.Game    => MoveDriver.Game,
            MoveMode.Navmesh => IsNavmeshAvailable() ? MoveDriver.Navmesh : MoveDriver.Game,
            MoveMode.Smart   => ShouldUseGameMove(localPlayerPosition, targetPosition) ? MoveDriver.Game : MoveDriver.Navmesh,
            _                => MoveDriver.Game
        };
    
    private static bool IsInterruptKeysPressed()
    {
        if (PluginConfig.Instance().ConflictKeyBinding.IsPressed()) return true;

        return ModuleConfig.WASDToInterrupt &&
               (DService.Instance().KeyState[VirtualKey.W] ||
                DService.Instance().KeyState[VirtualKey.A] ||
                DService.Instance().KeyState[VirtualKey.S] ||
                DService.Instance().KeyState[VirtualKey.D]);
    }

    private static bool IsNavmeshAvailable() =>
        DService.Instance().PI.IsPluginEnabled(vnavmeshIPC.INTERNAL_NAME) && vnavmeshIPC.GetIsNavReady();

    #endregion

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private sealed class Config : ModuleConfig
    {
        public VirtualKey     ComboKey        = VirtualKey.SHIFT;
        public ControlMode    ControlMode     = ControlMode.RightClick;
        public IndicatorStyle IndicatorStyle  = IndicatorStyle.Pulse;
        public MoveMode       MoveMode        = MoveMode.Smart;
        public bool           WASDToInterrupt = true;
    }

    private static class SessionManager
    {
        public static MoveSession? Current { get; private set; }

        public static void Start(Vector3 targetPosition)
        {
            if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer || MovementController == null) return;

            Stop();

            if (LocalPlayerState.Instance().IsMoving)
                ChatManager.Instance().SendMessage("/automove off");

            var driver = ResolveMoveDriver(localPlayer.Position, targetPosition);
            switch (driver)
            {
                case MoveDriver.Game:
                    StartGame(targetPosition);
                    break;
                case MoveDriver.Navmesh:
                    StartNavmesh(targetPosition);
                    break;
            }
        }

        public static void Stop()
        {
            var session = Current;
            Current = null;
            if (session == null) return;

            if (MovementController != null)
            {
                MovementController.Enabled         = false;
                MovementController.DesiredPosition = default;
            }

            vnavmeshIPC.StopPathfind();

            if (session is { Driver: MoveDriver.Game })
                MovementManager.Instance().SetCurrentControlMode(session.PreviousControlMode);
        }

        public static void UpdateGame(MoveSession session, Vector3 localPlayerPosition)
        {
            if (Vector2.DistanceSquared(session.Target.ToVector2(), localPlayerPosition.ToVector2()) <= GAME_ARRIVAL_DISTANCE_SQ)
                Stop();
        }

        public static void UpdateNavmesh(MoveSession session, Vector3 localPlayerPosition)
        {
            if (Vector2.DistanceSquared(session.Target.ToVector2(), localPlayerPosition.ToVector2()) <= NAVMESH_ARRIVAL_DISTANCE_SQ)
            {
                Stop();
                return;
            }

            var isBusy = vnavmeshIPC.GetIsPathfindRunning() || vnavmeshIPC.GetIsPathfindInProgress() || vnavmeshIPC.GetIsNavPathfindInProgress();
            if (!isBusy && session.ElapsedSeconds >= 0.15f)
                Stop();
        }

        private static void StartGame(Vector3 targetPosition)
        {
            if (MovementController == null) return;

            var previousControlMode = MovementManager.Instance().CurrentControlMode;
            MovementManager.Instance().SetCurrentControlMode(MovementControlMode.Normal);

            MovementController.DesiredPosition = targetPosition;
            MovementController.Enabled         = true;

            Current = new(targetPosition, MoveDriver.Game, previousControlMode);
            TargetIndicatorRenderer.Trigger(targetPosition);
        }

        private static void StartNavmesh(Vector3 targetPosition)
        {
            if (MovementController != null)
            {
                MovementController.Enabled         = false;
                MovementController.DesiredPosition = default;
            }
            
            var fly = DService.Instance().Condition[ConditionFlag.InFlight] || DService.Instance().Condition[ConditionFlag.Diving];
            if (!vnavmeshIPC.PathfindAndMoveTo(targetPosition, fly)) return;

            Current = new(targetPosition, MoveDriver.Navmesh, MovementManager.Instance().CurrentControlMode);
            TargetIndicatorRenderer.Trigger(targetPosition);
        }
    }

    private sealed class MoveSession(Vector3 target, MoveDriver driver, MovementControlMode previousControlMode)
    {
        public Vector3             Target              { get; } = target;
        public MoveDriver          Driver              { get; } = driver;
        public MovementControlMode PreviousControlMode { get; } = previousControlMode;
        public long                StartedAtTicks      { get; } = Environment.TickCount64;

        public float ElapsedSeconds => (Environment.TickCount64 - StartedAtTicks) / 1000f;
    }

    private static class ClickTriggerEvaluator
    {
        public static bool ShouldHandle(Config config) =>
            config.ControlMode switch
            {
                ControlMode.RightClick => true,
                ControlMode.LeftRightClick => (GetAsyncKeyState(0x01) & 0x8000) != 0,
                ControlMode.KeyRightClick => DService.Instance().KeyState[config.ComboKey],
                _ => false
            };
    }

    private static class ClickPointResolver
    {
        public static bool TryResolve(Vector2 clientPosition, out Vector3 targetPosition)
        {
            targetPosition = default;

            if (!DService.Instance().GameGUI.ScreenToWorld(clientPosition, out var worldPosition))
                return false;

            if (IsNavmeshAvailable() && vnavmeshIPC.QueryNearestPointOnMesh(worldPosition, 3f, 10f) is { } meshPosition)
            {
                targetPosition = meshPosition;
                return true;
            }

            if (MovementManager.TryDetectGroundDownwards(worldPosition, out var hitInfo, 1024) ?? false)
            {
                targetPosition = hitInfo.Point;
                return true;
            }

            return false;
        }
    }

    private static class TargetIndicatorRenderer
    {
        private static Vector3 PulseTarget;
        private static long    PulseStartedAtTicks;
        private static bool    IsPulseActive;

        public static void Trigger(Vector3 targetPosition)
        {
            if (ModuleConfig.IndicatorStyle != IndicatorStyle.Pulse)
            {
                ResetPulse();
                return;
            }

            PulseTarget          = targetPosition;
            PulseStartedAtTicks  = Environment.TickCount64;
            IsPulseActive        = true;
        }

        public static void Draw(MoveSession? session)
        {
            if (ModuleConfig.IndicatorStyle == IndicatorStyle.None) return;

            switch (ModuleConfig.IndicatorStyle)
            {
                case IndicatorStyle.Pulse:
                    DrawPulse();
                    break;
                case IndicatorStyle.Marker when session != null:
                    DrawMarker(session.Target);
                    break;
            }
        }

        public static void Reset()
        {
            ResetPulse();
        }

        private static void DrawPulse()
        {
            if (!IsPulseActive) return;
            if (!DService.Instance().GameGUI.WorldToScreen(PulseTarget, out var screenPosition))
            {
                ResetPulse();
                return;
            }

            var elapsed = (Environment.TickCount64 - PulseStartedAtTicks) / 1000f;
            if (elapsed >= PULSE_DURATION_SECONDS)
            {
                ResetPulse();
                return;
            }

            var progress  = Math.Clamp(elapsed / PULSE_DURATION_SECONDS, 0f, 1f);
            var alpha     = 0.85f * (1f - progress);
            var radius    = (PULSE_START_RADIUS + PULSE_EXPAND_RADIUS * progress) * GlobalUIScale;
            var thickness = MathF.Max(1.75f * GlobalUIScale, 4f * GlobalUIScale * (1f - progress * 0.6f));

            var drawList = ImGui.GetForegroundDrawList();
            drawList.AddCircle(screenPosition, radius, IndicatorColor.WithAlpha(alpha).ToUInt(), 32, thickness);
            drawList.AddCircleFilled(screenPosition, 4f * GlobalUIScale,
                                     IndicatorInnerColor.WithAlpha(0.25f + alpha * 0.25f).ToUInt(), 16);
        }

        private static void DrawMarker(Vector3 targetPosition)
        {
            if (!DService.Instance().GameGUI.WorldToScreen(targetPosition, out var screenPosition))
                return;

            var radius    = MARKER_RADIUS * GlobalUIScale;
            var drawList  = ImGui.GetForegroundDrawList();
            var color     = IndicatorColor.WithAlpha(0.95f).ToUInt();
            var inner     = IndicatorInnerColor.WithAlpha(0.45f).ToUInt();
            var crossSize = radius * 0.65f;

            drawList.AddCircle(screenPosition, radius, color, 24, 2.5f * GlobalUIScale);
            drawList.AddCircleFilled(screenPosition, 4f * GlobalUIScale, inner, 16);
            drawList.AddLine(screenPosition + new Vector2(-crossSize, 0), screenPosition + new Vector2(crossSize, 0), color, 2f * GlobalUIScale);
            drawList.AddLine(screenPosition + new Vector2(0, -crossSize), screenPosition + new Vector2(0, crossSize), color, 2f * GlobalUIScale);
        }

        private static void ResetPulse()
        {
            PulseTarget         = default;
            PulseStartedAtTicks = 0;
            IsPulseActive       = false;
        }
    }
    
    private enum ControlMode
    {
        RightClick,
        LeftRightClick,
        KeyRightClick
    }

    private enum MoveMode
    {
        Game,
        Navmesh,
        Smart
    }

    private enum MoveDriver
    {
        Game,
        Navmesh
    }

    private enum IndicatorStyle
    {
        None,
        Pulse,
        Marker
    }
    
    #region 预置数据
    
    private const float GAME_ARRIVAL_DISTANCE_SQ    = 2.25f;
    private const float NAVMESH_ARRIVAL_DISTANCE_SQ = 2.25f;
    private const float SMART_GAME_DISTANCE_SQ      = 144f;
    private const float SMART_GAME_HEIGHT_DELTA     = 1.5f;
    private const float PULSE_DURATION_SECONDS      = 0.45f;
    private const float MARKER_RADIUS               = 11f;
    private const float PULSE_START_RADIUS          = 14f;
    private const float PULSE_EXPAND_RADIUS         = 30f;
    
    private static readonly Vector4 IndicatorColor      = KnownColor.DeepSkyBlue.ToVector4();
    private static readonly Vector4 IndicatorInnerColor = KnownColor.LightSkyBlue.ToVector4();

    private static readonly FrozenDictionary<MoveMode, string> MoveModeTitles = new Dictionary<MoveMode, string>
    {
        [MoveMode.Game]    = Lang.Get("RightClickToMoveMode-MoveMode-Game"),
        [MoveMode.Navmesh] = Lang.Get("RightClickToMoveMode-MoveMode-Navmesh"),
        [MoveMode.Smart]   = Lang.Get("RightClickToMoveMode-MoveMode-Smart")
    }.ToFrozenDictionary();
    private static readonly FrozenDictionary<MoveMode, string> MoveModeDescriptions = new Dictionary<MoveMode, string>
    {
        [MoveMode.Game]    = Lang.Get("RightClickToMoveMode-MoveMode-Game-Desc"),
        [MoveMode.Navmesh] = Lang.Get("RightClickToMoveMode-MoveMode-Navmesh-Desc"),
        [MoveMode.Smart]   = Lang.Get("RightClickToMoveMode-MoveMode-Smart-Desc")
    }.ToFrozenDictionary();
    private static readonly FrozenDictionary<ControlMode, string> ControlModeTitles = new Dictionary<ControlMode, string>
    {
        [ControlMode.RightClick]     = Lang.Get("RightClickToMoveMode-RightClickMode-Title"),
        [ControlMode.LeftRightClick] = Lang.Get("RightClickToMoveMode-LeftRightClickMode-Title"),
        [ControlMode.KeyRightClick]  = Lang.Get("RightClickToMoveMode-KeyRightClickMode-Title")
    }.ToFrozenDictionary();
    private static readonly FrozenDictionary<ControlMode, string> ControlModeDescriptions = new Dictionary<ControlMode, string>
    {
        [ControlMode.RightClick]     = Lang.Get("RightClickToMoveMode-RightClickMode-Desc"),
        [ControlMode.LeftRightClick] = Lang.Get("RightClickToMoveMode-LeftRightClickMode-Desc"),
        [ControlMode.KeyRightClick]  = Lang.Get("RightClickToMoveMode-KeyRightClickMode-Desc")
    }.ToFrozenDictionary();
    private static readonly FrozenDictionary<IndicatorStyle, string> IndicatorStyleTitles = new Dictionary<IndicatorStyle, string>
    {
        [IndicatorStyle.None]   = Lang.Get("RightClickToMoveMode-IndicatorStyle-None"),
        [IndicatorStyle.Pulse]  = Lang.Get("RightClickToMoveMode-IndicatorStyle-Pulse"),
        [IndicatorStyle.Marker] = Lang.Get("RightClickToMoveMode-IndicatorStyle-Marker")
    }.ToFrozenDictionary();
    private static readonly FrozenDictionary<IndicatorStyle, string> IndicatorStyleDescriptions = new Dictionary<IndicatorStyle, string>
    {
        [IndicatorStyle.None]   = Lang.Get("RightClickToMoveMode-IndicatorStyle-None-Desc"),
        [IndicatorStyle.Pulse]  = Lang.Get("RightClickToMoveMode-IndicatorStyle-Pulse-Desc"),
        [IndicatorStyle.Marker] = Lang.Get("RightClickToMoveMode-IndicatorStyle-Marker-Desc")
    }.ToFrozenDictionary();

    #endregion
}
