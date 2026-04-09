using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using OmenTools.Interop.Game;

namespace DailyRoutines.ModulesPublic;

public class SameAethernetTeleport : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("SameAethernetTeleportTitle"),
        Description = Lang.Get("SameAethernetTeleportDescription"),
        Category    = ModuleCategory.System
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true, AllDefaultEnabled = true };
    
    private readonly MemoryPatch patch0 = new("75 ?? 48 8B 49 ?? 48 8B 01 FF 50 ?? 48 8B C8 BA ?? ?? ?? ?? 48 83 C4 ?? 5E 5D", [0xEB]);
    private readonly MemoryPatch patch1 = new("75 ?? 48 8B 4E ?? 48 8B 01 FF 50 ?? 48 8B C8 BA ?? ?? ?? ?? E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 80 7D", [0xEB]);

    protected override void Init()
    {
        patch0.Enable();
        patch1.Enable();
    }

    protected override void Uninit()
    {
        patch0.Disable();
        patch1.Disable();
    }
}
