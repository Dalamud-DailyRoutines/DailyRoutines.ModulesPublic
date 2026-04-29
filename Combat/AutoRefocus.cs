using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Manager;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoRefocus : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoRefocusTitle"),
        Description = Lang.Get("AutoRefocusDescription"),
        Category    = ModuleCategory.Combat
    };

    private ulong focusTarget = 0xE000_0000;

    protected override void Init()
    {
        focusTarget = 0xE000_0000;

        TargetManager.Instance().RegPostSetFocusTarget(OnSetFocusTarget);
        DService.Instance().ClientState.TerritoryChanged += OnZoneChange;
        PlayersManager.Instance().ReceivePlayersAround   += OnReceivePlayerAround;
    }

    protected override void Uninit()
    {
        PlayersManager.Instance().ReceivePlayersAround   -= OnReceivePlayerAround;
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChange;
        TargetManager.Instance().Unreg(OnSetFocusTarget);
    }

    private unsafe void OnReceivePlayerAround(IReadOnlyList<IPlayerCharacter> characters)
    {
        if (GameState.ContentFinderCondition == 0 || focusTarget == 0xE000_0000 || TargetManager.FocusTarget != null) return;
        TargetManager.ToStruct()->SetFocusTargetByObjectId(focusTarget);
    }

    private void OnSetFocusTarget(GameObjectId gameObjectID) =>
        focusTarget = gameObjectID;

    private void OnZoneChange(uint u) =>
        focusTarget = 0xE000_0000;
}
