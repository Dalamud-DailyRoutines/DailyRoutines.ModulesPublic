using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoBlockShutdownFromLobbyError : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoBlockShutdownFromLobbyErrorTitle"),
        Description = GetLoc("AutoBlockShutdownFromLobbyErrorDescription"),
        Category    = ModuleCategories.System
    };

    private static readonly CompSig                                  AtkMessageBoxReceiveEventSig = new("40 53 48 83 EC 30 48 8B D9 49 8B C8 E8 ?? ?? ?? ?? 8B D0");
    private delegate        bool                                     AtkMessageBoxReceiveEventDelegate(AtkMessageBoxManager* manager, nint a2, AtkValue* values);
    private static          Hook<AtkMessageBoxReceiveEventDelegate>? AtkMessageBoxReceiveEventHook;
    
    public override void Init()
    {
        AtkMessageBoxReceiveEventHook ??= AtkMessageBoxReceiveEventSig.GetHook<AtkMessageBoxReceiveEventDelegate>(AtkMessageBoxReceiveEventDetour);
        AtkMessageBoxReceiveEventHook.Enable();
    }

    private static bool AtkMessageBoxReceiveEventDetour(AtkMessageBoxManager* manager, nint a2, AtkValue* values)
    {
        values->UInt = 16000;
        return AtkMessageBoxReceiveEventHook.Original(manager, a2, values);
    }
}
