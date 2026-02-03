using DailyRoutines.Abstracts;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Lumina.Text.ReadOnly;

namespace DailyRoutines.ModulesPublic;

public class AutoGuardChatMessage : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoGuardChatMessageTitle"),
        Description = GetLoc("AutoGuardChatMessageDescription"),
        Category    = ModuleCategories.System
    };

    private static DalamudLinkPayload? Payload;
    
    protected override void Init()
    {
        Payload ??= LinkPayloadManager.Instance().Reg((_, _) => ChatManager.Instance().SendCommand($"/pdr toggle {nameof(AutoGuardChatMessage)}"), out _);
        ChatManager.Instance().RegPreExecuteCommandInner(OnPreExecuteCommandInner);
    }

    protected override void Uninit() =>
        ChatManager.Instance().Unreg(OnPreExecuteCommandInner);

    private static void OnPreExecuteCommandInner(ref bool isPrevented, ref ReadOnlySeString message)
    {
        if (message.ExtractText().StartsWith('/')) return;
        
        isPrevented = true;

        if (Throttler.Throttle("AutoGuardChatMessage-Notification", 30_000))
        {
            var builder = new SeStringBuilder();
            builder.AddText(GetLoc("AutoGuardChatMessage-Notification"))
                   .AddText($"\n   {GetLoc("Operation")}: [")
                   .Add(RawPayload.LinkTerminator)
                   .Add(Payload)
                   .AddUiForeground(32)
                   .AddText($"{GetLoc("Disable")}/{GetLoc("Enable")}")
                   .AddUiForegroundOff()
                   .Add(RawPayload.LinkTerminator)
                   .AddText("]");
            Chat(builder.Build());
        }
    }
}
