using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Interop.Game.Models;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoBlockShutdownFromLobbyError : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoBlockShutdownFromLobbyErrorTitle"),
        Description = Lang.Get("AutoBlockShutdownFromLobbyErrorDescription"),
        Category    = ModuleCategory.System
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private static readonly CompSig                                  AtkMessageBoxReceiveEventSig = new("40 53 48 83 EC 30 48 8B D9 49 8B C8 E8 ?? ?? ?? ?? 8B D0");
    private delegate        bool                                     AtkMessageBoxReceiveEventDelegate(AtkMessageBoxManager* manager, nint a2, AtkValue* values);
    private                 Hook<AtkMessageBoxReceiveEventDelegate>? AtkMessageBoxReceiveEventHook;

    protected override void Init()
    {
        AtkMessageBoxReceiveEventHook ??= AtkMessageBoxReceiveEventSig.GetHook<AtkMessageBoxReceiveEventDelegate>(AtkMessageBoxReceiveEventDetour);
        AtkMessageBoxReceiveEventHook.Enable();
    }

    private bool AtkMessageBoxReceiveEventDetour(AtkMessageBoxManager* manager, nint a2, AtkValue* values)
    {
        values->UInt = 16000;
        return AtkMessageBoxReceiveEventHook.Original(manager, a2, values);
    }
}
