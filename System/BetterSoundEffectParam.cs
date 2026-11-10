using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using OmenTools.Interop.Game;

namespace DailyRoutines.ModulesPublic;

internal class BetterSoundEffectParam : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("BetterSoundEffectParamTitle"),
        Description = Lang.Get("BetterSoundEffectParamDescription"),
        Category    = ModuleCategory.System
    };

    public override string ModuleName => nameof(BetterSoundEffectParam);

    public override ModuleBehaviour Behaviour { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private MemoryPatch channelCheckPatch = null!;

    private MemoryPatch counterCheckPatch = null!;

    protected override void Init()
    {
        channelCheckPatch = new
        (
            "0F 84 ?? ?? ?? ?? 48 63 4D",
            [0x90, 0x90, 0x90, 0x90, 0x90, 0x90]
        );

        counterCheckPatch = new
        (
            "0F 86 ?? ?? ?? ?? 83 C1",
            [0x90, 0x90, 0x90, 0x90, 0x90, 0x90]
        );

        channelCheckPatch.Enable();
        counterCheckPatch.Enable();
    }
}
