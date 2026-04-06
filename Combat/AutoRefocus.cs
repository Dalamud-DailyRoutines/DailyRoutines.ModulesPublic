using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Manager;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoRefocus : ModuleBase
{
    private static ulong FocusTarget = 0xE000_0000;

    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoRefocusTitle"),
        Description = Lang.Get("AutoRefocusDescription"),
        Category    = ModuleCategory.Combat
    };

    protected override void Init()
    {
        FocusTarget = 0xE000_0000;

        TargetManager.Instance().RegPostSetFocusTarget(OnSetFocusTarget);
        DService.Instance().ClientState.TerritoryChanged += OnZoneChange;
        PlayersManager.Instance().ReceivePlayersAround              += OnReceivePlayerAround;
    }

    private static unsafe void OnReceivePlayerAround(IReadOnlyList<IPlayerCharacter> characters)
    {
        if (GameState.ContentFinderCondition == 0 || FocusTarget == 0xE000_0000 || TargetManager.FocusTarget != null) return;
        TargetManager.ToStruct()->SetFocusTargetByObjectId(FocusTarget);
    }

    private static void OnSetFocusTarget(GameObjectId gameObjectID) =>
        FocusTarget = gameObjectID;

    private static void OnZoneChange(ushort zone) =>
        FocusTarget = 0xE000_0000;

    protected override void Uninit()
    {
        PlayersManager.Instance().ReceivePlayersAround              -= OnReceivePlayerAround;
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChange;
        TargetManager.Instance().Unreg(OnSetFocusTarget);
    }
}
