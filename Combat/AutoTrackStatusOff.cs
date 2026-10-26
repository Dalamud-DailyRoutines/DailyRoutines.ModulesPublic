using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Lumina.Text.ReadOnly;
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

    private StatusSelectCombo? statusSelectCombo;

    private readonly Dictionary<uint, (float Duration, ulong SourceID, DateTime GainTime, uint TargetID)> records = [];

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        statusSelectCombo = new
        (
            "Status",
            LuminaGetter.Get<Status>().Where(x => x.CanStatusOff && !string.IsNullOrEmpty(x.Name.ToString()))
        );
        if (config.StatusToMonitor.Count > 0)
            statusSelectCombo.SelectedIDs = [.. config.StatusToMonitor];

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
        using (ImRaii.Heading1(Lang.Get("Status")))
        {
            if (statusSelectCombo.DrawCheckbox())
            {
                config.StatusToMonitor = [.. statusSelectCombo.SelectedItems.Select(x => x.RowId)];
                config.Save(this);
            }

            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.FileImport, Lang.Get("Import")))
            {
                var imported = ImportFromClipboard<HashSet<uint>>();

                if (imported != null)
                {
                    config.StatusToMonitor.AddRange(imported);
                    config.Save(this);
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
        }
    }

    private void OnGainStatus
    (
        IBattleChara player,
        ushort       statusID,
        ushort       param,
        ushort       stackCount,
        TimeSpan     remainingTime,
        ulong        sourceID
    )
    {
        if (remainingTime.TotalSeconds <= 0)
            return;
        if (!config.StatusToMonitor.Contains(statusID))
            return;
        if (sourceID != LocalPlayerState.EntityID)
            return;
        if (!LuminaGetter.TryGetRow<Status>(statusID, out var status) ||
            !status.CanStatusOff)
            return;

        records[statusID] = ((float)remainingTime.TotalSeconds, sourceID, StandardTimeManager.Instance().Now, player.EntityID);
    }

    private void OnLoseStatus
    (
        IBattleChara player,
        ushort       statusID,
        ushort       param,
        ushort       stackCount,
        ulong        sourceID
    )
    {
        if (!config.StatusToMonitor.Contains(statusID))
            return;
        if (sourceID != LocalPlayerState.EntityID)
            return;
        if (!LuminaGetter.TryGetRow<Status>(statusID, out var status) ||
            !status.CanStatusOff)
            return;

        if (records.TryGetValue(statusID, out var buffInfo))
        {
            var expectedDuration = buffInfo.Duration;
            var actualDuration   = (StandardTimeManager.Instance().Now - buffInfo.GainTime).TotalSeconds;

            // 死了当然全没了啊
            if (actualDuration < expectedDuration * TIME_THRESHOLD &&
                !player.IsDead)
            {
                NotifyHelper.Instance().Chat
                (
                    Lang.GetSe
                    (
                        "AutoTrackStatusOff-Notification",
                        new Dictionary<string, object>
                        {
                            ["player"]            = ReadOnlySeString.CreatePlayerLink(player.Name, player.HomeWorld.RowId),
                            ["estimatedTimeText"] = $"{expectedDuration:F1}",
                            ["timeText"]          = $"{actualDuration:F1}"
                        }
                    )
                );
            }

            records.Remove(statusID);
        }
    }

    private class Config : ModuleConfig
    {
        public HashSet<uint> StatusToMonitor = [];
    }

    #region 常量

    private const float TIME_THRESHOLD = 0.2f;

    #endregion
}
