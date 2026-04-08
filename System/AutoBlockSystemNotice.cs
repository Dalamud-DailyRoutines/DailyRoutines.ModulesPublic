using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;

namespace DailyRoutines.ModulesPublic;

public class AutoBlockSystemNotice : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoBlockSystemNoticeTitle"),
        Description = Lang.Get("AutoBlockSystemNoticeDescription"),
        Category    = ModuleCategory.System
    };

    protected override void Init() =>
        DService.Instance().Chat.CheckMessageHandled += OnChat;

    private static void OnChat(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool ishandled)
    {
        if (type is not XivChatType.Notice) return;
        ishandled = true;
    }

    protected override void Uninit() =>
        DService.Instance().Chat.CheckMessageHandled -= OnChat;
}
