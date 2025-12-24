using System.Numerics;
using System.Runtime.InteropServices;
using DailyRoutines.Abstracts;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace DailyRoutines.ModulesPublic;

public unsafe class PlaceFurnitureAnywhere : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("PlaceFurnitureAnywhereTitle"),
        Description = GetLoc("PlaceFurnitureAnywhereDescription"),
        Category    = ModuleCategories.System
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static MemoryPatch? Patch0;
    private static MemoryPatch? Patch1;
    private static MemoryPatch? Patch2;

    private static readonly CompSig RaycastFilterSig = new("E8 ?? ?? ?? ?? 84 C0 75 ?? 48 8B 0D ?? ?? ?? ?? 48 8B 41");
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool RaycastFilterDelegate(
        BGCollisionModule* module,
        RaycastHit*        hitInfo,
        Vector3*           origin,
        Vector3*           direction,
        float              maxDistance,
        int                layerMask,
        int*               flags);
    private static Hook<RaycastFilterDelegate>? RaycastFilterHook;

    protected override void Init()
    {
        var baseAddress0 = DService.SigScanner.ScanText("C6 ?? ?? ?? 00 00 00 8B FE 48 89") + 6;
        Patch0 = new(baseAddress0, [0x1]);
        Patch0.Enable();
        
        var baseAddress1 = DService.SigScanner.ScanText("48 85 C0 74 ?? C6 87 ?? ?? 00 00 00") + 11;
        Patch1 = new(baseAddress1, [0x1]);
        Patch1.Enable();
        
        var baseAddress2 = DService.SigScanner.ScanText("C6 87 83 01 00 00 00 48 83 C4 ??") + 6;
        Patch2 = new(baseAddress2, [0x1]);
        Patch2.Enable();

        RaycastFilterHook ??= RaycastFilterSig.GetHook<RaycastFilterDelegate>(RaycastFilterDetour);
        RaycastFilterHook.Enable();
    }

    private static bool RaycastFilterDetour(
        BGCollisionModule* module,
        RaycastHit*        hitInfo,
        Vector3*           origin,
        Vector3*           direction,
        float              maxDistance,
        int                layerMask,
        int*               flags)
    {
        if (!DService.Condition[ConditionFlag.UsingHousingFunctions])
            return RaycastFilterHook.Original(module, hitInfo, origin, direction, maxDistance, layerMask, flags);

        return false;
    }

    protected override void Uninit()
    {
        Patch0?.Disable();
        Patch1?.Disable();
        Patch2?.Disable();
    }
}
