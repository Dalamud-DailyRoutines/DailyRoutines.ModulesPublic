using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Hooking;

namespace DailyRoutines.ModulesPublic;

public unsafe class InstantDismount : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("InstantDismountTitle"),
        Description = GetLoc("InstantDismountDescription"),
        Category    = ModuleCategories.System,
    };

    private static readonly CompSig DismountSig = new("E8 ?? ?? ?? ?? 84 C0 75 ?? 4D 85 F6 0F 84 ?? ?? ?? ?? 49 8B 06");
    private delegate bool DismountDelegate(nint a1, Vector3* location);
    private static Hook<DismountDelegate>? DismountHook;

    protected override void Init()
    {
        DismountHook ??= DismountSig.GetHook<DismountDelegate>(DismountDetour);
        DismountHook.Enable();
    }

    private static bool DismountDetour(nint a1, Vector3* location)
    {
        MovementManager.Dismount();
        return false;
    }
}
