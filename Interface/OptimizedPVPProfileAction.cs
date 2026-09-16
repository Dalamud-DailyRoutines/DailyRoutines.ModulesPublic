using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using OmenTools.Interop.Game;

namespace DailyRoutines.ModulesPublic.Interface;

public class OptimizedPVPProfileAction : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("OptimizedPVPProfileActionTitle"),
        Description = Lang.Get("OptimizedPVPProfileActionDescription"),
        Category    = ModuleCategory.Interface,
        ModulesPair = ["BetterPVPACCommand"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private MemoryPatch dragDropPatch = null!;

    protected override void Init()
    {
        dragDropPatch = new("83 E7 ?? E8 ?? ?? ?? ?? C1 E7", [0x6A, 0x01, 0x5F]);
        dragDropPatch.Enable();
    }
}
