using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoFilterLogMessage : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoFilterLogMessageTitle"),
        Description = Lang.Get("AutoFilterLogMessageDescription"),
        Category    = ModuleCategory.System
    };
    
    private          Config          config = null!;
    private readonly LogMessageCombo combo  = new("LogMessage");

    private readonly HashSet<uint> seenLogMessages = [];

    protected override void Init()
    {
        config            = Config.Load(this) ?? new();
        combo.SelectedIDs = config.FilteredLogMessages;

        LogMessageManager.Instance().RegPre(OnLogMessage);
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("AutoFilterLogMessage-MessageToFilter"));

        using (ImRaii.PushIndent())
        {
            if (combo.DrawCheckbox())
            {
                config.FilteredLogMessages = combo.SelectedIDs;
                config.Save(this);
            }
        }

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Mode"));

        using (ImRaii.PushIndent())
        {
            foreach (var filterMode in Enum.GetValues<FilterMode>())
            {
                if (ImGui.RadioButton(Lang.Get($"AutoFilterLogMessage-Mode-{filterMode}"), config.Mode == filterMode))
                {
                    config.Mode = filterMode;
                    config.Save(this);
                }
            }
        }
    }

    private void OnLogMessage(ref bool isPrevented, ref uint logMessageID, ref LogMessageQueueItem item)
    {
        if (!config.FilteredLogMessages.Contains(logMessageID)) return;

        switch (config.Mode)
        {
            case FilterMode.Always:
                isPrevented = true;
                break;

            case FilterMode.PassFirst:
                if (seenLogMessages.Add(logMessageID)) return;

                isPrevented = true;
                break;
        }
    }

    private class Config : ModuleConfig
    {
        public HashSet<uint> FilteredLogMessages = [];
        public FilterMode    Mode                = FilterMode.PassFirst;
    }

    private enum FilterMode
    {
        Always,
        PassFirst
    }
}
