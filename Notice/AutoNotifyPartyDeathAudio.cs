using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Bindings.ImGui;
using Dalamud.Hooking;
using Dalamud.Game;
using Dalamud.Interface;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Network;
using OmenTools.ImGuiOm;
using OmenTools.Managers;
using OmenTools.Service;
using System.Collections.Generic;
using System;
using System.IO;
using System.Media;
using static OmenTools.Helpers.HelpersOm;
using static OmenTools.Infos.InfosOm;

namespace DailyRoutines.ModulesPublic;

public class AutoNotifyPartyDeathAudio : DailyModuleBase
{
    private const int CONFIRM_WINDOW_MS = 500;
    private const int MAX_CONFIRM_ATTEMPTS = 5;
    private const int CONFIRM_INTERVAL_MS = CONFIRM_WINDOW_MS / MAX_CONFIRM_ATTEMPTS;
    private unsafe delegate void HandleActorControlPacketDelegate(uint entityID, uint category, uint arg1, uint arg2, uint arg3, uint arg4, uint arg5, uint arg6, uint arg7, uint arg8, ulong targetID, byte isRecorded);
    private static Hook<HandleActorControlPacketDelegate>? HandleActorControlPacketHook;

    private static Config ModuleConfig = null!;
    private static string AudioFilePathInput = string.Empty;
    private static AutoNotifyPartyDeathAudio? CurrentModule;

