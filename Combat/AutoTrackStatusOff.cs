using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using Status = Lumina.Excel.Sheets.Status;

namespace DailyRoutines.ModulesPublic;

public class AutoTrackStatusOff : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoTrackStatusOffTitle"),
        Description = Lang.Get("AutoTrackStatusOffDescription"),
        Category    = ModuleCategory.Combat,
        Author      = ["Fragile"]
    };

    private Config config = null!;
    
    private readonly StatusSelectCombo? statusSelectCombo = new
        ("Status", LuminaGetter.Get<Status>().Where(x => x.CanStatusOff && !string.IsNullOrEmpty(x.Name.ToString())));

    private readonly Dictionary<uint, (float Duration, ulong SourceID, DateTime GainTime, uint TargetID)> records = [];
    
    protected override void Init()
    {
        config = Config.Load(this) ?? new();
        if (config.StatusToMonitor.Count > 0)
            statusSelectCombo.SelectedIDs = config.StatusToMonitor.ToHashSet();

        CharacterStatusManager.Instance().RegGain(OnGainStatus);
        CharacterStatusManager.Instance().RegLose(OnLoseStatus);
    }
    
    protected override void Uninit()
    {
        CharacterStatusManager.Instance().Unreg(OnGainStatus);
        CharacterStatusManager.Instance().Unreg(OnLoseStatus);

        records.Clear();
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("SendChat"), ref config.SendChat))
            config.Save(this);

        ImGui.NewLine();

        if (ImGui.Checkbox(Lang.Get("AutoTrackStatusOff-OnlyTrackSpecific"), ref config.OnlyTrackSpecific))
        {
            config.Save(this);
            records.Clear();
        }

        if (config.OnlyTrackSpecific)
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.FileImport, Lang.Get("Import")))
            {
                var imported = ImportFromClipboard<HashSet<uint>>();

                if (imported != null)
                {
                    this.config.StatusToMonitor.AddRange(imported);
                    this.config.Save(this);
                }
            }

            ImGui.SameLine();

            using (ImRaii.Disabled(config.StatusToMonitor.Count > 0))
            {
                if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.FileExport, Lang.Get("Export")))
                {
                    ExportToClipboard(config.StatusToMonitor);
                    NotifyHelper.Instance().NotificationSuccess($"{Lang.Get("CopiedToClipboard")}");
                }
            }

            ImGui.Spacing();

            if (statusSelectCombo.DrawCheckbox())
            {
                config.StatusToMonitor = statusSelectCombo.SelectedItems.Select(x => x.RowId).ToHashSet();
                config.Save(this);
            }
        }
    }

    private void OnGainStatus(IBattleChara player, ushort statusID, ushort param, ushort stackCount, TimeSpan remainingTime, ulong sourceID)
    {
        if (remainingTime.TotalSeconds <= 0) return;
        if (config.OnlyTrackSpecific && !config.StatusToMonitor.Contains(statusID)) return;
        if (!LuminaGetter.TryGetRow<Status>(statusID, out var status) || !status.CanStatusOff) return;

        // 不是自己给的 Status 不记录
        if (sourceID != LocalPlayerState.EntityID) return;
        records[statusID] = ((float)remainingTime.TotalSeconds, sourceID, StandardTimeManager.Instance().Now, player.EntityID);
    }

    private void OnLoseStatus(IBattleChara player, ushort statusID, ushort param, ushort stackCount, ulong sourceID)
    {
        if (config.OnlyTrackSpecific && !config.StatusToMonitor.Contains(statusID)) return;
        if (!LuminaGetter.TryGetRow<Status>(statusID, out var status) || !status.CanStatusOff) return;

        // 不是自己给的 Status 不判断
        if (sourceID != LocalPlayerState.EntityID) return;

        if (records.TryGetValue(statusID, out var buffInfo))
        {
            var expectedDuration = buffInfo.Duration;
            var actualDuration   = (StandardTimeManager.Instance().Now - buffInfo.GainTime).TotalSeconds;

            // 死了当然全没了啊
            if (actualDuration < expectedDuration * TIME_THRESHOLD && !player.IsDead)
            {
                if (config.SendChat)
                {
                    NotifyHelper.Instance().Chat
                    (
                        Lang.GetSe
                        (
                            "AutoTrackStatusOff-Notification",
                            LuminaWrapper.GetStatusName(statusID),
                            statusID,
                            $"{expectedDuration:F1}",
                            $"{actualDuration:F1}",
                            new PlayerPayload(player.Name.ToString(), player.HomeWorld.RowId),
                            player.ClassJob.Value.ToBitmapFontIcon(),
                            player.ClassJob.Value.Name.ToString()
                        )
                    );
                }
            }

            records.Remove(statusID);
        }
    }

    private class Config : ModuleConfig
    {
        public bool OnlyTrackSpecific;
        public bool SendChat = true;

        public HashSet<uint> StatusToMonitor = [];
    }
    
    #region 常量
    
    private const float TIME_THRESHOLD = 0.2f;

    #endregion
}
