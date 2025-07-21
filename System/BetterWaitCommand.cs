using DailyRoutines.Abstracts;

namespace DailyRoutines.ModulesPublic;

public class BetterWaitCommand : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("BetterWaitCommandTitle"),
        Description = GetLoc("BetterWaitCommandDescription"),
        Category    = ModuleCategories.System,
        Author      = ["Cindy-Master"],
    };

    private static readonly MemoryPatch WaitSyntaxDecimalPatch = new(
        "F3 0F 58 05 ?? ?? ?? ?? F3 48 0F 2C C0 69 C8",
        [
            0xB8, 0x00, 0x00, 0x7A, 0x44,
            0x66, 0x0F, 0x6E, 0xC8,
            0xF3, 0x0F, 0x59, 0xC1,
            0xF3, 0x48, 0x0F, 0x2C, 0xC8,
            0x90,
            0x90, 0x90, 0x90, 0x90, 0x90
        ]
    );

    private static readonly MemoryPatch WaitCommandDecimalPatch = new(
        "F3 0F 58 0D ?? ?? ?? ?? F3 48 0F 2C C1 69 C8",
        [
            0xB8, 0x00, 0x00, 0x7A, 0x44,
            0x66, 0x0F, 0x6E, 0xC0,
            0xF3, 0x0F, 0x59, 0xC8,
            0xF3, 0x48, 0x0F, 0x2C, 0xC9,
            0x90,
            0x89, 0x4B, 0x58,
            0x90, 0x90, 0x90, 0x90, 0x90, 0x90,
            0xEB // 0x1F
        ]
    );

    protected override void Init()
    {
        WaitSyntaxDecimalPatch.Enable();
        WaitCommandDecimalPatch.Enable();
    }

    protected override void Uninit()
    {
        WaitSyntaxDecimalPatch.Disable();
        WaitCommandDecimalPatch.Disable();
    }
}
