using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using System.Collections.Generic;
using System.Linq;
using System;
using Lumina.Excel.Sheets;

namespace DailyRoutines.Modules;

public unsafe class AutoGysahlGreens : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoGysahlGreensTitle"),
        Description = GetLoc("AutoGysahlGreensDescription"),
        Category    = ModuleCategories.General,
        Author      = ["Veever"]
    };

    private const  uint     GysahlGreens  = 4868;

    private static Config   ModuleConfig  = null!;

    private static readonly HashSet<ushort> ValidTerritory;

    private static bool HasNotifiedInCurrentZone;

    static AutoGysahlGreens()
    {
        ValidTerritory = PresetSheet.Zones
                                    .Where(x =>
                                               x.Value.TerritoryIntendedUse.RowId == 1 &&
                                               x.Key                              != 250)
                                    .Select(x => (ushort)x.Key)
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
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("AutoGysahlGreens-ChangeStanceInfo")}:");

        bool isFirst = true;
        foreach (var checkPoint in Enum.GetValues<StanceCheckpoint>())
        {
            if (!isFirst)
                ImGui.SameLine();
            isFirst = false;

            if (!LuminaGetter.TryGetRow<BuddyAction>((uint)checkPoint, out var buddyAction)) continue;
            string label      = buddyAction.Name.ExtractText();
            bool   isSelected = ModuleConfig.CommandCheck == (int)checkPoint;
            
            if (ImGui.Checkbox(label, ref isSelected))
            {
                if (isSelected)
                {
                    ModuleConfig.CommandCheck = (int)checkPoint;
                    SaveConfig(ModuleConfig);
                }
            }
        }

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
            FrameworkManager.Register(OnUpdate, throttleMS: 5_000);
    }

    private static void OnUpdate(IFramework framework)
    {
        if (PlayerState.Instance()->IsPlayerStateFlagSet(PlayerStateFlag.IsBuddyInStable)) return;
        if (DService.ClientState.LocalPlayer is not { IsDead: false }) return;
        if (BetweenAreas || OccupiedInEvent || IsOnMount || !IsScreenReady()) return;

        var classJobData = DService.ClientState.LocalPlayer.ClassJob.ValueNullable;
        if (classJobData == null) return;
        if (!ModuleConfig.NotBattleJobUsingGysahl && (classJobData?.DohDolJobIndex ?? 0) != -1) return;

        if (UIState.Instance()->Buddy.CompanionInfo.ActiveCommand != ModuleConfig.CommandCheck)
        {
            SwitchCommand((StanceCheckpoint)ModuleConfig.CommandCheck);
        }

        if (UIState.Instance()->Buddy.CompanionInfo.TimeLeft > 300) return;
        if (InventoryManager.Instance()->GetInventoryItemCount(GysahlGreens) <= 3)
        {
            if (!HasNotifiedInCurrentZone)
            {
                HasNotifiedInCurrentZone = true;
                var notificationMessage = GetLoc("AutoGysahlGreens-NotificationMessage");
                if (ModuleConfig.SendChat) 
                    Chat(notificationMessage);
                if (ModuleConfig.SendNotification) 
                    NotificationInfo(notificationMessage);
                if (ModuleConfig.SendTTS) 
                    Speak(notificationMessage);
            }

            return;
        }
        UseActionManager.UseActionLocation(ActionType.Item, GysahlGreens, 0xE0000000, default, 0xFFFF);
    }

    private static void SwitchCommand(StanceCheckpoint command)
    {
        UseActionManager.UseAction(ActionType.BuddyAction, (uint)command);
    }

    public override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        OnZoneChanged(0);
    }

    private enum StanceCheckpoint
    {
        FreeStance     = 0x04,
        DefenderStance = 0x05,
        AttackerStance = 0x06,
        HealerStance   = 0x07
    }

    private class Config : ModuleConfiguration
    {
        public bool SendChat;
        public bool SendNotification = true;
        public bool SendTTS;
        public bool NotBattleJobUsingGysahl;
        public int  CommandCheck     = 0x04;
    }
}
