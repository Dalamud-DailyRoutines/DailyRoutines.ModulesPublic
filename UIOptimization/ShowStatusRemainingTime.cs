using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;

namespace DailyRoutines.Modules;

public class ShowStatusRemainingTime : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("ShowStatusRemainingTimeTitle"),
        Description = GetLoc("ShowStatusRemainingTimeDescription"),
        Category = ModuleCategories.UIOptimization,
        Author = ["Due"]
    };

    private static readonly string[] StatusAddons = ["_StatusCustom0", "_StatusCustom2"];

    public override void Init()
    {
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, StatusAddons, OnPostUpdate);
    }

    private unsafe void OnPostUpdate(AddonEvent type, AddonArgs args)
    {
        if (DService.Condition[ConditionFlag.InCombat]) return;

        var atkStage = AtkStage.Instance();
        if (atkStage == null) return;

        var NumberArray = atkStage->GetNumberArrayData(NumberArrayType.Hud);
        var StringArray = atkStage->GetStringArrayData(StringArrayType.Hud);
        if (NumberArray == null || StringArray == null) return;

        for (var i = 0; i < 30; i++)
        {
            var key = NumberArray->IntArray[100 + i];
            if (key == -1) return;

            if (!ArrayStatusPair.TryGetValue(key, out var status)) continue;

            var time = SeString.Parse(StringArray->StringArray[7 + i]).ToString();
            if (string.IsNullOrEmpty(time) ||
               (!time.Contains('h') && !time.Contains("小时") && time.Length <= 3)) continue;

            var remainingTime = GetRemainingTime(status);
            if (string.IsNullOrEmpty(remainingTime)) continue;

            time = remainingTime;
            StringArray->SetValue(7 + i, time);
        }
    }

    private static unsafe string GetRemainingTime(uint type)
    {
        if (DService.ClientState.LocalPlayer is not { } localPlayer) return string.Empty;

        var statusManager = ((Character*)localPlayer.Address)->GetStatusManager();
        if (statusManager == null) return string.Empty;
        
        var index = statusManager->GetStatusIndex(type);
        if (index == -1) return string.Empty;

        return TimeSpan.FromSeconds(statusManager->GetRemainingTime(index))
                       .ToString(@"hhmm");
    }

    private static readonly Dictionary<int, uint> ArrayStatusPair = new()
    {   
        { 1073957830, 46 },
        { 1073757830, 46 },
        { 1073958027, 49 },
        { 1073758027, 49 },
        { 1073958337, 1080 },
        { 1073758337, 1080 }
    };

    public override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnPostUpdate);
        base.Uninit();
    }

}