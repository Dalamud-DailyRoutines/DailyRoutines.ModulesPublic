using System;
using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoGysahlGreens : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoGysahlGreensTitle"),
        Description = GetLoc("AutoGysahlGreensDescription"),
        Category    = ModuleCategories.General,
        Author      = ["Veever"]
    };

    private const uint GysahlGreens = 4868;

    private static HashSet<ushort> ValidTerritory { get; } = PresetSheet.Zones
                                                                        .Where(x => x.Value.TerritoryIntendedUse.RowId == 1 &&
                                                                                    x.Key                              != 250)
                                                                        .Select(x => (ushort)x.Key)
                                                                        .ToHashSet();

    private static Config ModuleConfig = null!;

    private static bool HasNotifiedInCurrentZone;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        DService.ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged((ushort)GameState.TerritoryType);
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("AutoGysahlGreens-AutoSwitchStance"), ref ModuleConfig.AutoSwitchStance))
            SaveConfig(ModuleConfig);

        if (ModuleConfig.AutoSwitchStance)
        {
            using (ImRaii.PushIndent())
            {
                var isFirst = true;
                foreach (var checkPoint in Enum.GetValues<ChocoboStance>())
                {
                    if (!LuminaGetter.TryGetRow<BuddyAction>((uint)checkPoint, out var buddyAction)) continue;

                    if (!isFirst)
                        ImGui.SameLine();
                    isFirst = false;

                    if (ImGui.RadioButton(buddyAction.Name.ExtractText(), ModuleConfig.Stance == checkPoint))
                    {
                        ModuleConfig.Stance = checkPoint;
                        SaveConfig(ModuleConfig);
                    }
                }
            }
        }
        
        ImGui.NewLine();
        
        if (ImGui.Checkbox(GetLoc("AutoGysahlGreens-NotBattleJobUsingGys"), ref ModuleConfig.NotBattleJobUsingGysahl))
            SaveConfig(ModuleConfig);
        
        ImGui.NewLine();
        
        if (ImGui.Checkbox(GetLoc("SendChat"), ref ModuleConfig.SendChat))
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox(GetLoc("SendNotification"), ref ModuleConfig.SendNotification))
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox(GetLoc("SendTTS"), ref ModuleConfig.SendTTS))
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
        if (IsInDuty()) return;
        
        if (PlayerState.Instance()->IsPlayerStateFlagSet(PlayerStateFlag.IsBuddyInStable)) return;

        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null || localPlayer->IsDead()) return;
        
        if (OccupiedInEvent || IsOnMount || !IsScreenReady()) return;

        if (!LuminaGetter.TryGetRow<ClassJob>(localPlayer->ClassJob, out var classJob)) return;
        if (!ModuleConfig.NotBattleJobUsingGysahl && classJob.DohDolJobIndex != -1) return;

        var companionInfo = UIState.Instance()->Buddy.CompanionInfo;
        if (companionInfo.TimeLeft > 300)
        {
            if (ModuleConfig.AutoSwitchStance && companionInfo.ActiveCommand != (int)ModuleConfig.Stance)
                SwitchCommand(ModuleConfig.Stance);
            
            return;
        }
        
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

        UseActionManager.UseActionLocation(ActionType.Item, GysahlGreens, extraParam: 0xFFFF);
    }

    private static void SwitchCommand(ChocoboStance command) =>
        UseActionManager.UseAction(ActionType.BuddyAction, (uint)command);

    private static bool IsInDuty()
    {
        return GameState.ContentFinderCondition != 0;
    }

    protected override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        OnZoneChanged(0);
    }

    private enum ChocoboStance
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

        public bool          AutoSwitchStance;
        public ChocoboStance Stance = ChocoboStance.FreeStance;
    }
}