using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Config;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Dalamud;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;

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

    private readonly MemoryPatch loginFallbackPatch = new
    (
        "48 81 BE ?? ?? ?? ?? ?? ?? ?? ?? 76",
        [
            0x48, 0x81, 0xBE, 0x50, 0x12, 0x00, 0x00, // CMP [rsi+1250h], imm32
            0xE8, 0x03, 0x00, 0x00,                   // imm32 = 0x3E7 (1000ms)
            0x76, 0x16                                // JBE rel8
        ]
    );

    private Config config = null!;

    private uint     originalSystemSoundValue;
    private bool     isSystemSoundMuted;
    private DateTime systemSoundMuteUntil = DateTime.MinValue;
    private byte     lobbyUpdateStage;
    private bool     isSelectOkFilterRestored;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();
        TryRestorePendingSystemSound();

        AgentLobbyUpdateHook = AgentLobby.Instance()->VirtualTable->HookVFuncFromName("Update", (AgentLobby.Delegates.Update)AgentLobbyUpdateDetour);
        AgentLobbyUpdateHook.Enable();
        
        Timer0Hook = Timer0Sig.GetHook<TimerDelegate>(Timer0Detour);
        Timer1Hook = Timer1Sig.GetHook<TimerDelegate>(Timer1Detour);
        
        Timer0Hook.Enable();
        Timer1Hook.Enable();
        
        loginFallbackPatch.Enable();

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreDraw, "SelectOk",    OnSelectOk);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreDraw, "SelectYesno", OnSelectYesno);
    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnSelectOk);
        DService.Instance().AddonLifecycle.UnregisterListener(OnSelectYesno);
        RestoreSystemSound();
        isSelectOkFilterRestored = false;
    }

    private void AgentLobbyUpdateDetour(AgentLobby* agent, uint deltaTime)
    {
        agent->TemporaryLocked = false;
        AgentLobbyUpdateHook.Original(agent, deltaTime);
        lobbyUpdateStage = agent->LobbyUpdateStage;
        if (lobbyUpdateStage != LOGIN_QUEUE_LOBBY_UPDATE_STAGE)
            isSelectOkFilterRestored = false;
        agent->TemporaryLocked = false;
    }
    
    private byte Timer0Detour(void* timer, int intervalSecond, int retryCount) =>
        Timer0Hook.Original(timer, 1, retryCount);

    private byte Timer1Detour(void* timer, int intervalSecond, int retryCount) =>
        Timer1Hook.Original(timer, 1, retryCount);

    private void OnSelectOk(AddonEvent _, AddonArgs args)
    {
        if (lobbyUpdateStage != LOGIN_QUEUE_LOBBY_UPDATE_STAGE) return;

        var addon = (AtkUnitBase*)args.Addon.Address;
        if (!isSelectOkFilterRestored)
            addon->EnableFilter = false;

        ExtendSystemSoundMute();
    }

    private void OnSelectYesno(AddonEvent _, AddonArgs args)
    {
        if (lobbyUpdateStage != LOGIN_QUEUE_LOBBY_UPDATE_STAGE) return;

        // 手动取消排队会经过 SelectYesno，需要在这里恢复 SelectOk 的 EnableFilter，否则游戏会跳过遮罩清理。
        var selectOk = SelectOK;
        if (selectOk == null) return;

        selectOk->EnableFilter = true;
        isSelectOkFilterRestored = true;
    }

    private void TryRestorePendingSystemSound()
    {
        if (!config.HasPendingSystemSoundRestore) return;

        try
        {
            if (!DService.Instance().GameConfig.TryGet(SystemConfigOption.SoundSystem, out uint currentValue))
                return;

            if (currentValue == 0)
                DService.Instance().GameConfig.Set(SystemConfigOption.SoundSystem, config.OriginalSystemSoundValue);

            ClearPendingSystemSoundRestore();
        }
        catch (Exception ex)
        {
            DLog.Warning("恢复上次异常退出前的系统音失败", ex);
        }
    }

    private void ExtendSystemSoundMute()
    {
        systemSoundMuteUntil = StandardTimeManager.Instance().Now + SystemSoundMuteDuration;

        if (isSystemSoundMuted) return;

        try
        {
            if (!DService.Instance().GameConfig.TryGet(SystemConfigOption.SoundSystem, out originalSystemSoundValue))
                return;

            if (!TrySavePendingSystemSoundRestore(originalSystemSoundValue))
                return;

            isSystemSoundMuted = true;
            DService.Instance().GameConfig.Set(SystemConfigOption.SoundSystem, 0u);
            FrameworkManager.Instance().Reg(OnUpdate, SYSTEM_SOUND_RESTORE_CHECK_INTERVAL_MS);
        }
        catch (Exception ex)
        {
            if (isSystemSoundMuted)
                RestoreSystemSound();

            DLog.Warning("临时关闭系统音失败", ex);
        }
    }

    private void OnUpdate(IFramework _)
    {
        if (isSystemSoundMuted && StandardTimeManager.Instance().Now >= systemSoundMuteUntil)
            RestoreSystemSound();
    }

    private void RestoreSystemSound()
    {
        if (!isSystemSoundMuted) return;

        var isRestored = false;
        try
        {
            DService.Instance().GameConfig.Set(SystemConfigOption.SoundSystem, originalSystemSoundValue);
            isRestored = true;
        }
        catch (Exception ex)
        {
            DLog.Warning("恢复系统音失败", ex);
        }
        finally
        {
            isSystemSoundMuted   = false;
            systemSoundMuteUntil = DateTime.MinValue;

            FrameworkManager.Instance().Unreg(OnUpdate);

            if (isRestored)
                ClearPendingSystemSoundRestore();
        }
    }

    private bool TrySavePendingSystemSoundRestore(uint originalValue)
    {
        config.HasPendingSystemSoundRestore = true;
        config.OriginalSystemSoundValue     = originalValue;

        try
        {
            config.Save(this);
            return true;
        }
        catch (Exception ex)
        {
            config.HasPendingSystemSoundRestore = false;
            config.OriginalSystemSoundValue     = 0;

            DLog.Warning("保存系统音恢复状态失败", ex);
            return false;
        }
    }

    private void ClearPendingSystemSoundRestore()
    {
        if (!config.HasPendingSystemSoundRestore) return;

        config.HasPendingSystemSoundRestore = false;
        config.OriginalSystemSoundValue     = 0;

        try
        {
            config.Save(this);
        }
        catch (Exception ex)
        {
            DLog.Warning("清理系统音恢复状态失败", ex);
        }
    }

    private class Config : ModuleConfig
    {
        public bool HasPendingSystemSoundRestore;
        public uint OriginalSystemSoundValue;
    }

    #region 常量

    private const byte LOGIN_QUEUE_LOBBY_UPDATE_STAGE         = 31;
    private const int  SYSTEM_SOUND_RESTORE_CHECK_INTERVAL_MS = 500;

    private static readonly TimeSpan SystemSoundMuteDuration = TimeSpan.FromMilliseconds(1000);

    #endregion
}
