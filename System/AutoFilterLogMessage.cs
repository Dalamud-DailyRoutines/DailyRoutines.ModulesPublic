using System;
using System.Collections.Generic;
using DailyRoutines.Abstracts;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace DailyRoutines.ModulesPublic;

public class AutoFilterLogMessage : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoFilterLogMessageTitle"),
        Description = GetLoc("AutoFilterLogMessageDescription"),
        Category    = ModuleCategories.System
    };
    
    private static Config          ModuleConfig = null!;
    private static LogMessageCombo Combo        = null!;

    private static readonly HashSet<uint> SeenLogMessages = [];
    
    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        Combo                       ??= new("LogMessage");
        Combo.SelectedIDs =   ModuleConfig.FilteredLogMessages;

        LogMessageManager.Instance().RegPre(OnLogMessage);
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("AutoFilterLogMessage-MessageToFilter"));

        using (ImRaii.PushIndent())
        {
            if (Combo.DrawCheckbox())
            {
                ModuleConfig.FilteredLogMessages = Combo.SelectedIDs;
                ModuleConfig.Save(this);
            }
        }
        
        ImGui.NewLine();
        
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("Mode"));

        using (ImRaii.PushIndent())
        {
            foreach (var filterMode in Enum.GetValues<FilterMode>())
            {
                if (ImGui.RadioButton(GetLoc($"AutoFilterLogMessage-Mode-{filterMode}"), ModuleConfig.Mode == filterMode))
                {
                    ModuleConfig.Mode = filterMode;
                    ModuleConfig.Save(this);
                }
            }
        }
    }

    private static void OnLogMessage(ref bool isPrevented, ref uint logMessageID, ref LogMessageQueueItem item)
    {
        if (!ModuleConfig.FilteredLogMessages.Contains(logMessageID)) return;
        
        switch (ModuleConfig.Mode)
        {
            case FilterMode.Always:
                isPrevented = true;
                break;
            
            case FilterMode.PassFirst:
                if (SeenLogMessages.Add(logMessageID)) return;

                isPrevented = true;
                break;
        }
    }

    private class Config : ModuleConfiguration
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
