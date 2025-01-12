using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;

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

    public override void Init() => FrameworkManager.Register(true, OnUpdate);

    private static unsafe void OnUpdate(IFramework _)
    {
        if (!Throttler.Throttle("ShowRemainingTimeOnUpdate", 1_000)) return;

        var localPlayer = DService.ClientState.LocalPlayer;
        if (localPlayer is null || DService.Condition[ConditionFlag.InCombat]) return;

        var atkStage = AtkStage.Instance();
        if (atkStage == null) return;

        var numberArray = atkStage->GetNumberArrayData(NumberArrayType.Hud);
        var stringArray = atkStage->GetStringArrayData(StringArrayType.Hud);
        if (numberArray == null || stringArray == null) return;

        for (var i = 0; i < 30; i++)
        {
            var text = SeString.Parse(stringArray->StringArray[37 + i]).ToString();
            var id = PresetData.Statuses.FirstOrDefault(x => x.Value.Name == text.Split("\n")[0]).Key;

            if (id == 0)
            {
                var key = numberArray->IntArray[100 + i];
                if (key == -1 || !ArrayStatusPair.TryGetValue(key, out id)) continue;
            }

            var time = SeString.Parse(stringArray->StringArray[7 + i]).ToString();
            if (string.IsNullOrEmpty(time) ||
               (!time.Contains('h') && !time.Contains("小时") && time.Length <= 3)) continue;

            if (!GetRemainingTime(id, out time)) continue;

            stringArray->SetValue(7 + i, time);
        }

        return;

        bool GetRemainingTime(uint id, out string time)
        {
            time = string.Empty;
            var statusManager = localPlayer.ToStruct()->GetStatusManager();
            if (statusManager == null) return false;
            var index = statusManager->GetStatusIndex(id);

            if (index == -1) return false;
            time = TimeSpan.FromSeconds(statusManager->GetRemainingTime(index))
                           .ToString(@"hh\hmm\m");
            return true;
        }
    }

    private static readonly Dictionary<int, uint> ArrayStatusPair = new()
    {
        { 1073957830, 46 },
        { 1073757830, 46 },
        { 1073758026, 48 }, // 短时间增益之进食
        { 1073958027, 49 },
        { 1073758027, 49 },
        { 1073958337, 1080 },
        { 1073758337, 1080 }
    };

    public override void Uninit()
    {
       FrameworkManager.Unregister(OnUpdate);
       base.Uninit();
    }
}