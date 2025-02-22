using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DailyRoutines.Modules;

public unsafe class AutoGysahlGreens : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoGysahlGreensTitle"),
        Description = GetLoc("AutoGysahlGreensDescription"),
        Category = ModuleCategories.General,
        Author = ["Veever"]
    };

    private static Config ModuleConfig = null!;
    private static readonly HashSet<ushort> ValidTerritory;
    private static DateTime lastNotificationTime = DateTime.MinValue;

    static AutoGysahlGreens()
    {
        ValidTerritory = LuminaCache.Get<TerritoryType>()
                                    .Where(x => x.TerritoryIntendedUse.RowId == 1)
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

        if (ImGui.SliderFloat(GetLoc("AutoGysahlGreens-NotificationInterval"), ref ModuleConfig.NotificationInterval, 10.0f, 300.0f, "%.2f"))
        {
            SaveConfig(ModuleConfig);
        }

        ImGui.Text($"{GetLoc("AutoGysahlGreens-NotificationIntervalText")}: {ModuleConfig.NotificationInterval} {GetLoc("AutoGysahlGreens-Second")}");
    }

    private static void OnZoneChanged(ushort zone)
    {
        FrameworkManager.Unregister(OnUpdate);

        if (ValidTerritory.Contains(zone))
            FrameworkManager.Register(false, OnUpdate);
    }

    private static void OnUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        if (!Throttler.Throttle("AutoGysahlGreens-OnUpdate", 5_000)) return;

        if (DService.ClientState.LocalPlayer is not { } localPlayer) return;

        if (!HasGysahlGreens())
        {
            if ((DateTime.Now - lastNotificationTime).TotalSeconds >= ModuleConfig.NotificationInterval)
            {
                var notificationMessage = GetLoc("AutoGysahlGreens-NotificationMessage");

                if (ModuleConfig.SendChat) Chat(notificationMessage);
                if (ModuleConfig.SendNotification) NotificationInfo(notificationMessage);
                if (ModuleConfig.SendTTS) Speak(notificationMessage);
                lastNotificationTime = DateTime.Now;
            }
            return;
        }

        if (IsChocoboSummoned()) return;

        UseActionManager.UseActionLocation(ActionType.Item, 4868, 0xE0000000, default, 0xFFFF);
    }

    private static bool IsChocoboSummoned() => UIState.Instance()->Buddy.CompanionInfo.TimeLeft > 0;

    private static bool HasGysahlGreens() => InventoryManager.Instance()->GetInventoryItemCount(4868) > 0;

    public override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        FrameworkManager.Unregister(OnUpdate);
    }

    private class Config : ModuleConfiguration
    {
        public bool SendChat;
        public bool SendNotification;
        public bool SendTTS;
        public float NotificationInterval = 5f;
    }
}
