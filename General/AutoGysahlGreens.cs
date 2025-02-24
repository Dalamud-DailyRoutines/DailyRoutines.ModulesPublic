using System;
using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace DailyRoutines.Modules;

public unsafe class AutoGysahlGreens : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title       = GetLoc("AutoGysahlGreensTitle"),
        Description = GetLoc("AutoGysahlGreensDescription"),
        Category    = ModuleCategories.General,
        Author      = ["Veever"]
    };

    private static readonly HashSet<ushort> ValidTerritory;

    private static Config ModuleConfig = null!;

    private static bool HasNotifiedInCurrentZone;

    static AutoGysahlGreens()
    {
        ValidTerritory = LuminaCache.Get<TerritoryType>()
                                    .Where(x => x.TerritoryIntendedUse.RowId == 1 && x.RowId != 250)        // RowId: 250 = 狼狱停船场
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

        if (ImGui.Checkbox(GetLoc("AutoGysahlGreens-NoBattleCheckBox"), ref ModuleConfig.isNotBattleJob))
            SaveConfig(ModuleConfig);
        
        ImGuiOm.TooltipHover(GetLoc("AutoGysahlGreens-NoBattleTooltipHover"));
    }

    private static void OnZoneChanged(ushort zone)
    {
        FrameworkManager.Unregister(OnUpdate);
        HasNotifiedInCurrentZone = false;

        if (ValidTerritory.Contains(zone))
            FrameworkManager.Register(false, OnUpdate);
    }

    private static void OnUpdate(IFramework framework)
    {
        if (!Throttler.Throttle("AutoGysahlGreens-OnUpdate", 5_000)) return;
        if (DService.ClientState.LocalPlayer is not { IsDead: false }) return;
        if (BetweenAreas || OccupiedInEvent || IsOnMount || !IsScreenReady()) return;

        if (!LuminaCache.TryGetRow<ClassJob>(DService.ClientState.LocalPlayer.ClassJob.RowId, out var classJobData)) return;
        if (!ModuleConfig.isNotBattleJob  && classJobData.DohDolJobIndex != -1) return;

        if (UIState.Instance()->Buddy.CompanionInfo.TimeLeft > 300) return;
        
        if (InventoryManager.Instance()->GetInventoryItemCount(4868) <= 3)
        {
            if (!HasNotifiedInCurrentZone)
            {
                HasNotifiedInCurrentZone = true;
                
                var notificationMessage = GetLoc("AutoGysahlGreens-NotificationMessage");
                if (ModuleConfig.SendChat) Chat(notificationMessage);
                if (ModuleConfig.SendNotification) NotificationInfo(notificationMessage);
                if (ModuleConfig.SendTTS) Speak(notificationMessage);
            }

            return;
        }
        
        UseActionManager.UseActionLocation(ActionType.Item, 4868, 0xE0000000, default, 0xFFFF);
    }
    
    public override void Uninit()
    {
        OnZoneChanged(0);
    }

    private class Config : ModuleConfiguration
    {
        public bool  SendChat;
        public bool  SendNotification;
        public bool  SendTTS;
        public bool  isNotBattleJob;
    }
}
