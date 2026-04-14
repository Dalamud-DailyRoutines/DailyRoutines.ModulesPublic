using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Lumina.Text.ReadOnly;
using OmenTools.OmenService;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic;

public class AutoGuardChatMessage : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoGuardChatMessageTitle"),
        Description = Lang.Get("AutoGuardChatMessageDescription"),
        Category    = ModuleCategory.System
    };

    private DalamudLinkPayload payload = null!;

    protected override void Init()
    {
        payload = LinkPayloadManager.Instance().Reg((_, _) => ChatManager.Instance().SendCommand($"/pdr toggle {nameof(AutoGuardChatMessage)}"), out _);
        ChatManager.Instance().RegPreExecuteCommandInner(OnPreExecuteCommandInner);
    }

    protected override void Uninit() =>
        ChatManager.Instance().Unreg(OnPreExecuteCommandInner);

    private void OnPreExecuteCommandInner(ref bool isPrevented, ref ReadOnlySeString message)
    {
        if (message.ExtractText().StartsWith('/')) return;

        isPrevented = true;

        if (Throttler.Shared.Throttle("AutoGuardChatMessage-Notification", 30_000))
        {
            var builder = new SeStringBuilder();
            builder.AddText(Lang.Get("AutoGuardChatMessage-Notification"))
                   .AddText($"\n   {Lang.Get("Operation")}: [")
                   .Add(RawPayload.LinkTerminator)
                   .Add(payload)
                   .AddUiForeground(32)
                   .AddText($"{Lang.Get("Disable")}/{Lang.Get("Enable")}")
                   .AddUiForegroundOff()
                   .Add(RawPayload.LinkTerminator)
                   .AddText("]");
            NotifyHelper.Instance().Chat(builder.Build());
        }
    }
}
