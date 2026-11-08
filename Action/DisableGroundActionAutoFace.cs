using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using OmenTools.Interop.Game;

namespace DailyRoutines.ModulesPublic;

internal class DisableGroundActionAutoFace : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("DisableGroundActionAutoFaceTitle"),
        Description = Lang.Get("DisableGroundActionAutoFaceDescription"),
        Category    = ModuleCategory.Action
    };

    public override ModuleBehaviour Behaviour { get; } = new()
    {
        AllDefaultEnabled = true
    };

    public override string ModuleName => nameof(DisableGroundActionAutoFace);

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
