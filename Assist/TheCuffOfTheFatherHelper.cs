using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.OmenService;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace DailyRoutines.ModulesPublic;

public class TheCuffOfTheFatherHelper : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("TheCuffOfTheFatherHelperTitle"),
        Description = Lang.Get("TheCuffOfTheFatherHelperDescription"),
        Category    = ModuleCategory.Assist
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    protected override void Init()
    {
        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
    }

    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
    }

    private static void OnZoneChanged(ushort zone)
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);

        if (GameState.TerritoryType != 443) return;

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "_EnemyList", OnAddon);
    }

    private static unsafe void OnAddon(AddonEvent type, AddonArgs args)
    {
        var enemyListArray = AtkStage.Instance()->GetNumberArrayData(NumberArrayType.EnemyList);
        if (enemyListArray == null) return;

        var enemyCount = enemyListArray->IntArray[1];
        if (enemyCount < 1) return;

        for (var i = 0; i < enemyCount; i++)
        {
            var offset = 8 + i * 6;

            var gameObjectID = (ulong)enemyListArray->IntArray[offset];
            if (gameObjectID is 0 or 0xE0000000) continue;

            if (DService.Instance().ObjectTable.SearchByID(gameObjectID, IObjectTable.CharactersRange) is not
                {
                    ObjectKind: ObjectKind.BattleNpc,
                    DataID: 3865
                }
                obj)
                continue;

            if (DService.Instance().Condition[ConditionFlag.Mounted])
                obj.ToStruct()->TargetableStatus |= ObjectTargetableFlags.IsTargetable;
            else
                obj.ToStruct()->TargetableStatus &= ~ObjectTargetableFlags.IsTargetable;

            obj.ToStruct()->Highlight(ObjectHighlightColor.Yellow);
        }
    }
}
