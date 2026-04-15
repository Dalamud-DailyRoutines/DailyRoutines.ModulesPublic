using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Common.Runtime.Hosts;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Network;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using NAudio.Wave;
using OmenTools;
using OmenTools.Extensions;
using OmenTools.ImGuiOm;
using OmenTools.OmenService;
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using NotifyHelper = OmenTools.OmenService.NotifyHelper;

namespace DailyRoutines.ModulesPublic;

public class AutoNotifyPartyDeathAudio : ModuleBase
{
    private Config config = null!;
    private static string AudioFilePathInput = string.Empty;

    private readonly HashSet<uint> deadMembers = [];
    private ActorControlWatcher? actorControlWatcher;
    private WaveOutEvent? previewOutputDevice;
    private AudioFileReader? previewAudioReader;
    public override ModuleInfo Info { get; }

    private static float GlobalFontScale => ImGuiHelpers.GlobalScale;

    private static string GetLoc(string key, params object[] args) =>
        ManagerHost.Current.GetLoc(key, args);

    private bool IsModuleEnabled => config.IsEnabled;

    private bool isPreviewPlaying;

    public AutoNotifyPartyDeathAudio()
    {
        Info = new ModuleInfo
        {
            Title = GetLoc("AutoNotifyPartyDeathAudioTitle"),
            Description = GetLoc("AutoNotifyPartyDeathAudioDescription"),
            Category = ModuleCategory.Notice,
            Author = ["1shm4el"]
        };
    }

    protected override unsafe void Init()
    {
        config = Config.Load(this) ?? new();
        AudioFilePathInput = config.AudioFilePath;

        actorControlWatcher ??= new(this);
        actorControlWatcher.Enable();

        DService.Instance().ClientState.TerritoryChanged += OnTerritoryChanged;
    }

    private unsafe bool TryGetObservedState(uint entityID, out string name, out bool isDeadNow)
    {
        name = string.Empty;
        isDeadNow = false;

        if (entityID == LocalPlayerState.EntityID && LocalPlayerState.Object is { } localPlayer)
        {
            name = localPlayer.Name.TextValue;
            isDeadNow = localPlayer.IsDead;
            return true;
        }

        var agent = AgentHUD.Instance();
        if (agent == null || agent->PartyMemberCount < 2)
            return false;

        foreach (var member in agent->PartyMembers)
        {
            if (member.EntityId != entityID || member.Object == null)
                continue;

            name = SeString.Parse(member.Name.Value).TextValue;
            isDeadNow = member.Object->IsDead() || member.Object->Health <= 0;
            return true;
        }

        return false;
    }

    private static unsafe bool TryResolveObservedTarget(uint entityID, out bool isSelf)
    {
        isSelf = entityID == LocalPlayerState.EntityID;
        if (isSelf)
            return true;

        var agent = AgentHUD.Instance();
        if (agent == null || agent->PartyMemberCount < 2)
            return false;

        foreach (var member in agent->PartyMembers)
        {
            if (member.EntityId == entityID)
                return true;
        }

        return false;
    }

    private void HandleActorControlPacket(uint entityID, uint category, uint arg1)
    {
        if (!IsModuleEnabled)
            return;

        if (!TryResolveObservedTarget(entityID, out _))
            return;

        if (category == 2 && arg1 == 2)
        {
            HandleDeathPacket(entityID);
            return;
        }

        if (category == 2 && arg1 == 1)
            deadMembers.Remove(entityID);
    }

    private void HandleDeathPacket(uint entityID)
    {
        if (!IsModuleEnabled)
            return;

        if (!TryGetObservedState(entityID, out var name, out var isDeadNow) || !isDeadNow)
            return;

        if (!deadMembers.Add(entityID))
            return;

        PlayDeathAudio(name);
    }

    private void PlayDeathAudio(string playerName)
    {
        if (!string.IsNullOrEmpty(config.AudioFilePath) &&
            File.Exists(config.AudioFilePath))
            PlayAudioFile(config.AudioFilePath);

        if (config.ShowScreenHint)
            NotifyHelper.Instance().ContentHintRed(GetLoc("AutoNotifyPartyDeathAudio-PlayerDeadHint", playerName), TimeSpan.FromSeconds(1));

        if (config.ShowChatMessage)
            NotifyHelper.Instance().Chat(GetLoc("AutoNotifyPartyDeathAudio-PlayerDeadChat", playerName));
    }

