using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using OmenTools.OmenService;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;

namespace DailyRoutines.ModulesPublic.Duty;

public unsafe class BurnHelper : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("BurnHelperTitle"),
        Description = Lang.Get("BurnHelperDescription"),
        Category    = ModuleCategory.Duty
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    protected override void Init()
    {
        IClientState.Instance().TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
    }

    protected override void Uninit()
    {
        IClientState.Instance().TerritoryChanged -= OnZoneChanged;
        LogMessageManager.Instance().Unreg(OnPreContentText);
        FrameworkManager.Instance().Unreg(OnUpdate);
        UseActionManager.Instance().Unreg(OnPreUseAction);
    }

    private static void OnZoneChanged
    (
        uint zone
    )
    {
        LogMessageManager.Instance().Unreg(OnPreContentText);
        FrameworkManager.Instance().Unreg(OnUpdate);

        if (GameState.TerritoryType != ZONE_ID) return;

        LogMessageManager.Instance().RegPreInstanceContentText(OnPreContentText);
    }
    
    private static void OnPreContentText
    (
        ref bool isPrevented,
        ref uint rowID
    )
    {
        switch (rowID)
        {
            case VAPORIZE_TEXT_ID:
                FrameworkManager.Instance().Reg(OnUpdate);
                UseActionManager.Instance().RegPreUseAction(OnPreUseAction);
                break;
            
            case COALESCING_TEXT_ID:
                FrameworkManager.Instance().Unreg(OnUpdate);
                UseActionManager.Instance().Unreg(OnPreUseAction);
                
                var chara = CharacterManager.Instance()->FindFirst(&TryFindMistDragon);
                if (chara != null)
                    chara->TargetableStatus |= ObjectTargetableFlags.IsTargetable;
                break;
        }
    }

    private static void OnPreUseAction
    (
        ref bool                        isPrevented,
        ref ActionType                  actionType,
        ref uint                        actionID,
        ref ulong                       targetID,
        ref uint                        extraParam,
        ref ActionManager.UseActionMode queueState,
        ref uint                        comboRouteID
    )
    {
        var chara = CharacterManager.Instance()->FindFirst(&TryFindMistDragon);
        if (chara != null && chara->EntityId == targetID)
            isPrevented = true;
    }

    private static void OnUpdate
    (
        IFramework framework
    )
    {
        var chara = CharacterManager.Instance()->FindFirst(&TryFindMistDragon);
        if (chara != null)
            chara->TargetableStatus &= ~ObjectTargetableFlags.IsTargetable;
    }
    
    private static bool TryFindMistDragon
    (
        BattleChara* chara
    ) =>
        chara->ObjectKind == ObjectKind.BattleNpc &&
        chara->BaseId     == MIST_DRAGON_ID;

    #region 常量

    private const uint ZONE_ID = 1173;

    private const uint VAPORIZE_TEXT_ID   = 19708;
    private const uint COALESCING_TEXT_ID = 19712;
    
    private const uint MIST_DRAGON_ID = 9265;

    #endregion
}
