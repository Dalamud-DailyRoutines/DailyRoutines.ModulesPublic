using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;

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

    private const uint GysahlGreens              = 4868;
    private const uint StabledCompanionMessageId = 4481; // LogMessage ID：无法召唤出寄养在鸟房中的搭档

    static AutoGysahlGreens()
    {
        ValidTerritory = PresetSheet.Zones
                                    .Where(x =>
                                                x.Value.TerritoryIntendedUse.RowId == 1
                                                && x.Key != 250)
                                    .Select(x => (ushort)x.Key)
                                    .ToHashSet();
    }

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        LogMessageManager.Register(OnReceiveLogMessage);

        DService.ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(DService.ClientState.TerritoryType);
    }

    private void OnReceiveLogMessage(uint logMessageID)
    {
        if (logMessageID != StabledCompanionMessageId) return;

        var stabledMessage = GetLoc("AutoGysahlGreens-StabledMessage");
        if (ModuleConfig.SendNotification) NotificationInfo(stabledMessage);
        if (ModuleConfig.SendChat) ChatError(stabledMessage);
        if (ModuleConfig.SendTTS) Speak(stabledMessage);

        // 用户寄存陆行鸟自动卸载并让用户取回陆行鸟后重新打开
        Uninit();
    }

    public override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("SendChat"), ref ModuleConfig.SendChat))
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox(GetLoc("SendNotification"), ref ModuleConfig.SendNotification))
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox(GetLoc("SendTTS"), ref ModuleConfig.SendTTS))
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox(GetLoc("AutoGysahlGreens-NotBattleJobUsingGys"), ref ModuleConfig.NotBattleJobUsingGysahl))
            SaveConfig(ModuleConfig);
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

        var classJobData = DService.ClientState.LocalPlayer.ClassJob.ValueNullable;
        if (classJobData == null) return;
        if (!ModuleConfig.NotBattleJobUsingGysahl && (classJobData?.DohDolJobIndex ?? 0) != -1) return;

        if (UIState.Instance()->Buddy.CompanionInfo.TimeLeft > 300) return;

        if (InventoryManager.Instance()->GetInventoryItemCount(GysahlGreens) <= 3)
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

        UseActionManager.UseActionLocation(ActionType.Item, GysahlGreens, 0xE0000000, default, 0xFFFF);
    }

    public override void Uninit()
    {
        LogMessageManager.Unregister(OnReceiveLogMessage);

        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        OnZoneChanged(0);
        base.Uninit();
    }

    private class Config : ModuleConfiguration
    {
        public bool SendChat;
        public bool SendNotification = true;
        public bool SendTTS;
        public bool NotBattleJobUsingGysahl;
    }
}
