using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using OmenTools.Interop.Game;

namespace DailyRoutines.ModulesPublic;

public class BetterSoundEffectParam : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("BetterSoundEffectParamTitle"),
        Description = Lang.Get("BetterSoundEffectParamDescription"),
        Category    = ModuleCategory.System
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private MemoryPatch channelCheckPatch = new
    (
        "0F 84 ?? ?? ?? ?? 48 63 4D",
        [0x90, 0x90, 0x90, 0x90, 0x90, 0x90]
    );

    private MemoryPatch counterCheckPatch = new
    (
        "0F 86 ?? ?? ?? ?? 83 C1",
        [0x90, 0x90, 0x90, 0x90, 0x90, 0x90]
    );

    protected override void Init()
    {
        channelCheckPatch.Enable();
        counterCheckPatch.Enable();
    }
}
