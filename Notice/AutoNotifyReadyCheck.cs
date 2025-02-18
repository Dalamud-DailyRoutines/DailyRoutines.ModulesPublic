using DailyRoutines.Abstracts;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DailyRoutines.Modules;

public class AutoNotifyReadyCheck : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoNotifyReadyCheckTitle"),
        Description = GetLoc("AutoNotifyReadyCheckDescription"),
        Category = ModuleCategories.Notice,
    };

    private static readonly HashSet<ushort> ValidTypes = [57, 313, 569];
    private static readonly string[] ValidStrings = ["发起了准备确认", "a ready check", "レディチェックを開始しました"];

    public override void Init()
    {
        DService.Chat.ChatMessage += OnChatMessage;
    }

    private static void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool ishandled)
    {
        if (!ValidTypes.Contains((ushort)type)) return;

        var content = message.TextValue;
        if (!ValidStrings.Any(x => content.Contains(x, StringComparison.OrdinalIgnoreCase))) return;

        NotificationInfo(content);
        Speak(content);
    }

    public override void Uninit()
    {
        DService.Chat.ChatMessage -= OnChatMessage;
    }
}
