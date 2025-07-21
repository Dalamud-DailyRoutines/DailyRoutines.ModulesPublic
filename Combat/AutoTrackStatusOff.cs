using System;
using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using DailyRoutines.Widgets;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;
using Status = Lumina.Excel.Sheets.Status;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoTrackStatusOff : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title           = GetLoc("AutoTrackStatusOffTitle"),
        Description     = GetLoc("AutoTrackStatusOffDescription"),
        Category        = ModuleCategories.Combat,
        Author          = ["Fragile"]
    };

    private const float TimeThreshold = 0.2f;
    
    private static Config             ModuleConfig = null!;
    private static StatusSelectCombo? StatusSelectCombo;
    
    private static readonly Dictionary<uint, (float Duration, ulong SourceID, DateTime GainTime, uint TargetID)> Records = [];


    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        StatusSelectCombo ??= new("Status", LuminaGetter.Get<Status>().Where(x => x.CanStatusOff && !string.IsNullOrEmpty(x.Name.ExtractText())));

        if (ModuleConfig.StatusToMonitor.Count > 0)
            StatusSelectCombo.SelectedStatusIDs = ModuleConfig.StatusToMonitor.ToHashSet();

        PlayerStatusManager.RegGainStatus(OnGainStatus);
        PlayerStatusManager.RegLoseStatus(OnLoseStatus);
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("SendChat"), ref ModuleConfig.SendChat))
            SaveConfig(ModuleConfig);
        
        ImGui.NewLine();

        if (ImGui.Checkbox(GetLoc("AutoTrackStatusOff-OnlyTrackSpecific"), ref ModuleConfig.OnlyTrackSpecific))
        {
            SaveConfig(ModuleConfig);
            Records.Clear();
        }

        if (ModuleConfig.OnlyTrackSpecific)
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.FileImport, GetLoc("Import")))
            {
                var config = ImportFromClipboard<HashSet<uint>>();
                if (config != null)
                {
                    ModuleConfig.StatusToMonitor.AddRange(config);
                    ModuleConfig.Save(this);
                }
            }
            
            ImGui.SameLine();
            using (ImRaii.Disabled(ModuleConfig.StatusToMonitor.Count > 0))
            {
                if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.FileExport, GetLoc("Export")))
                {
                    ExportToClipboard(ModuleConfig.StatusToMonitor);
                    NotificationSuccess($"{GetLoc("CopiedToClipboard")}");
                }
            }
            
            ImGui.Spacing();

            if (StatusSelectCombo.DrawCheckbox())
            {
                ModuleConfig.StatusToMonitor = StatusSelectCombo.SelectedStatuses.Select(x => x.RowId).ToHashSet();
                ModuleConfig.Save(this);
            }
        }
    }
    
    private static void OnGainStatus(BattleChara* player, ushort statusID, ushort param, ushort stackCount, TimeSpan remainingTime, ulong sourceID)
    {
        if (player == null || remainingTime.TotalSeconds <= 0) return;
        if (ModuleConfig.OnlyTrackSpecific && !ModuleConfig.StatusToMonitor.Contains(statusID)) return;
        if (!LuminaGetter.TryGetRow<Status>(statusID, out var status) || !status.CanStatusOff) return;
        
        // 不是自己给的 Status 不记录
        if (sourceID != LocalPlayerState.EntityID) return;
        Records[statusID] = ((float)remainingTime.TotalSeconds, sourceID, DateTime.Now, player->EntityId);
    }

    private static void OnLoseStatus(BattleChara* player, ushort statusID, ushort param, ushort stackCount, ulong sourceID)
    {
        if (player == null) return;
        if (ModuleConfig.OnlyTrackSpecific && !ModuleConfig.StatusToMonitor.Contains(statusID)) return;
        if (!LuminaGetter.TryGetRow<Status>(statusID, out var status) || !status.CanStatusOff) return;
        
        // 不是自己给的 Status 不判断
        if (sourceID != LocalPlayerState.EntityID) return;
        
        if (Records.TryGetValue(statusID, out var buffInfo))
        {
            var expectedDuration = buffInfo.Duration;
            var actualDuration   = (DateTime.Now - buffInfo.GainTime).TotalSeconds;

            // 死了当然全没了啊
            if (actualDuration < expectedDuration * TimeThreshold && !player->IsDead())
            {
                var job = LuminaGetter.GetRow<ClassJob>(player->ClassJob).GetValueOrDefault();
                if (ModuleConfig.SendChat)
                    Chat(GetSLoc("AutoTrackStatusOff-Notification",
                                 LuminaWrapper.GetStatusName(statusID),                    statusID,               $"{expectedDuration:F1}", $"{actualDuration:F1}",
                                 new PlayerPayload(player->NameString, player->HomeWorld), job.ToBitmapFontIcon(), job.Name.ExtractText()));
            }

            Records.Remove(statusID);
        }
    }

    protected override void Uninit()
    {
        PlayerStatusManager.Unreg(OnGainStatus);
        PlayerStatusManager.Unreg(OnLoseStatus);

        Records.Clear();
        
        base.Uninit();
    }

    private class Config : ModuleConfiguration
    {
        public bool SendChat = true;
        
        public bool OnlyTrackSpecific;

        public HashSet<uint> StatusToMonitor = [];
    }
}
