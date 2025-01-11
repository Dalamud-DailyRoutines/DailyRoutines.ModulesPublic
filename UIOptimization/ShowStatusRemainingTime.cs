using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
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

    private static unsafe void OnPostUpdate(AddonEvent type, AddonArgs args)
    {
        var localPlayer = DService.ClientState.LocalPlayer;
        if (localPlayer is null || DService.Condition[ConditionFlag.InCombat]) return;

        var atkStage = AtkStage.Instance();
        if (atkStage == null) return;

        var numberArray = atkStage->GetNumberArrayData(NumberArrayType.Hud);
        var stringArray = atkStage->GetStringArrayData(StringArrayType.Hud);
        if (numberArray == null || stringArray == null) return;

        for (var i = 0; i < 30; i++)
        {
            var key = numberArray->IntArray[100 + i];
            if (key == -1) return;

            if (!ArrayStatusPair.TryGetValue(key, out var status)) continue;

            var time = SeString.Parse(stringArray->StringArray[7 + i]).ToString();
            if (string.IsNullOrEmpty(time) ||
               (!time.Contains('h') && !time.Contains("小时") && time.Length <= 3)) continue;

            if (!GetRemainingTime(status, out time)) continue;

            stringArray->SetValue(7 + i, time);
        }

        return;

        bool GetRemainingTime(uint _type, out string time)
        {
            time = string.Empty;
            var statusManager = localPlayer.ToStruct()->GetStatusManager();
            if (statusManager == null) return false;

            var index = statusManager->GetStatusIndex(_type);
            if (index == -1) return false;
            time = TimeSpan.FromSeconds(statusManager->GetRemainingTime(index))
                           .ToString(@"hhmm");
            return true;
        }
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
