using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using KamiToolKit.Classes;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.OmenService.ZoneIndicator;

namespace DailyRoutines.ModulesPublic;

internal unsafe class StrikingTreeHelper : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("StrikingTreeHelperTitle"),
        Description = Lang.Get("StrikingTreeHelperDescription"),
        Category    = ModuleCategory.Duty
    };

    public override string ModuleName => nameof(StrikingTreeHelper);

    public override ModuleBehaviour Behaviour { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private ZoneIndicatorHandle? handle;

    private List<nint> gameObjects = [];

    protected override void Init()
    {
        IClientState.Instance().TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);

        handle = ZoneIndicatorRenderer.Instance().RegPermanent
        (
            ZONE_ID,
            () => gameObjects,
            ptr => ((GameObject*)ptr)->Position,
            new()
            {
                TextGetter = _ => new()
                {
                    Text      = $"{LuminaWrapper.GetStatusName(STATUS_ID)}",
                    TextScale = 1.4f,
                    TextColor = ColorHelper.GetColor(518)
                }
            }
        );
    }

    protected override void Uninit()
    {
        IClientState.Instance().TerritoryChanged -= OnZoneChanged;

        handle?.Unreg();
        handle = null;

        gameObjects = [];
    }

    private void OnStatusLose
    (
        IBattleChara player,
        ushort       id,
        ushort       param,
        ushort       stackCount,
        ulong        sourceID
    )
    {
        if (id != STATUS_ID) return;

        gameObjects.Remove(player.Address);
    }

    private void OnStatusGain
    (
        IBattleChara player,
        ushort       id,
        ushort       param,
        ushort       stackCount,
        TimeSpan     remainingTime,
        ulong        sourceID
    )
    {
        if (id != STATUS_ID) return;

        gameObjects.Add(player.Address);
    }

    private void OnZoneChanged
    (
        uint obj
    )
    {
        CharacterStatusManager.Instance().Unreg(OnStatusGain);
        CharacterStatusManager.Instance().Unreg(OnStatusLose);

        gameObjects = [];

        if (GameState.TerritoryType != ZONE_ID) return;

        CharacterStatusManager.Instance().RegGain(OnStatusGain);
        CharacterStatusManager.Instance().RegLose(OnStatusLose);
    }

    #region 常量

    private const uint STATUS_ID = 66;
    private const uint ZONE_ID   = 374;

    #endregion
}
