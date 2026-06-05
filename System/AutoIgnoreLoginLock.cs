using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Sound;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using InteropGenerator.Runtime;
using OmenTools.Interop.Game;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoIgnoreLoginLock : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoIgnoreLoginLockTitle"),
        Description = Lang.Get("AutoIgnoreLoginLockDescription", LuminaWrapper.GetLogMessageText(430)),
        Category    = ModuleCategory.System
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private Hook<AgentLobby.Delegates.Update> AgentLobbyUpdateHook;
    
    private delegate byte TimerDelegate(void* timer, int intervalSecond, int retryCount);

    private static readonly CompSig             Timer0Sig = new("40 57 41 57 48 83 EC ?? 48 8B 05 ?? ?? ?? ?? 45 8B F8");
    private                 Hook<TimerDelegate> Timer0Hook;
    
    private static readonly CompSig             Timer1Sig = new("40 53 57 48 83 EC ?? 48 8B F9 41 8B D8");
    private                 Hook<TimerDelegate> Timer1Hook;

    private static readonly CompSig PlaySystemSoundSig = new("E8 ?? ?? ?? ?? 48 0F BE 46 ?? 41 B1");
    private delegate SoundData* PlaySystemSoundDelegate
    (
        SoundManager*       soundManager,
        CStringPointer      path,
        float               volume,
        uint                soundNumber,
        uint                fadeInDuration,
        bool                autoRelease,
        SoundVolumeCategory volumeCategory
    );
    private Hook<PlaySystemSoundDelegate> PlaySystemSoundHook;

    private readonly MemoryPatch loginFallbackPatch = new
    (
        "48 81 BE ?? ?? ?? ?? ?? ?? ?? ?? 76",
        [
            0x48, 0x81, 0xBE, 0x50, 0x12, 0x00, 0x00, // CMP [rsi+1250h], imm32
            0xE8, 0x03, 0x00, 0x00,                   // imm32 = 0x3E7 (1000ms)
            0x76, 0x16                                // JBE rel8
        ]
    );

    protected override void Init()
    {
        AgentLobbyUpdateHook = AgentLobby.Instance()->VirtualTable->HookVFuncFromName("Update", (AgentLobby.Delegates.Update)AgentLobbyUpdateDetour);
        AgentLobbyUpdateHook.Enable();
        
        Timer0Hook          = Timer0Sig.GetHook<TimerDelegate>(Timer0Detour);
        Timer1Hook          = Timer1Sig.GetHook<TimerDelegate>(Timer1Detour);
        PlaySystemSoundHook = PlaySystemSoundSig.GetHook<PlaySystemSoundDelegate>(PlaySystemSoundDetour);
        
        Timer0Hook.Enable();
        Timer1Hook.Enable();
        PlaySystemSoundHook.Enable();
        
        loginFallbackPatch.Enable();
    }

    private void AgentLobbyUpdateDetour(AgentLobby* agent, uint deltaTime)
    {
        agent->TemporaryLocked = false;
        AgentLobbyUpdateHook.Original(agent, deltaTime);
        
        if (agent->LobbyUpdateStage == LOGIN_QUEUE_LOBBY_UPDATE_STAGE)
            Throttler.Shared.Throttle("AutoIgnoreLoginLock.SystemSound", 1500, true);

        agent->TemporaryLocked = false;
    }
    
    private byte Timer0Detour(void* timer, int intervalSecond, int retryCount) =>
        Timer0Hook.Original(timer, 1, retryCount);

    private byte Timer1Detour(void* timer, int intervalSecond, int retryCount) =>
        Timer1Hook.Original(timer, 1, retryCount);

    private SoundData* PlaySystemSoundDetour
    (
        SoundManager*       soundManager,
        CStringPointer      path,
        float               volume,
        uint                soundNumber,
        uint                fadeInDuration,
        bool                autoRelease,
        SoundVolumeCategory volumeCategory
    ) =>
        !Throttler.Shared.Check("AutoIgnoreLoginLock.SystemSound")
            ? null
            : PlaySystemSoundHook.Original(soundManager, path, volume, soundNumber, fadeInDuration, autoRelease, volumeCategory);

    #region 常量

    private const byte LOGIN_QUEUE_LOBBY_UPDATE_STAGE = 31;

    #endregion
}
