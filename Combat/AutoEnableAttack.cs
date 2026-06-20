using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.Interop.Game;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using Action = Lumina.Excel.Sheets.Action;

namespace DailyRoutines.ModulesPublic;

public class AutoEnableAttack : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoEnableAttackTitle"),
        Description = Lang.Get("AutoEnableAttackDescription"),
        Category    = ModuleCategory.Combat
    };

    private MemoryPatch autoAttackPatch = new("41 0F B6 46 ?? FF C8", [0xB8, 0x02, 0x00, 0x00, 0x00]);

    protected override void Init()
    {
        UseActionManager.Instance().RegPreUseActionLocation(OnPreUseAction);
        UseActionManager.Instance().RegPostUseActionLocation(OnPostUseAction);
    }

    protected override void Uninit()
    {
        UseActionManager.Instance().Unreg(OnPreUseAction);
        UseActionManager.Instance().Unreg(OnPostUseAction);
    }

    private void OnPreUseAction
    (
        ref bool       isPrevented,
        ref ActionType type,
        ref uint       actionID,
        ref ulong      targetID,
        ref Vector3    location,
        ref uint       extraParam,
        ref byte       a7
    )
    {
        if (type != ActionType.Action ||
            !LuminaGetter.TryGetRow(actionID, out Action row))
        {
            autoAttackPatch.Disable();
            return;
        }

        autoAttackPatch.Set(BehavioursToModify.Contains(row.AutoAttackBehaviour));
    }
    
    private void OnPostUseAction
    (
        bool       result,
        ActionType actionType,
        uint       actionID,
        ulong      targetID,
        Vector3    location,
        uint       extraParam,
        byte       a7
    ) =>
        autoAttackPatch.Disable();
    
    private static readonly FrozenSet<byte> BehavioursToModify =
    [
        1, // 咏唱
        2, // 战技
        4  // AOE
    ];
}
