using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace DailyRoutines.Modules;

public class AutoNotifyBonusFate : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoNotifyBonusFateTitle"),
        Description = GetLoc("AutoNotifyBonusFateDescription"),
        Category = ModuleCategories.Notice,
        Author = ["Due"]
    };

    private static Config ModuleConfig = null!;
    
    private static List<IFate> LastFates = [];
    private static readonly HashSet<ushort> ValidTerritory;

    static AutoNotifyBonusFate()
    {
        ValidTerritory = LuminaCache.Get<TerritoryType>()
                                    .Where(x => x.TerritoryIntendedUse.RowId == 1)
                                    .Where(x => x.ExVersion.Value.RowId      >= 2)
                                    .Select(x => (ushort)x.RowId)
                                    .ToHashSet();
    }
    
    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        DService.ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(DService.ClientState.TerritoryType);
    }

    public override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("SendChat"), ref ModuleConfig.SendChat))
            SaveConfig(ModuleConfig);
        
        if (ImGui.Checkbox(GetLoc("SendNotification"), ref ModuleConfig.SendNotification))
            SaveConfig(ModuleConfig);
        
        if (ImGui.Checkbox(GetLoc("SendTTS"), ref ModuleConfig.SendTTS))
            SaveConfig(ModuleConfig);
        
        ImGui.Spacing();
        
        if (ImGui.Checkbox(GetLoc("OpenMap"), ref ModuleConfig.AutoOpenMap))
            SaveConfig(ModuleConfig);
    }

    private static void OnZoneChanged(ushort zone)
    {
        FrameworkManager.Unregister(OnUpdate);
        LastFates.Clear();

        if (ValidTerritory.Contains(zone))
            FrameworkManager.Register(false, OnUpdate);
    }

    private static unsafe void OnUpdate(IFramework _)
    {
        if (!Throttler.Throttle("AutoNotifyBonusFate-Check", 5_000)) return;
        
        var zoneID = DService.ClientState.TerritoryType;
        if (!ValidTerritory.Contains(zoneID))
        {
            FrameworkManager.Unregister(OnUpdate);
            return;
        }
        
        if (BetweenAreas || DService.ClientState.LocalPlayer == null) return;

        if (DService.Fate is not { Length: > 0 } fateTable) return;
        if (LastFates.Count != 0 && fateTable.SequenceEqual(LastFates)) return;
        var newFates = LastFates.Count == 0 ? fateTable : fateTable.Except(LastFates);
        
        var mapID  = DService.ClientState.MapId;
        if (!LuminaCache.TryGetRow<Map>(mapID, out var mapRow)) return;
        
        foreach (var fate in newFates)
        {
            if (fate == null || !fate.HasBonus) continue;

            var mapPos = WorldToMap(fate.Position.ToVector2(), mapRow);
            
            var chatMessage = GetSLoc("AutoNotifyBonusFate-Chat", fate.Name.ExtractText(), fate.Progress, SeString.CreateMapLink(zoneID, mapID, mapPos.X, mapPos.Y));
            var notificationMessage = GetLoc("AutoNotifyBonusFate-Notification", fate.Name.ExtractText(), fate.Progress);

            if (ModuleConfig.SendChat) Chat(chatMessage);
            if (ModuleConfig.SendNotification) NotificationInfo(notificationMessage);
            if (ModuleConfig.SendTTS) Speak(notificationMessage);

            if (ModuleConfig.AutoOpenMap)
            {
                var instance         = AgentMap.Instance();
                var currentZoneMapID = instance->CurrentMapId;
                instance->SelectedMapId = currentZoneMapID;

                if (!instance->IsAgentActive()) instance->Show();
                instance->SetFlagMapMarker(DService.ClientState.TerritoryType, currentZoneMapID, fate.Position);
                instance->OpenMap(currentZoneMapID, DService.ClientState.TerritoryType, fate.Name.ExtractText());
            }
            break;
        }
        
        LastFates = [.. fateTable];
    }

    public override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        FrameworkManager.Unregister(OnUpdate);
    }

    private class Config : ModuleConfiguration
    {
        public bool SendChat;
        public bool SendNotification = true;
        public bool SendTTS = true;
        public bool AutoOpenMap = true;
    }
}
