using System.Numerics;
using System.Runtime.InteropServices;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using OmenTools.Interop.Game;
using OmenTools.Interop.Game.Models;

namespace DailyRoutines.ModulesPublic;

public unsafe class PlaceFurnitureAnywhere : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("PlaceFurnitureAnywhereTitle"),
        Description = Lang.Get("PlaceFurnitureAnywhereDescription"),
        Category    = ModuleCategory.System
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private static readonly CompSig                      RaycastFilterSig = new("48 8B C4 48 89 58 ?? 48 89 70 ?? 57 48 81 EC ?? ?? ?? ?? 33 DB 48 8B F2");
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool RaycastFilterDelegate
    (
        BGCollisionModule* module,
        RaycastHit*        hitInfo,
        Vector3*           origin,
        Vector3*           direction,
        float              maxDistance,
        int                layerMask,
        int*               flags
    );
    private                 Hook<RaycastFilterDelegate>? RaycastFilterHook;
    
    private MemoryPatch? patch0;
    private MemoryPatch? patch1;
    private MemoryPatch? patch2;

    protected override void Init()
    {
        var baseAddress0 = DService.Instance().SigScanner.ScanText("C6 83 ?? ?? ?? ?? ?? 0F 29 44 24") + 6;
        patch0 = new(baseAddress0, [0x1]);
        patch0.Enable();

        var baseAddress1 = DService.Instance().SigScanner.ScanText("48 85 C0 74 ?? C6 87 ?? ?? 00 00 00") + 11;
        patch1 = new(baseAddress1, [0x1]);
        patch1.Enable();

        var baseAddress2 = DService.Instance().SigScanner.ScanText("C6 87 83 01 00 00 00 48 83 C4 ??") + 6;
        patch2 = new(baseAddress2, [0x1]);
        patch2.Enable();

        RaycastFilterHook ??= RaycastFilterSig.GetHook<RaycastFilterDelegate>(RaycastFilterDetour);
        RaycastFilterHook.Enable();
    }
    
    protected override void Uninit()
    {
        patch0?.Disable();
        patch1?.Disable();
        patch2?.Disable();
    }

    private bool RaycastFilterDetour
    (
        BGCollisionModule* module,
        RaycastHit*        hitInfo,
        Vector3*           origin,
        Vector3*           direction,
        float              maxDistance,
        int                layerMask,
        int*               flags
    )
    {
        if (!DService.Instance().Condition[ConditionFlag.UsingHousingFunctions])
            return RaycastFilterHook.Original(module, hitInfo, origin, direction, maxDistance, layerMask, flags);

        return false;
    }
}
