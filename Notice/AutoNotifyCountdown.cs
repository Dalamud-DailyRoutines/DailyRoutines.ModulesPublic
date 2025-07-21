using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public class AutoNotifyCountdown : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoNotifyCountdownTitle"),
        Description = GetLoc("AutoNotifyCountdownDescription"),
        Category    = ModuleCategories.Notice,
        Author      = ["HSS"]
    };
    
    private static readonly List<string> Countdown = LuminaGetter.GetRow<LogMessage>(5255)!.Value.Text.ToDalamudString().Payloads
                                                                 .Where(x => x.Type == PayloadType.RawText)
                                                                 .Select(text => text.ToString() ?? string.Empty).ToList();

    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        DService.Chat.ChatMessage += OnChatMessage;
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("OnlyNotifyWhenBackground"), ref ModuleConfig.OnlyNotifyWhenBackground))
            SaveConfig(ModuleConfig);
    }

    private static unsafe void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool ishandled)
    {
        if (ModuleConfig.OnlyNotifyWhenBackground && !Framework.Instance()->WindowInactive) return;
        if ((uint)type != 185) return;

        var msg = message.TextValue;
        if (Countdown.All(msg.Contains))
        {
            NotificationInfo(message.TextValue, Lang.Get("AutoNotifyCountdown-NotificationTitle"));
            Speak(message.TextValue);
        }
    }

    protected override void Uninit() => 
        DService.Chat.ChatMessage -= OnChatMessage;

    private class Config : ModuleConfiguration
    {
        public bool OnlyNotifyWhenBackground = true;
    }
}
