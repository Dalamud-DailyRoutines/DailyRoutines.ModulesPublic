using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.Interop.Game.Models;

namespace DailyRoutines.ModulesPublic;

public unsafe class NoHideHotbars : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("NoHideHotbarsTitle"),
        Description = Lang.Get("NoHideHotbarsDescription"),
        Category    = ModuleCategory.UIOptimization
    };
    
    private static readonly CompSig                 ToggleUISig = new("48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 48 8B 01 41 0F B6 D9");
    private delegate        void                    ToggleUIDelegate(UIModule* module, UIModule.UiFlags flags, bool isEnable, bool unknown = true);
    private                 Hook<ToggleUIDelegate>? ToggleUIHook;

    private static readonly CompSig                  ToggleUI2Sig = new("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 41 0F B6 E9 41 0F B6 F0");
    private delegate        bool                     ToggleUI2Delegate(UIModule* module, UIModule.UiFlags flags, bool isEnable, bool unknown = true);
    private                 Hook<ToggleUI2Delegate>? ToggleUI2Hook;

    protected override void Init()
    {
        ToggleUIHook ??= ToggleUISig.GetHook<ToggleUIDelegate>(ToggleUIDetour);
        ToggleUIHook.Enable();

        ToggleUI2Hook ??= ToggleUI2Sig.GetHook<ToggleUI2Delegate>(ToggleUI2Detour);
        ToggleUI2Hook.Enable();
    }

    private void ToggleUIDetour(UIModule* module, UIModule.UiFlags flags, bool isEnable, bool unknown = true)
    {
        if (!isEnable) return;
        ToggleUIHook.Original(module, flags, isEnable, unknown);
    }

    private bool ToggleUI2Detour(UIModule* module, UIModule.UiFlags flags, bool isEnable, bool unknown = true)
    {
        if (!isEnable) return true;
        return ToggleUI2Hook.Original(module, flags, isEnable, unknown);
    }
}
