using DailyRoutines.Abstracts;

namespace DailyRoutines.ModulesPublic;

public class SameAethernetTeleport : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("SameAethernetTeleportTitle"),
        Description = GetLoc("SameAethernetTeleportDescription"),
        Category    = ModuleCategories.System
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true, AllDefaultEnabled = true };
    
    private static readonly MemoryPatch Patch0 = new("75 ?? 48 8B 49 ?? 48 8B 01 FF 50 ?? 48 8B C8 BA ?? ?? ?? ?? 48 83 C4 ?? 5E 5D", [0xEB]);
    private static readonly MemoryPatch Patch1 = new("75 ?? 48 8B 4E ?? 48 8B 01 FF 50 ?? 48 8B C8 BA ?? ?? ?? ?? E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 80 7D", [0xEB]);

    protected override void Init()
    {
        Patch0.Enable();
        Patch1.Enable();
    }

    protected override void Uninit()
    {
        Patch0.Disable();
        Patch1.Disable();
    }
}
