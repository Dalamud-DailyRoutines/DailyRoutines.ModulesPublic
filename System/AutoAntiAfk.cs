using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoAntiAfk : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoAntiAfkTitle"),
        Description = GetLoc("AutoAntiAfkDescription"),
        Category    = ModuleCategories.System,
    };

    private static readonly CompSig InputTimerModuleUpdateSig = new("E8 ?? ?? ?? ?? 48 8B 8B ?? ?? ?? ?? 48 8B 01 FF 90 ?? ?? ?? ?? 84 C0");
    private delegate        void    InputTimerModuleUpdateDelegate(InputTimerModule* module, void* a2, void* a3);
    private static          Hook<InputTimerModuleUpdateDelegate> InputTimerModuleUpdateHook;

    protected override void Init()
    {
        InputTimerModuleUpdateHook ??= InputTimerModuleUpdateSig.GetHook<InputTimerModuleUpdateDelegate>(InputTimerModuleUpdateDetour);
        InputTimerModuleUpdateHook.Enable();
    }

    private static void InputTimerModuleUpdateDetour(InputTimerModule* module, void* a2, void* a3)
    {
        module->AfkTimer = module->ContentInputTimer = module->InputTimer = module->Unk1C = 0;
        InputTimerModuleUpdateHook.Original(module, a2, a3);
    }
}