    private readonly Dictionary<uint, PendingConfirm> pendingConfirms = new();
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoNotifyPartyDeathAudioTitle"),
        Description = GetLoc("AutoNotifyPartyDeathAudioDescription"),
        Category    = ModuleCategories.Notice,
        Author      = ["1shm4el"]
    };

    protected override unsafe void Init()
    {
        CurrentModule = this;
        ModuleConfig = LoadConfig<Config>() ?? new();
        AudioFilePathInput = ModuleConfig.AudioFilePath;

        HandleActorControlPacketHook ??= DService.Instance().Hook.HookFromAddress<HandleActorControlPacketDelegate>((nint)PacketDispatcher.MemberFunctionPointers.HandleActorControlPacket, HandleActorControlPacketDetour);
        HandleActorControlPacketHook.Enable();

        // 切图的时候清掉
        DService.Instance().ClientState.TerritoryChanged += OnTerritoryChanged;
    }

    private unsafe bool TryGetObservedState(uint entityID, out string name, out bool isDeadNow)
    {
        var localPlayer = DService.Instance().ObjectTable.LocalPlayer;
        var localPlayerID = localPlayer?.EntityID ?? 0;
        name = string.Empty;
        isDeadNow = false;

        if (entityID == localPlayerID && localPlayer != null)
        {
            name = localPlayer.Name.TextValue;
            isDeadNow = localPlayer.IsDead;
            return true;
        }

        foreach (var member in DService.Instance().PartyList)
        {
            if (member.EntityId != entityID)
                continue;

            name = member.Name.TextValue;
            var battleChara = CharacterManager.Instance()->LookupBattleCharaByEntityId(entityID);
            if (battleChara == null)
                return false;

            isDeadNow = battleChara->IsDead();
            return true;
        }

        return false;
    }

    private void ProcessPendingConfirm(uint entityID)
    {
        if (!pendingConfirms.TryGetValue(entityID, out var pending))
            return;

        if (pending.ExpiresAt < Environment.TickCount64 || pending.Attempts >= MAX_CONFIRM_ATTEMPTS)
        {
            pendingConfirms.Remove(entityID);
            return;
        }

        pending.Attempts++;

        if (!TryGetObservedState(entityID, out var name, out var isDeadNow))
        {
            SchedulePendingConfirm(entityID);
            return;
        }

        if (isDeadNow != pending.TargetDead)
        {
            SchedulePendingConfirm(entityID);
            return;
        }

        if (pending.TargetDead)
            PlayDeathAudio(name);

        pendingConfirms.Remove(entityID);
    }

    private void SchedulePendingConfirm(uint entityID)
    {
        if (!pendingConfirms.ContainsKey(entityID))
            return;

        DService.Instance().Framework.RunOnTick(() => ProcessPendingConfirm(entityID), TimeSpan.FromMilliseconds(CONFIRM_INTERVAL_MS));
    }

    /// <summary>
    /// 走 Client::Network::PacketDispatcher 下的收包函数触发轮询
    /// </summary>
    private static unsafe void HandleActorControlPacketDetour(uint entityID, uint category, uint arg1, uint arg2, uint arg3, uint arg4, uint arg5, uint arg6, uint arg7, uint arg8, ulong targetID, byte isRecorded)
    {
        HandleActorControlPacketHook!.Original(entityID, category, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, targetID, isRecorded);

        var isPartyMember = false;
        var name = string.Empty;
        var localPlayerID = DService.Instance().ObjectTable.LocalPlayer?.EntityID ?? 0;
        
        foreach (var member in DService.Instance().PartyList)
        {
            if (member.EntityId != entityID)
                continue;

            isPartyMember = true;
            name = member.Name.TextValue;
            break;
        }

        var isSelf = entityID == localPlayerID;

        if (!isPartyMember && !isSelf)
            return;

        // 检查是否为死亡事件(category=2, arg1=2)，如果是则加入待确认队列(死亡状态)
        if (category == 2 && arg1 == 2)
        {
            QueuePendingConfirm(entityID, true);
            return;
        }

        // 检查是否为复活事件(category=2, arg1=1)，如果是则加入待确认队列(非死亡状态)
        if (category == 2 && arg1 == 1)
            QueuePendingConfirm(entityID, false);
    }

    private static void QueuePendingConfirm(uint entityID, bool targetDead)
    {
        if (CurrentModule is not { } module)
            return;

        if (module.pendingConfirms.TryGetValue(entityID, out var existing) && existing.TargetDead == targetDead)
            return;

        module.pendingConfirms[entityID] = new PendingConfirm
        {
            TargetDead = targetDead,
            Attempts = 0,
            ExpiresAt = Environment.TickCount64 + CONFIRM_WINDOW_MS
        };

        module.SchedulePendingConfirm(entityID);
    }

    private void PlayDeathAudio(string playerName)
    {
        // 路径检查
        if (!string.IsNullOrEmpty(ModuleConfig.AudioFilePath) && File.Exists(ModuleConfig.AudioFilePath))
        {
            try
            {
                using var player = new SoundPlayer(ModuleConfig.AudioFilePath);
                player.Play();
            }
            catch (Exception ex)
            {
                Error(GetLoc("AutoNotifyPartyDeathAudio-PlayAudioFailed", ex.Message));
            }
        }

        //横幅跟聊天框信息

        if (ModuleConfig.ShowScreenHint)
            ContentHintRed(GetLoc("AutoNotifyPartyDeathAudio-PlayerDeadHint", playerName), 50);

        if (ModuleConfig.ShowChatMessage)
            Chat(GetLoc("AutoNotifyPartyDeathAudio-PlayerDeadChat", playerName));
    }

    private void OnTerritoryChanged(ushort zone)
    {
        pendingConfirms.Clear();
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("AutoNotifyPartyDeathAudio-Enable"), ref ModuleConfig.IsEnabled))
            ModuleConfig.Save(this);

        ImGui.Separator();

        // wav 路径。
        ImGui.Text(GetLoc("AutoNotifyPartyDeathAudio-AudioPath"));
        ImGui.SetNextItemWidth(400f * GlobalFontScale);
        ImGui.InputText("###AudioFilePath", ref AudioFilePathInput, 500);

        ImGui.SameLine();
        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Save, GetLoc("AutoNotifyPartyDeathAudio-Save")))
            SaveAudioFilePath();

        // 试听
        if (!string.IsNullOrEmpty(ModuleConfig.AudioFilePath))
        {
            ImGui.SameLine();
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Play, GetLoc("AutoNotifyPartyDeathAudio-Preview")))
            {
                if (IsValidAudioFilePath(ModuleConfig.AudioFilePath, out var validationError))
                    PlayAudioFile(ModuleConfig.AudioFilePath);
                else
                    Warning(validationError);
            }
        }

        ImGui.Separator();

        if (ImGui.Checkbox(GetLoc("AutoNotifyPartyDeathAudio-ScreenHint"), ref ModuleConfig.ShowScreenHint))
            ModuleConfig.Save(this);

        if (ImGui.Checkbox(GetLoc("AutoNotifyPartyDeathAudio-ChatMessage"), ref ModuleConfig.ShowChatMessage))
            ModuleConfig.Save(this);
    }

    private void SaveAudioFilePath()
    {
        if (!TryNormalizeAudioFilePath(AudioFilePathInput, out var normalizedPath, out var validationError))
        {
            Warning(validationError);
            return;
        }

        ModuleConfig.AudioFilePath = normalizedPath;
        AudioFilePathInput = normalizedPath;
        ModuleConfig.Save(this);
    }

    private bool TryNormalizeAudioFilePath(string rawPath, out string normalizedPath, out string validationError)
    {
        normalizedPath = string.Empty;
        validationError = string.Empty;

        var candidate = rawPath.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            validationError = GetLoc("AutoNotifyPartyDeathAudio-AudioPathRequired");
            return false;
        }

        try
        {
            normalizedPath = Path.GetFullPath(candidate);
        }
        catch (Exception ex)
        {
            validationError = GetLoc("AutoNotifyPartyDeathAudio-InvalidAudioPath", ex.Message);
            return false;
        }

        if (!string.Equals(Path.GetExtension(normalizedPath), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            validationError = GetLoc("AutoNotifyPartyDeathAudio-InvalidAudioExtension");
            return false;
        }

        if (!IsValidAudioFilePath(normalizedPath, out validationError))
            return false;

        return true;
    }

    private bool IsValidAudioFilePath(string path, out string validationError)
    {
        validationError = string.Empty;

        if (!File.Exists(path))
        {
            validationError = GetLoc("AutoNotifyPartyDeathAudio-AudioNotFound");
            return false;
        }

        try
        {
            using var player = new SoundPlayer(path);
            player.Load();
            return true;
        }
        catch (Exception ex)
        {
            validationError = GetLoc("AutoNotifyPartyDeathAudio-InvalidAudioFile", ex.Message);
            return false;
        }
    }

    private void PlayAudioFile(string path)
    {
        try
        {
            using var player = new SoundPlayer(path);
            player.Play();
        }
        catch (Exception ex)
        {
            Error(GetLoc("AutoNotifyPartyDeathAudio-PlayAudioFailed", ex.Message));
        }
    }

    protected override void Uninit()
    {
        CurrentModule = null;
        HandleActorControlPacketHook?.Disable();
        DService.Instance().ClientState.TerritoryChanged -= OnTerritoryChanged;
        base.Uninit();
    }

    private class Config : ModuleConfiguration
    {
        public bool   IsEnabled       = true;
        public string AudioFilePath   = string.Empty;
        public bool   ShowScreenHint  = true;
        public bool   ShowChatMessage = true;
    }

    private sealed class PendingConfirm
    {
        public bool TargetDead;
        public int Attempts;
        public long ExpiresAt;
    }
}
