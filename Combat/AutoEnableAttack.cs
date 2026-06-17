using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using OmenTools.Interop.Game;

namespace DailyRoutines.ModulesPublic;

public class AutoEnableAttack : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoEnableAttackTitle"),
        Description = Lang.Get("AutoEnableAttackDescription"),
        Category    = ModuleCategory.Combat
    };

    private MemoryPatch autoAttackPatch = new("41 0F B6 46 ?? FF C8", [0xB8, 0x02, 0x00, 0x00, 0x00]);
    
    protected override void Init() =>
        autoAttackPatch.Enable();
}
