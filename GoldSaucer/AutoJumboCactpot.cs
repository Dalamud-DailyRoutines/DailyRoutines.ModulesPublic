using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using OmenTools.Interop.Game.AddonEvent;

namespace DailyRoutines.ModulesPublic;

public class AutoJumboCactpot : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoJumboCactpotTitle"),
        Description = Lang.Get("AutoJumboCactpotDescription"),
        Category    = ModuleCategory.GoldSaucer
    };
    
    private Config config = null!;
    
    protected override unsafe void Init()
    {
        config = Config.Load(this) ?? new();

        TaskHelper ??= new() { TimeoutMS = 5_000 };

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "LotteryWeeklyInput", OnAddon);
        if (LotteryWeeklyInput->IsAddonAndNodesReady())
            OnAddon(AddonEvent.PostSetup, null);
    }
    
    protected override void Uninit() =>
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(100f * GlobalUIScale);

        using (var combo = ImRaii.Combo($"{Lang.Get("AutoJumboCactpot-NumberMode")}", NumberModeLoc.GetValueOrDefault(config.NumberMode, string.Empty)))
        {
            if (combo)
            {
                foreach (var modePair in NumberModeLoc)
                {
                    if (ImGui.Selectable(modePair.Value, modePair.Key == config.NumberMode))
                    {
                        config.NumberMode = modePair.Key;
                        config.Save(this);
                    }
                }
            }
        }

        if (config.NumberMode == Mode.Fixed)
        {
            ImGui.SameLine();

            ImGui.SetNextItemWidth(100f * GlobalUIScale);
            ImGui.InputInt(Lang.Get("AutoJumboCactpot-FixedNumber"), ref config.FixedNumber);

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                config.FixedNumber = Math.Clamp(config.FixedNumber, 0, 9999);
                config.Save(this);
            }
        }
    }

    private unsafe void OnAddon(AddonEvent type, AddonArgs args)
    {
        TaskHelper.Abort();

        TaskHelper.Enqueue
        (() =>
            {
                if (!DService.Instance().Condition.IsOccupiedInEvent)
                {
                    TaskHelper.Abort();
                    return true;
                }

                if (!LotteryWeeklyInput->IsAddonAndNodesReady()) return false;

                var number = config.NumberMode switch
                {
                    Mode.Random => Random.Shared.Next(0, 9999),
                    Mode.Fixed  => Math.Clamp(config.FixedNumber, 0, 9999),
                    _           => 0
                };

                LotteryWeeklyInput->Callback(number);
                return true;
            }
        );

        TaskHelper.Enqueue
        (() =>
            {
                AddonSelectYesnoEvent.ClickYes();
                return false;
            }
        );
    }


    private class Config : ModuleConfig
    {
        public int  FixedNumber = 1;
        public Mode NumberMode  = Mode.Random;
    }

    private enum Mode
    {
        Random,
        Fixed
    }
    
    #region 常量

    private static readonly FrozenDictionary<Mode, string> NumberModeLoc = new Dictionary<Mode, string>()
    {
        [Mode.Random] = Lang.Get("AutoJumboCactpot-Random"),
        [Mode.Fixed]  = Lang.Get("AutoJumboCactpot-Fixed")
    }.ToFrozenDictionary();

    #endregion
}
