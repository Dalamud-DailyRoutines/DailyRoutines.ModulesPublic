using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace DailyRoutines.Modules;

public unsafe class NoHideHotbars : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("NoHideHotbarsTitle"),
        Description = GetLoc("NoHideHotbarsDescription"),
        Category = ModuleCategories.UIOptimization,
    };

    private static readonly CompSig ToggleUISig = new("48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 48 8B 01 41 0F B6 D9 41 0F B6 F8 8B F2 FF 50 ?? 44 0F B6 CB 44 0F B6 C7 48 8B C8 8B D6 48 8B 5C 24 ?? 48 8B 74 24 ?? 48 83 C4 ?? 5F E9 ?? ?? ?? ?? CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC 48 89 6C 24");
    private delegate void ToggleUIDelegate(UIModule* module, UIModule.UiFlags flags, bool isEnable, bool unknown = true);
    private static Hook<ToggleUIDelegate>? ToggleUIHook;

    private static readonly CompSig ToggleUI2Sig = new("48 89 6C 24 ?? 48 89 74 24 ?? 48 89 7C 24 ?? 41 56 48 83 EC ?? 45 0F B6 F1 41 0F B6 E8");
    private delegate bool ToggleUI2Delegate(UIModule* module, UIModule.UiFlags flags, bool isEnable, bool unknown = true);
    private static Hook<ToggleUI2Delegate>? ToggleUI2Hook;

    protected override void Init()
    {
        ToggleUIHook ??= ToggleUISig.GetHook<ToggleUIDelegate>(ToggleUIDetour);
        ToggleUIHook.Enable();

        ToggleUI2Hook ??= ToggleUI2Sig.GetHook<ToggleUI2Delegate>(ToggleUI2Detour);
        ToggleUI2Hook.Enable();
    }

    private static void ToggleUIDetour(UIModule* module, UIModule.UiFlags flags, bool isEnable, bool unknown = true)
    {
        if (!isEnable) return;
        ToggleUIHook.Original(module, flags, isEnable, unknown);
    }

    private static bool ToggleUI2Detour(UIModule* module, UIModule.UiFlags flags, bool isEnable, bool unknown = true)
    {
        if (!isEnable) return true;
        return ToggleUI2Hook.Original(module, flags, isEnable, unknown);
    }
}
