using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using AchievementInfo = OmenTools.OmenService.AchievementInfo;
using ContentsFinder = FFXIVClientStructs.FFXIV.Client.Game.UI.ContentsFinder;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoNotifyMentorRouletteProgress : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoNotifyMentorRouletteProgressTitle"),
        Description = Lang.Get("AutoNotifyMentorRouletteProgressDescription"),
        Category    = ModuleCategory.Notice,
        PreviewImageURL =
        [
            "https://gh.atmoomen.top/raw.githubusercontent.com/AtmoOmen/StaticAssets/main/DailyRoutines/image/AutoNotifyMentorRouletteProgress-UI.png"
        ]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private DalamudLinkPayload? achievementLinkPayload;

    protected override void Init()
    {
        TaskHelper ??= new();

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
    }

    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;

        if (achievementLinkPayload != null)
            LinkPayloadManager.Instance().Unreg(achievementLinkPayload.CommandId);
    }

    private void OnZoneChanged(uint u)
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

                if (achievementLinkPayload != null)
                    LinkPayloadManager.Instance().Unreg(achievementLinkPayload.CommandId);


                achievementLinkPayload = LinkPayloadManager.Instance().Reg((_, _) => AgentAchievement.Instance()->OpenById(firstIncomplete.ID), out _);
                var builder = new SeStringBuilder();
                builder.AddText(Lang.Get("AutoNotifyMentorRouletteProgres-Notification-Title"))
                       .Add(NewLinePayload.Payload)
                       .AddText($"   {Lang.Get("AutoNotifyMentorRouletteProgres-Notification-CurrentProgress")}: {firstIncomplete.Current} / {firstIncomplete.Max}")
                       .Add(NewLinePayload.Payload)
                       .AddText($"   {Lang.Get("AutoNotifyMentorRouletteProgres-Notification-TargetAchievement")}: ")
                       .Add(RawPayload.LinkTerminator)
                       .Add(achievementLinkPayload)
                       .AddRange(SeString.TextArrowPayloads)
                       .AddText(firstIncomplete.Name)
                       .Add(RawPayload.LinkTerminator);

                if (firstIncomplete.GetData().Title is { RowId: > 0 } titleRowRef)
                {
                    builder.Add(NewLinePayload.Payload)
                           .AddText
                           (
                               $"   {Lang.Get("AutoNotifyMentorRouletteProgres-Notification-AchievementReward")}:"          +
                               $" {(LocalPlayerState.Sex == 0 ? titleRowRef.Value.Masculine : titleRowRef.Value.Feminine)}" +
                               $" [{LuminaWrapper.GetAddonText(14119)}]"
                           );
                }
                else if (firstIncomplete.GetData().Item is { RowId: > 0 } itemRowRef)
                {
                    builder.Add(NewLinePayload.Payload)
                           .AddText($"   {Lang.Get("AutoNotifyMentorRouletteProgres-Notification-AchievementReward")}: ")
                           .Append(SeString.CreateItemLink(itemRowRef.Value, false));
                }

                builder.Add(NewLinePayload.Payload)
                       .AddText($"   {Lang.Get("AutoNotifyMentorRouletteProgres-Notification-CurrentDuty")}: ")
                       .Append
                       (
                           DService.Instance().SeStringEvaluator.EvaluateFromAddon
                           (
                               12599,
                               [
                                   (uint)GameState.ContentFinderConditionData.ClassJobLevelRequired,
                                   GameState.ContentFinderConditionData.Name
                               ]
                           ).ToDalamudString()
                       );

                // TODO: 改成 ReadOnlyString
                NotifyHelper.Instance().Chat(builder.Build().Encode());
                return true;
            }
        );
    }
    
    #region 常量

    private const byte MENTOR_ROULETTE_ID = 9;

    private static readonly FrozenSet<uint> MentorRouletteAchievements = [1472, 1473, 1474, 1475, 1603, 1604];

    #endregion
}
