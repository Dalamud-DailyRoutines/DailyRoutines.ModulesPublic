using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace DailyRoutines.ModulesPublic;

public class HullbreakerIsleHelper : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("HullbreakerIsleHelperTitle"),
        Description = Lang.Get("HullbreakerIsleHelperDescription"),
        Category    = ModuleCategory.Assist
    };
    
    private List<Vector3> trapPositions         = [];
    private List<Vector3> fakeTreasurePositions = [];

    protected override void Init()
    {
        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
    }

    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;

        WindowManager.Instance().PostDraw -= OnDraw;
        FrameworkManager.Instance().Unreg(OnUpdate);

        trapPositions.Clear();
        fakeTreasurePositions.Clear();
    }

    private void OnZoneChanged(ushort zone)
    {
        WindowManager.Instance().PostDraw -= OnDraw;
        FrameworkManager.Instance().Unreg(OnUpdate);
        trapPositions.Clear();
        fakeTreasurePositions.Clear();

        if (GameState.TerritoryType != 361) return;

        FrameworkManager.Instance().Reg(OnUpdate, 2_000);
        WindowManager.Instance().PostDraw += OnDraw;
    }

    private void OnDraw()
    {
        var list = ImGui.GetBackgroundDrawList();

        foreach (var trap in trapPositions)
        {
            if (!DService.Instance().GameGUI.WorldToScreen(trap, out var screenPos)) continue;
            list.AddText(screenPos, KnownColor.Yellow.ToUInt(), LuminaWrapper.GetEObjName(2000947));
        }

        foreach (var fakeTreasure in fakeTreasurePositions)
        {
            if (!DService.Instance().GameGUI.WorldToScreen(fakeTreasure, out var screenPos)) continue;
            list.AddText(screenPos, KnownColor.Yellow.ToUInt(), LuminaWrapper.GetBNPCName(2896));
        }
    }

    private unsafe void OnUpdate(IFramework _)
    {
        List<Vector3> trapCollect         = [];
        List<Vector3> fakeTreasureCollect = [];

        // 捕兽夹
        foreach (var trap in DService.Instance().ObjectTable.SearchObjects
                 (
                     x => x.ObjectKind == ObjectKind.BattleNpc &&
                          x is IBattleNPC { NameID: 2891, TargetableStatus: (ObjectTargetableFlags)248 },
                     IObjectTable.CharactersRange
                 ))
        {
            trapCollect.Add(trap.Position);
            trap.ToStruct()->Highlight(ObjectHighlightColor.Yellow);
        }

        // 怪宝箱
        foreach (var treasure in DService.Instance().ObjectTable.SearchObjects
                 (
                     x => x.ObjectKind == ObjectKind.EventObj && FakeTreasuresID.Contains(x.DataID) && x.IsTargetable,
                     IObjectTable.EventRange
                 ))
        {
            fakeTreasureCollect.Add(treasure.Position);
            treasure.ToStruct()->Highlight(ObjectHighlightColor.Yellow);
        }

        trapPositions         = trapCollect;
        fakeTreasurePositions = fakeTreasureCollect;
    }

    #region 常量

    private static readonly FrozenSet<uint> FakeTreasuresID = [2004074, 2004075, 2004076, 2004077, 2004078, 2004079];

    #endregion
}
