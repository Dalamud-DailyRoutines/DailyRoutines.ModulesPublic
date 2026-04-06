using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Manager;
using Dalamud.Hooking;
using OmenTools.Interop.Game.Models;

namespace DailyRoutines.ModulesPublic;

public unsafe class InstantDismount : ModuleBase
{
    private static readonly CompSig                 DismountSig = new("E8 ?? ?? ?? ?? 84 C0 75 ?? 4D 85 F6 0F 84 ?? ?? ?? ?? 49 8B 06");
    private static          Hook<DismountDelegate>? DismountHook;

    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("InstantDismountTitle"),
        Description = Lang.Get("InstantDismountDescription"),
        Category    = ModuleCategory.System
    };

    protected override void Init()
    {
        DismountHook ??= DismountSig.GetHook<DismountDelegate>(DismountDetour);
        DismountHook.Enable();
    }

    private static bool DismountDetour(nint a1, Vector3* location)
    {
        MovementManager.Instance().Dismount();
        return false;
    }

    private delegate bool DismountDelegate(nint a1, Vector3* location);
}
