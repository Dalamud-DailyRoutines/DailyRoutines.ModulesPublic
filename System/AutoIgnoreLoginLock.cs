using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Config;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using OmenTools.Dalamud;
using OmenTools.Interop.Game;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
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

    private uint     originalSystemSoundValue;
    private bool     isSystemSoundMuted;
    private DateTime systemSoundMuteUntil = DateTime.MinValue;
    private string   loginQueueErrorText  = string.Empty;
    private bool     isFilterRestored;

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
        
        Timer0Hook = Timer0Sig.GetHook<TimerDelegate>(Timer0Detour);
        Timer1Hook = Timer1Sig.GetHook<TimerDelegate>(Timer1Detour);
        
        Timer0Hook.Enable();
        Timer1Hook.Enable();
        
        loginFallbackPatch.Enable();

        loginQueueErrorText = LoadLoginQueueErrorText();

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreDraw, "SelectOk",    OnSelectOk);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreDraw, "SelectYesno", OnSelectYesno);
    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnSelectOk);
        DService.Instance().AddonLifecycle.UnregisterListener(OnSelectYesno);
        RestoreSystemSound();
        loginQueueErrorText = string.Empty;
        isFilterRestored    = false;
    }

    private void AgentLobbyUpdateDetour(AgentLobby* agent, uint deltaTime)
    {
        agent->TemporaryLocked = false;
        AgentLobbyUpdateHook.Original(agent, deltaTime);
        agent->TemporaryLocked = false;
    }
    
    private byte Timer0Detour(void* timer, int intervalSecond, int retryCount) =>
        Timer0Hook.Original(timer, 1, retryCount);

    private byte Timer1Detour(void* timer, int intervalSecond, int retryCount) =>
        Timer1Hook.Original(timer, 1, retryCount);

    private void OnSelectOk(AddonEvent _, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon.Address;

        if (!IsLoginQueueErrorDialog(addon))
            return;

        if (!isFilterRestored)
            addon->EnableFilter = false;

        if (!Throttler.Shared.Throttle("AutoIgnoreLoginLock-OnSelectOkDraw", 250))
            return;

        isFilterRestored = false;
        ExtendSystemSoundMute();
    }

    private void OnSelectYesno(AddonEvent _, AddonArgs args)
    {
        if (!isSystemSoundMuted) return;

        // 手动取消排队会经过 SelectYesno, 需要在这里恢复 SelectOk 的 EnableFilter，否则游戏会跳过遮罩清理。
        var selectOk = DService.Instance().GameGUI.GetAddonByName("SelectOk").Address;
        if (selectOk == nint.Zero) return;

        ((AtkUnitBase*)selectOk)->EnableFilter = true;
        isFilterRestored = true;
    }

    private bool IsLoginQueueErrorDialog(AtkUnitBase* selectOk)
    {
        if (selectOk == null || CharaSelect == null) return false;

        if (!selectOk->IsAddonAndNodesReady()) return false;

        var addon      = (AddonSelectOk*)selectOk;
        var promptText = addon->PromptText;
        if (promptText == null) return false;

        return IsPromptMatched(promptText->NodeText.ToString(), loginQueueErrorText);
    }

    private void ExtendSystemSoundMute()
    {
        systemSoundMuteUntil = StandardTimeManager.Instance().Now + SystemSoundMuteDuration;

        if (isSystemSoundMuted) return;

        try
        {
            if (!DService.Instance().GameConfig.TryGet(SystemConfigOption.SoundSystem, out originalSystemSoundValue))
                return;

            DService.Instance().GameConfig.Set(SystemConfigOption.SoundSystem, 0u);
            isSystemSoundMuted = true;
            FrameworkManager.Instance().Reg(OnUpdate, 250);
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

        try
        {
            DService.Instance().GameConfig.Set(SystemConfigOption.SoundSystem, originalSystemSoundValue);
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
        }
    }

    private static string LoadLoginQueueErrorText()
    {
        try
        {
            return LuminaGetter.TryGetRow<Error>(LOGIN_QUEUE_ERROR_ROW_ID, out var row)
                       ? GetFirstNonEmptyLine(row.Unknown0.ToString())
                       : string.Empty;
        }
        catch (Exception ex)
        {
            DLog.Warning("加载登录排队错误文本失败", ex);
            return string.Empty;
        }
    }

    private static string GetFirstNonEmptyLine(string text)
    {
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
                return trimmed;
        }

        return string.Empty;
    }

    private static bool IsPromptMatched(string text, string prompt)
    {
        var normalizedText   = NormalizePromptText(text);
        var normalizedPrompt = NormalizePromptText(prompt);

        return !string.IsNullOrWhiteSpace(normalizedText)   &&
               !string.IsNullOrWhiteSpace(normalizedPrompt) &&
               normalizedText.Contains(normalizedPrompt, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePromptText(string text) =>
        text.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();

    #region 常量

    private const uint LOGIN_QUEUE_ERROR_ROW_ID = 13206;
    private static readonly TimeSpan SystemSoundMuteDuration = TimeSpan.FromSeconds(0.5);

    #endregion
}
