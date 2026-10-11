using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using OmenTools.Interop.Game;

namespace DailyRoutines.ModulesPublic;

public class DisableGroundActionAutoFace : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("DisableGroundActionAutoFaceTitle"),
        Description = Lang.Get("DisableGroundActionAutoFaceDescription"),
        Category    = ModuleCategory.Action
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private MemoryPatch groundActionAutoFacePatch = null!;

    protected override void Init()
    {
        groundActionAutoFacePatch = new
        (
            "74 ?? 48 8D 8E ?? ?? ?? ?? E8 ?? ?? ?? ?? 84 C0 75 ?? 48 8B 55",
            [0xEB]
        );
        groundActionAutoFacePatch.Enable();
    }
}
