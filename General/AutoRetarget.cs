using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace DailyRoutines.Modules;

public class AutoRetarget : DailyModuleBase
{
    private static Config ModuleConfig = null!;
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoRetargetTitle"),
        Description = GetLoc("AutoRetargetDescription"),
        Category = ModuleCategories.General,
        Author = ["KirisameVanilla"],
    };

    private class Config : ModuleConfiguration
    {
        public bool MarkerTrack;
        public string DisplayName = GetLoc("None");
        public bool PrioritizeForlorn;
    }

    public override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("AutoRetarget-PrioritizeForlorn"), ref ModuleConfig.PrioritizeForlorn)) 
            ModuleConfig.Save(this);

        ImGui.InputText(GetLoc("Target"), ref ModuleConfig.DisplayName, 64);
        if (ImGui.Button(GetLoc("AutoRetarget-SetToTarget")) && DService.Targets.Target is not null)
        {
            ModuleConfig.DisplayName = DService.Targets.Target is IPlayerCharacter ipc
                                           ? $"{DService.Targets.Target?.Name}@{((IPlayerCharacter)DService.Targets.Target).HomeWorld.GameData.Name}"
                                           : $"{DService.Targets.Target?.Name}";
            ModuleConfig.Save(this);
        }
        
        ImGui.SameLine();
        if (ImGui.Button(GetLoc("Clear")))
        {
            ModuleConfig.DisplayName = GetLoc("None"); ;
            ModuleConfig.Save(this);
        }

        if (ImGui.Checkbox(GetLoc("AutoRetarget-UseMarkerTrack"), ref ModuleConfig.MarkerTrack) && !ModuleConfig.MarkerTrack)
            ClearMarkers();
    }

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        TaskHelper ??= new() { TimeLimitMS = 15_000 };
        FrameworkManager.Register(true, OnUpdate);
    }

    public override void Uninit()
    {
        FrameworkManager.Unregister(OnUpdate);
        base.Uninit();
    }

    private void OnUpdate(IFramework framework)
    {
        if (!Throttler.Throttle("AutoRetarget", 1_000)) return;

        if (ModuleConfig.DisplayName == GetLoc("None") && !ModuleConfig.PrioritizeForlorn)
        {
            ClearMarkers();
            return;
        }

        List<IGameObject> found = [];
        foreach (var igo in DService.ObjectTable)
        {
            var objName = igo is IPlayerCharacter ipc
                              ? $"{igo.Name}@{ipc.HomeWorld.GameData.Name}"
                              : igo.Name.ToString();

            if (ModuleConfig.PrioritizeForlorn && igo is IBattleNpc ibn && (ibn.NameId == 6737 || ibn.NameId == 6738))
            {
                found.Insert(0, igo);
                break;
            }

            if (objName != ModuleConfig.DisplayName) continue;
            found.Add(igo);
        }

        if (found.Count != 0)
        {
            var igo = found.First();
            if (igo is IBattleNpc ibn && (ibn.NameId == 6737 || ibn.NameId == 6738)) 
                DService.Targets.Target = igo;
            else 
                DService.Targets.Target ??= igo;

            if (ModuleConfig.MarkerTrack)
            {
                EnqueuePlaceFieldMarkers(igo.Position);
            }
        }
    }

    private void EnqueuePlaceFieldMarkers(Vector3 targetPos)
    {
        TaskHelper.Abort();
        TaskHelper.Enqueue(() =>
        {
            var flagPos = new Vector2(targetPos.X, targetPos.Z);
            var currentY = targetPos.Y;
            var counter = 0;

            foreach (var fieldMarkerPoint in Enum.GetValues<FieldMarkerPoint>())
            {
                FieldMarkerHelper.PlaceLocal(fieldMarkerPoint, flagPos.ToVector3(currentY - 2 + (counter * 5)), true);
                counter++;
            }
        }, name:"放置标点");
    }

    private static unsafe void ClearMarkers()
    {
        var instance = MarkingController.Instance();
        if (instance == null) return;

        var array = instance->FieldMarkers.ToArray();
        if (array.Count(x => x.Active) != 8) return;
        if (array.Select(x => x.Position.ToVector2()).ToHashSet().Count == 1)
            Enumerable.Range(0, 8).ForEach(x => FieldMarkerHelper.PlaceLocal((uint)x, default, false));
    }
}
