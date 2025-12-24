using System;
using System.Linq;
using DailyRoutines.Abstracts;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;

namespace DailyRoutines.ModulesPublic;

public class AutoNotifyRecruitmentEnd : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("AutoNotifyRecruitmentEndTitle"),
        Description = GetLoc("AutoNotifyRecruitmentEndDescription"),
        Category = ModuleCategories.Notice,
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static readonly string[] ValidStrings =
    [
        "招募队员结束",
        "Party recruitment ended",
        "パーティ募集の人数を満たしたため終了します。"
    ];

    protected override void Init() => 
        DService.Chat.ChatMessage += OnChatMessage;

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

    protected override void Uninit() => 
        DService.Chat.ChatMessage -= OnChatMessage;
}
