using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Windows.Forms;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoChocoboRacing : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("AutoChocoboRacingTitle"),
        Description = GetLoc("AutoChocoboRacingDescription"),
        Category = ModuleCategories.GoldSaucer,
        Author = ["Bill"],
        ModulesPrerequisite = ["AutoCommenceDuty"]
    };

    private enum RaceRoute : ushort
    {
        Sagolii = 18,  // 荒野大道
        Costa = 19,    // 太阳海岸
        Tranquil = 20, // 恬静小路
        Random = 21,   // 随机赛道
    }

    private static Config ModuleConfig = null!;
    private static ContentsFinderOption ContentsFinderOption { get; set; } = ContentsFinderHelper.DefaultOption.Clone();

    private byte ChocoboLevel
    {
        get
        {
            var manager = RaceChocoboManager.Instance(); 
            return manager != null ? manager->Rank : (byte)0;
        }
    }

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        FrameworkManager.Register(OnUpdate, throttleMS: 1500);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RaceChocoboResult", OnRaceResult);
    }

    protected override void ConfigUI()
    {
        ImGui.Text(GetLoc($"AutoChocoboRacing-ChocoboLevel{ChocoboLevel}"));

        ImGui.NewLine();

        ImGui.Text(GetLoc("AutoChocoboRacing-RouteSelection"));
        var currentRoute = (RaceRoute)ModuleConfig.Route;
        using (var combo = ImRaii.Combo("##RouteSelection", LuminaWrapper.GetContentRouletteName((ushort)currentRoute)))
        {
            if (combo)
            {
                foreach (var route in Enum.GetValues<RaceRoute>())
                {
                    var isSelected = currentRoute == route;
                    if (ImGui.Selectable(LuminaWrapper.GetContentRouletteName((ushort)route), isSelected))
                    {
                        ModuleConfig.Route = (ushort)route;
                        SaveConfig(ModuleConfig);
                    }
                }
            }
        }

        if (ImGui.Checkbox(GetLoc("AutoChocoboRacing-AutoExit"), ref ModuleConfig.AutoExit))
            SaveConfig(ModuleConfig);
        if (ImGui.Checkbox(GetLoc("AutoChocoboRacing-StopAtMaxRank"), ref ModuleConfig.StopAtMaxRank))
            SaveConfig(ModuleConfig);
        if (ImGui.Checkbox(GetLoc("AutoChocoboRacing-AlwaysRun"), ref ModuleConfig.AlwaysRun))
            SaveConfig(ModuleConfig);

        ImGui.NewLine();

        if (ImGui.Button(ModuleConfig.IsEnabled ? GetLoc("Stop") : GetLoc("Start")))
        {
            ModuleConfig.IsEnabled ^= true;
            SaveConfig(ModuleConfig);
            if (!ModuleConfig.IsEnabled && DService.Condition[ConditionFlag.ChocoboRacing])
                SetMoving(false);
            if (!ModuleConfig.IsEnabled && DService.Condition[ConditionFlag.InDutyQueue])
                CancelDutyApply();
        }
    }

    private void OnRaceResult(AddonEvent type, AddonArgs args)
    {
        if (!ModuleConfig.AutoExit) return;

        var addon = RaceChocoboResult;
        if ((addon == null || !IsAddonAndNodesReady(addon)) && (addon->GetNodeById(8) == null)) return;

        SetMoving(false);
        SlowDown(false);

        Callback(addon, true, 1);
    }

    private void OnUpdate(IFramework _)
    {
        if (!ModuleConfig.IsEnabled || !IsScreenReady()) return;

        // 等级限制 (40级退休)
        if (ModuleConfig.StopAtMaxRank && ChocoboLevel >= 40)
        {
            ModuleConfig.IsEnabled = false;
            SaveConfig(ModuleConfig);
            DService.Chat.Print("AutoChocoboRacing-FinishLeveling");
            return;
        }

        // 比赛中的移动控制
        if (DService.Condition[ConditionFlag.ChocoboRacing] &&
            TryGetAddonByName("_RaceChocoboParameter", out var raceChocoboParameter))
        {
            HandleRacing(raceChocoboParameter);
            return;
        }
        
        // 进本
        if (!DService.Condition[ConditionFlag.WaitingForDuty] &&
            !DService.Condition[ConditionFlag.InDutyQueue] &&
            Throttler.Throttle("##RequestRoulette",1500))
            RequestDutyRoulette(ModuleConfig.Route, ContentsFinderOption); 
    }

    private void HandleRacing(AtkUnitBase* raceChocoboParameter)
    {
        var lathered = raceChocoboParameter->GetImageNodeById(3)->IsVisible();
        var stamina = raceChocoboParameter->GetNodeById(5)->GetAsAtkCounterNode()->NodeText.ToString();
        var hasStamina = !string.Equals(stamina, "0.00%");

        SetMoving(ModuleConfig.AlwaysRun || (!lathered && hasStamina));
        SlowDown(!ModuleConfig.AlwaysRun && lathered);
    }

    private void SetMoving(bool value)
    {
        if (value) 
            SendKeyDown(Keys.W);
        else 
            SendKeyUp(Keys.W);
    }

    private void SlowDown(bool value)
    {
        if (value)
            SendKeyDown(Keys.S);
        else
            SendKeyUp(Keys.S);
    }

    protected override void Uninit()
    {
        SetMoving(false);
        SlowDown(false);

        ModuleConfig.IsEnabled = false;
        SaveConfig(ModuleConfig);

        DService.AddonLifecycle.UnregisterListener(OnRaceResult);
        FrameworkManager.Unregister(OnUpdate);
    }

    private class Config : ModuleConfiguration
    {
        public bool IsEnabled;
        public bool AutoExit = true;
        public bool AlwaysRun = true;
        public bool StopAtMaxRank = true;

        public ushort Route = 19;
    }
}
