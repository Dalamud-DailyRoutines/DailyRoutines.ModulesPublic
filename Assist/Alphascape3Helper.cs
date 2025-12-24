using System.Linq;
using DailyRoutines.Abstracts;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DailyRoutines.ModulesPublic;

public class Alphascape3Helper : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("Alphascape3HelperTitle"),
        Description = GetLoc("Alphascape3HelperDescription"),
        Category    = ModuleCategories.Assist
    };

    protected override void Init()
    {
        DService.ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(DService.ClientState.TerritoryType);
    }

    private static void OnZoneChanged(ushort zoneID)
    {
        FrameworkManager.Unreg(OnUpdate);
        if (zoneID != 800) return;
        
        FrameworkManager.Reg(OnUpdate, throttleMS: 100);
    }

    private static void OnUpdate(IFramework framework)
    {
        if (GameState.TerritoryType != 800)
        {
            FrameworkManager.Unreg(OnUpdate);
            return;
        }

        if (DService.ObjectTable.LocalPlayer is null) return;
        
        var obj = DService.ObjectTable.FirstOrDefault(x => x is { ObjectKind: ObjectKind.BattleNpc, DataID: 9638 });
        if (obj is not { IsTargetable: true }) return;

        UseActionManager.UseAction(ActionType.Action, 12911, obj.EntityID);
    }

    protected override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        FrameworkManager.Unreg(OnUpdate);
    }
}
