using System;
using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;

namespace DailyRoutines.ModulesPublic;

public class AutoNotifyReadyCheck : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoNotifyReadyCheckTitle"),
        Description = GetLoc("AutoNotifyReadyCheckDescription"),
        Category    = ModuleCategories.Notice,
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static readonly HashSet<ushort> ValidTypes = [57, 313, 569];
    private static readonly string[] ValidStrings = ["发起了准备确认", "a ready check", "レディチェックを開始しました"];

    protected override void Init() => 
        DService.Chat.ChatMessage += OnChatMessage;

    private static void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool ishandled)
    {
        if (!ValidTypes.Contains((ushort)type)) return;

        var content = message.TextValue;
        if (!ValidStrings.Any(x => content.Contains(x, StringComparison.OrdinalIgnoreCase))) return;

        NotificationInfo(content);
        Speak(content);
    }

    protected override void Uninit() => 
        DService.Chat.ChatMessage -= OnChatMessage;
}
