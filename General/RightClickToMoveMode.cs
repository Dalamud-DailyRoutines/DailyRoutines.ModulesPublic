using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using DailyRoutines.Abstracts;
using DailyRoutines.IPC;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace DailyRoutines.ModulesPublic;

public unsafe class RightClickToMoveMode : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("RightClickToMoveModeTitle"),
        Description = GetLoc("RightClickToMoveModeDescription"),
        Category    = ModuleCategories.General,
    };

    private static readonly Dictionary<ControlMode, (string Title, string Desc)> ControlModes = new()
    {
        [ControlMode.RightClick]     = (GetLoc("RightClickToMoveMode-RightClickMode-Title"), GetLoc("RightClickToMoveMode-RightClickMode-Desc")),
        [ControlMode.LeftRightClick] = (GetLoc("RightClickToMoveMode-LeftRightClickMode-Title"), GetLoc("RightClickToMoveMode-LeftRightClickMode-Desc")),
        [ControlMode.KeyRightClick]  = (GetLoc("RightClickToMoveMode-KeyRightClickMode-Title"), GetLoc("RightClickToMoveMode-KeyRightClickMode-Desc")),
    };
    
    private static readonly CompSig                              GameObjectSetRotationSig = new("40 53 48 83 EC ?? F3 0F 10 81 ?? ?? ?? ?? 48 8B D9 0F 2E C1");
    private delegate        void                                 GameObjectSetRotationDelegate(nint obj, float value);
    private static          Hook<GameObjectSetRotationDelegate>? GameObjectSetRotationHook;

    private static volatile bool   IsModuleActive;

    private static readonly uint LineColor = KnownColor.LightSkyBlue.ToVector4().ToUInt();
    private static readonly uint DotColor  = KnownColor.RoyalBlue.ToVector4().ToUInt();
    private static readonly uint TextColor = KnownColor.Orange.ToVector4().ToUInt();
    
    private static Vector3 TargetWorldPos;

    private static Config          ModuleConfig;
    private static PathFindHelper? PathFindHelper;
    
    private static WindowHook? Hook;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        GameObjectSetRotationHook ??= GameObjectSetRotationSig.GetHook<GameObjectSetRotationDelegate>(GameObjectSetRotationDetour);
        GameObjectSetRotationHook.Enable();

        if (!IsPluginEnabled(vnavmeshIPC.InternalName))
        {
            ModuleConfig.MoveMode = MoveMode.Game;
            SaveConfig(ModuleConfig);
        }

        DService.ClientState.TerritoryChanged += OnZoneChanged;

        if (IsModuleActive) return;
        IsModuleActive = true;

        try
        {
            PathFindHelper ??= new PathFindHelper { Precision = 1f };

            Hook ??= new(Framework.Instance()->GameWindow->WindowHandle, HandleClickResult);

            WindowManager.Draw += OnPosDraw;
        }
        catch
        {
            CleanupResources();
            throw;
        }
    }

    protected override void ConfigUI()
    {
        ConflictKeyText();
        
        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("RightClickToMoveMode-MoveMode")}");

        using (ImRaii.PushIndent())
        {
            ImGui.Spacing();
            
            foreach (var moveMode in Enum.GetValues<MoveMode>())
            {
                using var disabled = ImRaii.Disabled(ModuleConfig.MoveMode == moveMode || 
                                                     (moveMode == MoveMode.vnavmesh && !IsPluginEnabled(vnavmeshIPC.InternalName)));

                ImGui.SameLine();
                if (ImGui.RadioButton(moveMode.ToString(), moveMode == ModuleConfig.MoveMode))
                {
                    ModuleConfig.MoveMode = moveMode;
                    SaveConfig(ModuleConfig);
                }
            }
        }
        
        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("RightClickToMoveMode-ControlMode")}");

        using (ImRaii.PushIndent())
        {
            ImGui.Spacing();
            
            foreach (var controlMode in Enum.GetValues<ControlMode>())
            {
                using var disabled = ImRaii.Disabled(controlMode == ModuleConfig.ControlMode);

                ImGui.SameLine();
                if (ImGui.RadioButton(ControlModes[controlMode].Title, controlMode == ModuleConfig.ControlMode))
                {
                    ModuleConfig.ControlMode = controlMode;
                    SaveConfig(ModuleConfig);
                }
            }
            
            ImGui.Text(ControlModes[ModuleConfig.ControlMode].Desc);

            if (ModuleConfig.ControlMode == ControlMode.KeyRightClick)
            {
                ImGui.AlignTextToFramePadding();
                ImGui.Text($"{GetLoc("RightClickToMoveMode-ComboKey")}:");

                ImGui.SameLine();
                ImGui.SetNextItemWidth(200f * GlobalFontScale);
                using var combo = ImRaii.Combo("###ComboKeyCombo", ModuleConfig.ComboKey.GetFancyName());
                if (combo)
                {
                    var validKeys = DService.KeyState.GetValidVirtualKeys();
                    foreach (var keyToSelect in validKeys)
                    {
                        using var disabled = ImRaii.Disabled(DRConfig.Instance().ConflictKey == keyToSelect);
                        if (ImGui.Selectable(keyToSelect.GetFancyName()))
                        {
                            ModuleConfig.ComboKey = keyToSelect;
                            SaveConfig(ModuleConfig);
                        }
                    }
                }
            }
        }
        
        ImGui.NewLine();
        
        if (ImGui.Checkbox($"{GetLoc("RightClickToMoveMode-DisplayLineToTarget")}###DisplayLineToTarget", ref ModuleConfig.DisplayLineToTarget))
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox($"{GetLoc("RightClickToMoveMode-NoChangeFaceDirection")}###NoChangeFaceDirection", ref ModuleConfig.NoChangeFaceDirection))
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox($"{GetLoc("RightClickToMoveMode-WASDToInterrupt")}###WASDToInterrupt", ref ModuleConfig.WASDToInterrupt))
            SaveConfig(ModuleConfig);
    }

    protected override void Uninit() => 
        CleanupResources();

    private static void OnPosDraw()
    {
        if (!IsModuleActive) return;

        if (TargetWorldPos == default) return;
        if (DService.ObjectTable.LocalPlayer is not { } localPlayer) return;

        MovementManager.SetCurrentControlMode(MovementControlMode.Normal);
        
        var distance = Vector2.DistanceSquared(TargetWorldPos.ToVector2(), localPlayer.Position.ToVector2());
        if (IsInterruptKeysPressed() || distance <= 4f)
            StopPathFind();

        if (!ModuleConfig.DisplayLineToTarget) return;

        if (!DService.Gui.WorldToScreen(TargetWorldPos,       out var screenPos) ||
            !DService.Gui.WorldToScreen(localPlayer.Position, out var localScreenPos)) 
            return;

        var drawList = ImGui.GetForegroundDrawList();

        drawList.AddLine(localScreenPos, screenPos, LineColor, 8f);
        drawList.AddCircleFilled(localScreenPos, 12f, DotColor);
        drawList.AddCircleFilled(screenPos,      12f, DotColor);

        ImGuiOm.TextOutlined(screenPos + ScaledVector2(16f),
                             TextColor,
                             GetLoc("RightClickToMoveMode-TextDisplay",
                                    $"[{TargetWorldPos.X:F1}, {TargetWorldPos.Y:F1}, {TargetWorldPos.Z:F1}]",
                                    MathF.Sqrt(distance).ToString("F2")));
    }
    
    private static void OnZoneChanged(ushort obj) => 
        StopPathFind();

    private static void GameObjectSetRotationDetour(nint obj, float value)
    {
        if (ModuleConfig.NoChangeFaceDirection                                         &&
            obj            == (DService.ObjectTable.LocalPlayer?.Address ?? nint.Zero) &&
            TargetWorldPos != default)
            return;
        
        GameObjectSetRotationHook.Original(obj, value);
    }

    private static bool IsInterruptKeysPressed()
    {
        if (IsConflictKeyPressed()) return true;
        if (ModuleConfig.WASDToInterrupt && 
            (DService.KeyState[VirtualKey.W] || 
             DService.KeyState[VirtualKey.A] ||
             DService.KeyState[VirtualKey.S] || 
             DService.KeyState[VirtualKey.D])) 
            return true;

        return false;
    }

    private static void HandleClickResult()
    {
        if (!IsModuleActive) return;
        if (DService.ObjectTable.LocalPlayer is null || PathFindHelper == null) return;

        switch (ModuleConfig.ControlMode)
        {
            case ControlMode.RightClick:
                break;
            case ControlMode.LeftRightClick:
                var isLeftButtonPressed = (GetAsyncKeyState(0x01) & 0x8000) != 0;
                if (!isLeftButtonPressed) return;
                break;
            case ControlMode.KeyRightClick:
                var isKeyPressed = DService.KeyState[ModuleConfig.ComboKey];
                if (!isKeyPressed) return;
                break;
        }

        if (!DService.Gui.ScreenToWorld(ImGui.GetMousePos(), out var worldPos)) return;
        
        var finalWorldPos = Vector3.Zero;
        if (IsPluginEnabled(vnavmeshIPC.InternalName) &&
            vnavmeshIPC.QueryMeshNearestPoint(worldPos, 3, 10) is { } worldPosByNavmesh)
            finalWorldPos = worldPosByNavmesh;
        else if (MovementManager.TryDetectGroundDownwards(worldPos, out var hitInfo, 1024) ?? false)
            finalWorldPos = hitInfo.Point;
        else 
            return;

        StopPathFind();
        TargetWorldPos = finalWorldPos;

        if (AgentMap.Instance()->IsPlayerMoving)
            ChatManager.SendMessage("/automove off");
        
        switch (ModuleConfig.MoveMode)
        {
            case MoveMode.Game:
                PathFindHelper.DesiredPosition = finalWorldPos;
                PathFindHelper.Enabled         = true;
                break;
            case MoveMode.vnavmesh:
                if (!IsPluginEnabled(vnavmeshIPC.InternalName))
                {
                    ModuleConfig.MoveMode = MoveMode.Game;
                    ModuleConfig.Save(ModuleManager.GetModule<RightClickToMoveMode>());
                    return;
                }
                
                vnavmeshIPC.PathSetTolerance(2f);
                vnavmeshIPC.PathfindAndMoveTo(TargetWorldPos, DService.Condition[ConditionFlag.InFlight] || DService.Condition[ConditionFlag.Diving]);
                break;
        }
    }

    private static void StopPathFind()
    {
        TargetWorldPos = default;
        
        if (PathFindHelper != null)
            PathFindHelper.Enabled = false;
        
        vnavmeshIPC.PathStop();
    }

    private static void CleanupResources()
    {
        if (!IsModuleActive) return;

        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        WindowManager.Draw               -= OnPosDraw;

        Hook?.Dispose();

        if (PathFindHelper != null)
        {
            PathFindHelper.Dispose();
            PathFindHelper = null;
        }

        TargetWorldPos   = default;
        IsModuleActive   = false;
    }

    private class Config : ModuleConfiguration
    {
        public MoveMode    MoveMode            = MoveMode.Game;
        public ControlMode ControlMode         = ControlMode.RightClick;
        public VirtualKey  ComboKey            = VirtualKey.SHIFT;
        public bool        DisplayLineToTarget = true;
        public bool        NoChangeFaceDirection;
        public bool        WASDToInterrupt = true;
    }

    public enum MoveMode
    {
        Game,
        vnavmesh
    }

    public enum ControlMode
    {
        RightClick,
        LeftRightClick,
        KeyRightClick,
    }

    public class WindowHook
    {
        private delegate nint Win32MouseProc(int nCode, nint wParam, nint lParam);

        private static nint           HookID = nint.Zero;
        private static Win32MouseProc MouseProc;
        private const  int            WH_MOUSE       = 7;
        private const  int            WM_RBUTTONDOWN = 0x0204;
        private const  int            WM_ACTIVATE    = 0x0006;

        private static nint       GameWindowHandle;
        private static bool       IsModuleActive = true;
        private static Action     HandleClickCallback;
        private        WindowProc WindowHookProc;
        private        nint       OldWndProc;

        public WindowHook(nint windowHandle, Action clickCallback)
        {
            GameWindowHandle    = windowHandle;
            HandleClickCallback = clickCallback;
            MouseProc           = MouseHookCallback;

            WindowHookProc = WndProc;
            OldWndProc     = GetWindowLongPtr(GameWindowHandle, -4);
            SetWindowLongPtr(GameWindowHandle, -4, Marshal.GetFunctionPointerForDelegate(WindowHookProc));

            StartHook();
        }

        private delegate nint WindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

        private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
        {
            if (msg == WM_ACTIVATE)
            {
                StopHook();
                StartHook();
            }

            return CallWindowProc(OldWndProc, hWnd, msg, wParam, lParam);
        }

        private static void StartHook()
        {
            if (GameWindowHandle == nint.Zero) return;

            var threadID = GetWindowThreadProcessID(GameWindowHandle, out _);
            HookID = SetWindowsHookEx(WH_MOUSE, MouseProc, nint.Zero, threadID);

            if (HookID == nint.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                throw new Exception($"Failed to set mouse hook, error code: {error}");
            }
        }

        private static void StopHook()
        {
            if (HookID != nint.Zero)
            {
                UnhookWindowsHookEx(HookID);
                HookID = nint.Zero;
            }
        }

        private static nint MouseHookCallback(int nCode, nint wParam, nint lParam)
        {
            if (!IsModuleActive) return CallNextHookEx(nint.Zero, nCode, wParam, lParam);

            if (nCode >= 0 && HookID != nint.Zero)
            {
                var mouseStruct = (MOUSEHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MOUSEHOOKSTRUCT));

                if ((int)wParam == WM_RBUTTONDOWN && mouseStruct.hwnd == GameWindowHandle)
                {
                    var clientPoint = new Vector2() { X = mouseStruct.pt.X, Y = mouseStruct.pt.Y };
                    ScreenToClient(GameWindowHandle, ref clientPoint);

                    HandleClickCallback();
                }
            }

            return CallNextHookEx(HookID, nCode, wParam, lParam);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEHOOKSTRUCT
        {
            public Vector2 pt;
            public nint    hwnd;
            public uint    wHitTestCode;
            public nint    dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowThreadProcessId")]
        private static extern uint GetWindowThreadProcessID(nint hWnd, out uint lpdwProcessID);

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookExW")]
        private static extern nint SetWindowsHookEx(int idHook, Win32MouseProc lpfn, nint hMod, uint dwThreadID);

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "UnhookWindowsHookEx")]
        private static extern bool UnhookWindowsHookEx(nint hhk);

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "CallNextHookEx")]
        private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

        [DllImport("user32.dll", EntryPoint = "ScreenToClient")]
        private static extern bool ScreenToClient(nint hWnd, ref Vector2 lpPoint);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

        [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
        private static extern nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, uint Msg, nint wParam, nint lParam);

        public void Dispose()
        {
            StopHook();
            if (GameWindowHandle != nint.Zero && OldWndProc != nint.Zero)
                SetWindowLongPtr(GameWindowHandle, -4, OldWndProc);
        }
    }

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);
}
