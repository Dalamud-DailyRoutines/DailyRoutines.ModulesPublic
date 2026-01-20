using System;
using System.Linq;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace DailyRoutines.ModulesPublic;

public partial class AutoReplyChatBot
{
    private static void AppendHistory(string key, string role, string text, string name = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var list = ModuleConfig.Histories.GetOrAdd(key, _ => []);

        var displayName = name;

        if (string.IsNullOrEmpty(displayName))
        {
            if (role.Equals("user", StringComparison.OrdinalIgnoreCase))
                displayName = key;
            else if (role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
                displayName = ModuleConfig.Model;
            else
                displayName = role;
        }

        list.Add(new ChatMessage(role, text, displayName));
        // 不再删除历史记录，全部保留
        RequestSaveConfig();
    }

    private static bool IsCooldownReady(string key) =>
        GetSession(key).IsCooldownReady(ModuleConfig.CooldownSeconds);

    private static void SetCooldown(string key) =>
        GetSession(key).SetCooldown();

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
