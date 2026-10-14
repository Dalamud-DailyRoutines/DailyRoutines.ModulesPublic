using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using OmenTools.Interop.Game.Models;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class AutoCastMovementLock
{
    private static readonly CompSig ReadWalkInputSig = new("E8 ?? ?? ?? ?? 80 7B 3E 00 48 8D 3D");
    private delegate void ReadWalkInputDelegate
    (
        nint   self,
        float* left,
        float* forward,
        float* turn,
        byte*  backwardOrStrafe,
        byte*  a6,
        byte   additive
    );
    private Hook<ReadWalkInputDelegate> ReadWalkInputHook;

    private static readonly CompSig ReadFlyInputSig = new("E8 ?? ?? ?? ?? 0F B6 0D ?? ?? ?? ?? B8");
    private delegate void ReadFlyInputDelegate
    (
        nint      self,
        FlyInput* input
    );
    private Hook<ReadFlyInputDelegate> ReadFlyInputHook;

    private static readonly CompSig MouseMovementSig = new("48 8B C4 48 89 70 18 48 89 78 20 55 41 56");
    private delegate void MouseMovementDelegate
    (
        nint     self,
        float    horizontal,
        float    vertical,
        char     a4,
        byte     alignWithCamera,
        Vector3* direction
    );
    private Hook<MouseMovementDelegate> MouseMovementHook;

    private void ReadWalkInputDetour
    (
        nint   self,
        float* left,
        float* forward,
        float* turn,
        byte*  backwardOrStrafe,
        byte*  a6,
        byte   additive
    )
    {
        ReadWalkInputHook.Original(self, left, forward, turn, backwardOrStrafe, a6, additive);

        if (IObjectTable.Instance().LocalPlayer is not { } localPlayer)
            return;
        if (additive != 0 || !inputInterceptor.ShouldBlockMovement(localPlayer))
            return;

        *left    = 0;
        *forward = 0;
    }

    private void ReadFlyInputDetour
    (
        nint      self,
        FlyInput* input
    )
    {
        ReadFlyInputHook.Original(self, input);

        if (IObjectTable.Instance().LocalPlayer is not { } localPlayer)
            return;
        if (!inputInterceptor.ShouldBlockMovement(localPlayer))
            return;

        input->Forward = 0;
        input->Left    = 0;
        input->Up      = 0;
    }

    private void MouseMovementDetour
    (
        nint     self,
        float    horizontal,
        float    vertical,
        char     a4,
        byte     alignWithCamera,
        Vector3* direction
    )
    {
        if (IObjectTable.Instance().LocalPlayer is not { } localPlayer)
            return;

        if (*(byte*)(self + MOUSE_MOVEMENT_FLAG_OFFSET) != 0 &&
            inputInterceptor.ShouldBlockMovement(localPlayer))
            return;

        MouseMovementHook.Original(self, horizontal, vertical, a4, alignWithCamera, direction);
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct FlyInput
    {
        [FieldOffset(0)]
        public float Forward;
        [FieldOffset(4)]
        public float Left;
        [FieldOffset(8)]
        public float Up;
    }

    #region 常量

    private const int MOUSE_MOVEMENT_FLAG_OFFSET = 63;

    #endregion
}
