using DailyRoutines.Abstracts;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;

namespace DailyRoutines.Modules;

public class AutoBlockSystemNotice : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("AutoBlockSystemNoticeTitle"),
        Description = GetLoc("AutoBlockSystemNoticeDescription"),
        Category = ModuleCategories.System,
    };

    protected override void Init()
    {
        DService.Chat.CheckMessageHandled += OnChat;
    }

    private static void OnChat(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool ishandled)
    {
        if (type is not XivChatType.Notice) return;
        ishandled = true;
    }

    protected override void Uninit()
    {
        DService.Chat.CheckMessageHandled -= OnChat;
    }
}
