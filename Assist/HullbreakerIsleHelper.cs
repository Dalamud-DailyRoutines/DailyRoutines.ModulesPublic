using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Lumina.Excel.Sheets;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace DailyRoutines.ModulesPublic;

public class HullbreakerIsleHelper : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("HullbreakerIsleHelperTitle"),
        Description = GetLoc("HullbreakerIsleHelperDescription"),
        Category    = ModuleCategories.Assist
    };

    private static readonly HashSet<string> TrapNames;
    private static readonly HashSet<string> FakeTreasureNames;

    private static HashSet<Vector3> TrapPositions         = [];
    private static HashSet<Vector3> FakeTreasurePositions = [];

    static HullbreakerIsleHelper()
    {
        TrapNames = new(StringComparer.OrdinalIgnoreCase)
        {
            // 捕兽夹
            LuminaGetter.GetRow<EObjName>(2000947)!.Value.Singular.ExtractText(),
            LuminaGetter.GetRow<EObjName>(2000947)!.Value.Plural.ExtractText(),
        };

        FakeTreasureNames = new(StringComparer.OrdinalIgnoreCase)
        {
            // 宝箱
            LuminaGetter.GetRow<EObjName>(2002491)!.Value.Singular.ExtractText(),
            LuminaGetter.GetRow<EObjName>(2002491)!.Value.Plural.ExtractText()
        };
    }

    protected override void Init()
    {
        DService.ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(DService.ClientState.TerritoryType);
    }

    private static void OnZoneChanged(ushort zone)
    {
        DService.UiBuilder.Draw -= OnDraw;
        FrameworkManager.Unregister(OnUpdate);
        TrapPositions.Clear();
        FakeTreasurePositions.Clear();
        
        if (zone != 361) return;
        FrameworkManager.Register(OnUpdate, throttleMS: 2000);
        DService.UiBuilder.Draw += OnDraw;
    }

    private static void OnDraw()
    {
        foreach (var trap in TrapPositions)
        {
            if (!DService.Gui.WorldToScreen(trap, out var screenPos)) continue;
            ImGui.GetBackgroundDrawList().AddText(screenPos, ImGui.ColorConvertFloat4ToU32(Yellow), TrapNames.First());
        }
        
        foreach (var fakeTreasure in FakeTreasurePositions)
        {
            if (!DService.Gui.WorldToScreen(fakeTreasure, out var screenPos)) continue;
            ImGui.GetBackgroundDrawList().AddText(screenPos, ImGui.ColorConvertFloat4ToU32(Yellow), FakeTreasureNames.First());
        }
    }

    private static unsafe void OnUpdate(IFramework _)
    {
        HashSet<Vector3> trapCollect = [];
        HashSet<Vector3> fakeTreasureCollect = [];
        
        foreach (var obj in DService.ObjectTable)
        {
            switch (obj.ObjectKind)
            {
                // 捕兽夹
                case ObjectKind.BattleNpc:
                    if (!TrapNames.Contains(obj.Name.ExtractText())) continue;
                    trapCollect.Add(obj.Position);
                    obj.ToStruct()->Highlight(ObjectHighlightColor.Yellow);
                    break;
                case ObjectKind.EventObj:
                    if (!FakeTreasureNames.Contains(obj.Name.ExtractText()) || 
                        !obj.IsTargetable) continue;
                    fakeTreasureCollect.Add(obj.Position);
                    obj.ToStruct()->Highlight(ObjectHighlightColor.Yellow);
                    break;
            }
        }
        
        TrapPositions = trapCollect;
        FakeTreasurePositions = fakeTreasureCollect;
    }

    protected override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        OnZoneChanged(0);
    }
}
