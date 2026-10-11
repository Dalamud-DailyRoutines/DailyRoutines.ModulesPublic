using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Text.ReadOnly;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using AchievementInfo = OmenTools.OmenService.AchievementInfo;
using ContentsFinder = FFXIVClientStructs.FFXIV.Client.Game.UI.ContentsFinder;

namespace DailyRoutines.ModulesPublic.Duty;

public unsafe class AutoNotifyMentorRouletteProgress : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoNotifyMentorRouletteProgressTitle"),
        Description = Lang.Get("AutoNotifyMentorRouletteProgressDescription"),
        Category    = ModuleCategory.Duty,
        PreviewImageURL =
        [
            "https://gh.atmoomen.top/raw.githubusercontent.com/AtmoOmen/StaticAssets/main/DailyRoutines/image/AutoNotifyMentorRouletteProgress-UI.png"
        ]
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };
    
    protected override void Init()
    {
        TaskHelper ??= new();

        IClientState.Instance().TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
    }

    protected override void Uninit() =>
        IClientState.Instance().TerritoryChanged -= OnZoneChanged;

    private void OnZoneChanged
    (
        uint u
    )
    {
        if (GameState.TerritoryType == 0) return;

        foreach (var id in MentorRouletteAchievements)
            ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.RequestAchievement, id);

        if (GameState.ContentFinderCondition == 0) return;

        var contentsFinder = ContentsFinder.Instance();

        if (contentsFinder != null)
        {
            var queueInfo = contentsFinder->GetQueueInfo();
            if (queueInfo                          == null ||
                queueInfo->QueuedContentRouletteId != MENTOR_ROULETTE_ID)
                return;
        }

        TaskHelper.Abort();
        TaskHelper.Enqueue
        (() =>
            {
                if (!UIModule.IsScreenReady())
                    return false;

                AchievementInfo? firstIncomplete = null;

                foreach (var id in MentorRouletteAchievements)
                {
                    if (!AchievementManager.Instance().TryGetAchievement(id, out var info))
                        return false;

                    if (info.IsFinished) continue;

                    firstIncomplete = info;
                    break;
                }

                if (firstIncomplete == null) return true;

                using var rented  = new RentedSeStringBuilder();
                var       builder = rented.Builder;
                
                builder.Append(Lang.Get("AutoNotifyMentorRouletteProgres-Notification-Title"))
                       .AppendNewLine()
                       .Append($"{Lang.Get("AutoNotifyMentorRouletteProgres-Notification-CurrentProgress")}：{firstIncomplete.Current}/{firstIncomplete.Max}")
                       .AppendNewLine()
                       .Append($"{Lang.Get("AutoNotifyMentorRouletteProgres-Notification-TargetAchievement")}：")
                       .Append(ReadOnlySeString.CreateAchievementLink(firstIncomplete.ID));

                if (firstIncomplete.GetData().Title is { RowId: > 0 } titleRowRef)
                {
                    builder.AppendNewLine()
                           .Append
                           (
                               $"{Lang.Get("AutoNotifyMentorRouletteProgres-Notification-AchievementReward")}："          +
                               $"{(LocalPlayerState.Sex == 0 ? titleRowRef.Value.Masculine : titleRowRef.Value.Feminine)}" +
                               $"（{LuminaWrapper.GetAddonText(14119)}）" // 称号
                           );
                }
                else if (firstIncomplete.GetData().Item is { RowId: > 0 } itemRowRef)
                {
                    builder.AppendNewLine()
                           .Append($"{Lang.Get("AutoNotifyMentorRouletteProgres-Notification-AchievementReward")}：")
                           .Append(ReadOnlySeString.CreateItemLink(itemRowRef.Value.RowId, false));
                }

                builder.AppendNewLine()
                       .Append($"{Lang.Get("AutoNotifyMentorRouletteProgres-Notification-CurrentDuty")}：")
                       .Append
                       (
                           ISeStringEvaluator.Instance().EvaluateFromAddon
                           (
                               12599,
                               [
                                   (uint)GameState.ContentFinderConditionData.ClassJobLevelRequired,
                                   GameState.ContentFinderConditionData.Name
                               ]
                           )
                       );

                NotifyHelper.Instance().Chat(builder.ToReadOnlySeString(), false);
                return true;
            }
        );
    }

    #region 常量

    private const byte MENTOR_ROULETTE_ID = 9;

    private static readonly uint[] MentorRouletteAchievements = [1472, 1473, 1474, 1475, 1603, 1604];

    #endregion
}
