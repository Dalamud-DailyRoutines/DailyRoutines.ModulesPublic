using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace DailyRoutines.ModulesPublic;

public class TheCuffOfTheFatherHelper : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("TheCuffOfTheFatherHelperTitle"),
        Description = GetLoc("TheCuffOfTheFatherHelperDescription"),
        Category    = ModuleCategories.Assist
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    protected override void Init()
    {
        DService.ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
    }

    protected override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        FrameworkManager.Unreg(OnUpdate);
    }

    private static void OnZoneChanged(ushort zone)
    {
        FrameworkManager.Unreg(OnUpdate);
        
        if (GameState.TerritoryType != 443) return;

        FrameworkManager.Reg(OnUpdate, throttleMS: 500);
    }

    private static unsafe void OnUpdate(IFramework _)
    {
        foreach (var obj in DService.ObjectTable)
        {
            if (obj.ObjectKind != ObjectKind.BattleNpc || obj.DataID != 3865) continue;
            
            if (DService.Condition[ConditionFlag.Mounted])
                obj.ToStruct()->TargetableStatus |= ObjectTargetableFlags.IsTargetable;
            else
                obj.ToStruct()->TargetableStatus &= ~ObjectTargetableFlags.IsTargetable;
            
            obj.ToStruct()->Highlight(ObjectHighlightColor.Yellow);
        }
    }
}
