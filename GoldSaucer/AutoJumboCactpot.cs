using System;
using System.Collections.Generic;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;

namespace DailyRoutines.ModulesPublic;

public class AutoJumboCactpot : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoJumboCactpotTitle"),
        Description = GetLoc("AutoJumboCactpotDescription"),
        Category    = ModuleCategories.GoldSaucer,
    };

    private static readonly Dictionary<Mode, string> NumberModeLoc = new()
    {
        [Mode.Random] = GetLoc("AutoJumboCactpot-Random") ,
        [Mode.Fixed] = GetLoc("AutoJumboCactpot-Fixed"),
    };

    private static Config ModuleConfig = null!;


    protected override unsafe void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        TaskHelper ??= new() { TimeLimitMS = 5_000 };

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "LotteryWeeklyInput", OnAddon);
        if (IsAddonAndNodesReady(LotteryWeeklyInput))
            OnAddon(AddonEvent.PostSetup, null);
    }

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(100f * GlobalFontScale);
        using (var combo = ImRaii.Combo($"{GetLoc("AutoJumboCactpot-NumberMode")}", NumberModeLoc.GetValueOrDefault(ModuleConfig.NumberMode, string.Empty)))
        {
            if (combo)
            {
                foreach (var modePair in NumberModeLoc)
                {
                    if (ImGui.Selectable(modePair.Value, modePair.Key == ModuleConfig.NumberMode))
                    {
                        ModuleConfig.NumberMode = modePair.Key;
                        SaveConfig(ModuleConfig);
                    }
                }
            }
        }

        if (ModuleConfig.NumberMode == Mode.Fixed)
        {
            ImGui.SameLine();
            
            ImGui.SetNextItemWidth(100f * GlobalFontScale);
            ImGui.InputInt(GetLoc("AutoJumboCactpot-FixedNumber"), ref ModuleConfig.FixedNumber);
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                ModuleConfig.FixedNumber = Math.Clamp(ModuleConfig.FixedNumber, 0, 9999);
                SaveConfig(ModuleConfig);
            }
        }
    }

    private unsafe void OnAddon(AddonEvent type, AddonArgs args)
    {
        TaskHelper.Abort();
        
        TaskHelper.Enqueue(() =>
        {
            if (!OccupiedInEvent)
            {
                TaskHelper.Abort();
                return true;
            }
            
            if (!IsAddonAndNodesReady(LotteryWeeklyInput)) return false;

            var number = ModuleConfig.NumberMode switch
            {
                Mode.Random => Random.Shared.Next(0, 9999),
                Mode.Fixed  => Math.Clamp(ModuleConfig.FixedNumber, 0, 9999),
                _           => 0,
            };

            Callback(LotteryWeeklyInput, true, number);
            return true;
        });
        
        TaskHelper.Enqueue(() =>
        {
            ClickSelectYesnoYes();
            return false;
        });
    }

    protected override void Uninit() => 
        DService.AddonLifecycle.UnregisterListener(OnAddon);

    private class Config : ModuleConfiguration
    {
        public Mode NumberMode  = Mode.Random;
        public int  FixedNumber = 1;
    }

    private enum Mode
    {
        Random,
        Fixed,
    }
}