    private void OnTerritoryChanged(ushort zone)
    {
        deadMembers.Clear();
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("AutoNotifyPartyDeathAudio-Enable"), ref config.IsEnabled))
            config.Save(this);

        ImGui.Separator();

        ImGui.Text(GetLoc("AutoNotifyPartyDeathAudio-AudioPath"));
        ImGui.SetNextItemWidth(400f * GlobalFontScale);
        ImGui.InputText("###AudioFilePath", ref AudioFilePathInput, 500, ImGuiInputTextFlags.ReadOnly);

        ImGui.SameLine();
        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.FolderOpen, GetLoc("Select")))
            SelectAudioFilePath();

        if (!string.IsNullOrEmpty(config.AudioFilePath))
        {
            ImGui.SameLine();
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Play, GetLoc("AutoNotifyPartyDeathAudio-Preview")))
                PlayAudioFile(config.AudioFilePath);
        }

        ImGui.Separator();

        if (ImGui.Checkbox(GetLoc("AutoNotifyPartyDeathAudio-ScreenHint"), ref config.ShowScreenHint))
            config.Save(this);

        if (ImGui.Checkbox(GetLoc("AutoNotifyPartyDeathAudio-ChatMessage"), ref config.ShowChatMessage))
            config.Save(this);
    }

    private void SelectAudioFilePath()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Wave Audio (*.wav)|*.wav",
            CheckFileExists = true,
            Multiselect = false,
            RestoreDirectory = true,
            Title = GetLoc("AutoNotifyPartyDeathAudio-AudioPath")
        };

        if (!string.IsNullOrWhiteSpace(config.AudioFilePath) && File.Exists(config.AudioFilePath))
        {
            dialog.FileName = config.AudioFilePath;
            dialog.InitialDirectory = Path.GetDirectoryName(config.AudioFilePath);
        }

        if (dialog.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
            return;

        config.AudioFilePath = dialog.FileName;
        AudioFilePathInput = dialog.FileName;
        config.Save(this);
    }

    private void PlayAudioFile(string path)
    {
        try
        {
            if (isPreviewPlaying)
                StopPreviewPlayback();

            previewAudioReader = new AudioFileReader(path);
            previewOutputDevice = new WaveOutEvent();
            previewOutputDevice.PlaybackStopped += OnPreviewPlaybackStopped;
            previewOutputDevice.Init(previewAudioReader);
            isPreviewPlaying = true;
            previewOutputDevice.Play();
        }
        catch (Exception ex)
        {
            StopPreviewPlayback();
            NotifyHelper.Instance().NotificationError(GetLoc("AutoNotifyPartyDeathAudio-PlayAudioFailed", ex.Message));
        }
    }

    private void OnPreviewPlaybackStopped(object? sender, StoppedEventArgs e) =>
        StopPreviewPlayback();

    private void StopPreviewPlayback()
    {
        if (previewOutputDevice != null)
        {
            previewOutputDevice.PlaybackStopped -= OnPreviewPlaybackStopped;
            previewOutputDevice.Dispose();
            previewOutputDevice = null;
        }

        previewAudioReader?.Dispose();
        previewAudioReader = null;
        isPreviewPlaying = false;
    }

    protected override void Uninit()
    {
        StopPreviewPlayback();
        actorControlWatcher?.Dispose();
        actorControlWatcher = null;
        DService.Instance().ClientState.TerritoryChanged -= OnTerritoryChanged;
        base.Uninit();
    }

    private class Config : ModuleConfig
    {
        public bool IsEnabled = true;
        public string AudioFilePath = "";
        public bool ShowScreenHint = true;
        public bool ShowChatMessage = true;
    }

    private sealed class ActorControlWatcher : IDisposable
    {
        private readonly AutoNotifyPartyDeathAudio owner;
        private readonly Hook<PacketDispatcher.Delegates.HandleActorControlPacket> hook;

        public unsafe ActorControlWatcher(AutoNotifyPartyDeathAudio owner)
        {
            this.owner = owner;
            hook = DService.Instance().Hook.HookFromMemberFunction<PacketDispatcher.Delegates.HandleActorControlPacket>
            (
                typeof(PacketDispatcher.MemberFunctionPointers),
                nameof(PacketDispatcher.MemberFunctionPointers.HandleActorControlPacket),
                Detour
            );
        }

        public void Enable() => hook.Enable();

        private unsafe void Detour(uint entityID, uint category, uint arg1, uint arg2, uint arg3, uint arg4, uint arg5, uint arg6, uint arg7, uint arg8, GameObjectId targetID, bool isRecorded)
        {
            hook.Original(entityID, category, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, targetID, isRecorded);
            owner.HandleActorControlPacket(entityID, category, arg1);
        }

        public void Dispose() => hook.Dispose();
    }
}
