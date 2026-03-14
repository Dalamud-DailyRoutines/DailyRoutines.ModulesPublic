using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Bindings.ImGui;
using Dalamud.Game;
using Dalamud.Interface;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
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

    private static Config ModuleConfig = null!;
    private static string AudioFilePathInput = string.Empty;

    private readonly Dictionary<uint, bool> deathStates = new();
    private readonly HashSet<uint> currentIds = new();
    private readonly List<uint> toRemove = new();

    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoNotifyPartyDeathAudioTitle"),
        Description = GetLoc("AutoNotifyPartyDeathAudioDescription"),
        Category    = ModuleCategories.Notice,
        Author      = ["1shm4el"]
    };

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        AudioFilePathInput = ModuleConfig.AudioFilePath;

        FrameworkManager.Instance().Reg(OnFrameworkUpdate, CHECK_INTERVAL_MS);
        DService.Instance().ClientState.TerritoryChanged += OnTerritoryChanged;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!ModuleConfig.IsEnabled)
            return;

        CheckPartyDeaths();
    }

    private unsafe void CheckPartyDeaths()
    {
        var partyList     = DService.Instance().PartyList;
        var localPlayerID = DService.Instance().ObjectTable.LocalPlayer?.EntityID ?? 0;

        currentIds.Clear();

        foreach (var member in partyList)
        {
            if (member.EntityId == 0 || member.EntityId == localPlayerID)
                continue;

            currentIds.Add(member.EntityId);

            var battleChara = CharacterManager.Instance()->LookupBattleCharaByEntityId(member.EntityId);
            if (battleChara == null)
                continue;

            var isDeadNow = battleChara->IsDead();

            if (!deathStates.TryGetValue(member.EntityId, out var wasDead))
            {
                deathStates[member.EntityId] = isDeadNow;
                continue;
            }

            if (!wasDead && isDeadNow)
                PlayDeathAudio(member.Name.TextValue);

            deathStates[member.EntityId] = isDeadNow;
        }

        toRemove.Clear();
        foreach (var id in deathStates.Keys)
        {
            if (!currentIds.Contains(id))
                toRemove.Add(id);
        }

        foreach (var id in toRemove)
            deathStates.Remove(id);
    }

    private void PlayDeathAudio(string playerName)
    {
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

        if (ModuleConfig.ShowScreenHint)
            ContentHintRed(GetLoc("AutoNotifyPartyDeathAudio-PlayerDeadHint", playerName), 50);

        if (ModuleConfig.ShowChatMessage)
            Chat(GetLoc("AutoNotifyPartyDeathAudio-PlayerDeadChat", playerName));
    }

    private void OnTerritoryChanged(ushort zone)
    {
        deathStates.Clear();
        currentIds.Clear();
        toRemove.Clear();
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("AutoNotifyPartyDeathAudio-Enable"), ref ModuleConfig.IsEnabled))
            ModuleConfig.Save(this);

        ImGui.Separator();

        ImGui.Text(GetLoc("AutoNotifyPartyDeathAudio-AudioPath"));
        ImGui.SetNextItemWidth(400f * GlobalFontScale);
        ImGui.InputText("###AudioFilePath", ref AudioFilePathInput, 500);

        ImGui.SameLine();
        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Save, GetLoc("AutoNotifyPartyDeathAudio-Save")))
            SaveAudioFilePath();

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
}
