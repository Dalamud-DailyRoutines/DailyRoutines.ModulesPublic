using System.Collections.Frozen;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace DailyRoutines.ModulesPublic;

public partial class AutoReplyChatBot
{
    private void AppendHistory(string key, string role, string text, string name = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var displayName = name;

        if (string.IsNullOrEmpty(displayName))
        {
            if (role.Equals("user", StringComparison.OrdinalIgnoreCase))
                displayName = key;
            else if (role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
                displayName = config.Model;
            else
                displayName = role;
        }

        conversationStore!.AppendTurn(key, new ChatMessage(role, text, displayName));
    }

    private bool IsCooldownReady(string key) =>
        rateLimiter!.CanProceed(key, config.CooldownSeconds);

    private void SetCooldown(string key) =>
        rateLimiter!.MarkUsed(key);

    private static (string Name, ushort WorldID, string? WorldName) ExtractNameWorld(SeString sender)
    {
        var p = sender.Payloads?.OfType<PlayerPayload>().FirstOrDefault();

        if (p != null)
        {
            var name     = p.PlayerName;
            var worldID  = (ushort)p.World.RowId;
            var worldStr = p.World.Value.Name.ToString();
            if (!string.IsNullOrEmpty(name))
                return (name, worldID, worldStr);
        }

        var text = sender.TextValue?.Trim() ?? string.Empty;
        var idx  = text.IndexOf('@');
        var nm   = idx < 0 ? text : text[..idx].Trim();
        var wn   = idx < 0 ? null : text[(idx + 1)..].Trim();
        return (nm, 0, wn);
    }

    private static unsafe bool IsFriend(string name, ushort worldID)
    {
        if (string.IsNullOrEmpty(name)) return false;

        var proxy = InfoProxyFriendList.Instance();
        if (proxy == null) return false;

        for (var i = 0u; i < proxy->EntryCount; i++)
        {
            var entry = proxy->GetEntry(i);
            if (entry == null) continue;

            var fName  = SeString.Parse(entry->Name).TextValue;
            var fWorld = entry->HomeWorld;

            if (fWorld == worldID && fName == name)
                return true;
        }

        return false;
    }
}
