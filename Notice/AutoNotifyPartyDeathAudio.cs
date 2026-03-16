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
    private const int CHECK_INTERVAL_MS = 100;
    private const int CONFIRM_WINDOW_MS = 500;
    private const int MAX_CONFIRM_ATTEMPTS = 5;
    private unsafe delegate void HandleActorControlPacketDelegate(uint entityID, uint category, uint arg1, uint arg2, uint arg3, uint arg4, uint arg5, uint arg6, uint arg7, uint arg8, ulong targetID, byte isRecorded);
    private static Hook<HandleActorControlPacketDelegate>? HandleActorControlPacketHook;

    private static Config ModuleConfig = null!;
    private static string AudioFilePathInput = string.Empty;
    private static AutoNotifyPartyDeathAudio? CurrentModule;

    private readonly Dictionary<uint, bool> deathStates = new();
    private readonly Dictionary<uint, PendingConfirm> pendingConfirms = new();
    private readonly HashSet<uint> currentIds = new();
    private readonly List<uint> toRemove = new();

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

        FrameworkManager.Instance().Reg(OnFrameworkUpdate, CHECK_INTERVAL_MS);
        HandleActorControlPacketHook ??= DService.Instance().Hook.HookFromAddress<HandleActorControlPacketDelegate>((nint)PacketDispatcher.MemberFunctionPointers.HandleActorControlPacket, HandleActorControlPacketDetour);
        HandleActorControlPacketHook.Enable();

        // 切图的时候清掉
        DService.Instance().ClientState.TerritoryChanged += OnTerritoryChanged;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!ModuleConfig.IsEnabled)
            return;

        CheckPartyDeaths();
    }

    /// <summary>
    /// 主逻辑
    /// </summary>
    private unsafe void CheckPartyDeaths()
    {
        var partyList = DService.Instance().PartyList;
        var localPlayer = DService.Instance().ObjectTable.LocalPlayer;
        var localPlayerID = localPlayer?.EntityID ?? 0;

        currentIds.Clear();

        if (localPlayer != null)
        {
            currentIds.Add(localPlayer.EntityID);

            var isDeadNow = localPlayer.IsDead;
            deathStates[localPlayer.EntityID] = isDeadNow;
            ProcessPendingConfirm(localPlayer.EntityID, localPlayer.Name.TextValue, isDeadNow, true);
        }

        foreach (var member in partyList)
        {
            if (member.EntityId == 0)
                continue;

            currentIds.Add(member.EntityId);
            var isSelf = member.EntityId == localPlayerID;

            var battleChara = CharacterManager.Instance()->LookupBattleCharaByEntityId(member.EntityId);
            if (battleChara == null)
                continue;

            var isDeadNow = battleChara->IsDead();

            if (!deathStates.TryGetValue(member.EntityId, out var wasDead))
            {
                deathStates[member.EntityId] = isDeadNow;
                continue;
            }

            deathStates[member.EntityId] = isDeadNow;
            ProcessPendingConfirm(member.EntityId, member.Name.TextValue, isDeadNow, isSelf);
        }
        // 不在队里的就清
        toRemove.Clear();
        foreach (var id in deathStates.Keys)
        {
            if (!currentIds.Contains(id))
                toRemove.Add(id);
        }

        foreach (var id in toRemove)
        {
            deathStates.Remove(id);
            pendingConfirms.Remove(id);
        }
    }

    private void ProcessPendingConfirm(uint entityID, string name, bool isDeadNow, bool isSelf)
    {
        if (!pendingConfirms.TryGetValue(entityID, out var pending))
            return;

        if (pending.ExpiresAt < Environment.TickCount64 || pending.Attempts >= MAX_CONFIRM_ATTEMPTS)
        {
            pendingConfirms.Remove(entityID);
            return;
        }

        pending.Attempts++;

        if (isDeadNow != pending.TargetDead)
            return;

        if (pending.TargetDead)
            PlayDeathAudio(name);

        pendingConfirms.Remove(entityID);
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
        
        // 遍历队伍列表，检查当前实体是否为队伍成员，并获取其姓名
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
            QueuePendingConfirm(entityID, name, isSelf, true);
            return;
        }

        // 检查是否为复活事件(category=2, arg1=1)，如果是则加入待确认队列(非死亡状态)
        if (category == 2 && arg1 == 1)
            QueuePendingConfirm(entityID, name, isSelf, false);
    }

    private static void QueuePendingConfirm(uint entityID, string name, bool isSelf, bool targetDead)
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
        deathStates.Clear();
        pendingConfirms.Clear();
        currentIds.Clear();
        toRemove.Clear();
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
        FrameworkManager.Instance().Unreg(OnFrameworkUpdate);
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
