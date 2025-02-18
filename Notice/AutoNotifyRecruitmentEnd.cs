using DailyRoutines.Abstracts;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using System;
using System.Linq;

namespace DailyRoutines.Modules;

public class AutoNotifyRecruitmentEnd : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoNotifyRecruitmentEndTitle"),
        Description = GetLoc("AutoNotifyRecruitmentEndDescription"),
        Category = ModuleCategories.Notice,
    };

    private static readonly string[] ValidStrings =
    [
        "招募队员结束",
        "Party recruitment ended",
        "パーティ募集の人数を満たしたため終了します。"
    ];

    public override void Init()
    {
        DService.Chat.ChatMessage += OnChatMessage;
    }

    private static void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool ishandled)
    {
        if (type != XivChatType.SystemMessage) return;
        if (BoundByDuty) return;

        var content = message.TextValue;
        if (!ValidStrings.Any(x => content.Contains(x, StringComparison.OrdinalIgnoreCase))) return;

        string[] parts = [];
        if (content.Contains('，'))
            parts = content.Split(["，"], StringSplitOptions.RemoveEmptyEntries);
        else if (content.Contains('.'))
            parts = content.Split(["."], StringSplitOptions.RemoveEmptyEntries);

        if (parts is { Length: > 1 })
        {
            NotificationInfo(parts[1], parts[0]);
            Speak(content);
            return;
        }

        NotificationInfo(content);
        Speak(content);
    }

    public override void Uninit()
    {
        DService.Chat.ChatMessage -= OnChatMessage;
    }
}
