using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using KamiToolKit.Classes;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.OmenService.ZoneIndicator;

namespace DailyRoutines.ModulesPublic.Duty;

public unsafe class PoolOfTributeHelper : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = Lang.Get("PoolOfTributeHelperTitle"),
        Description = Lang.Get
        (
            "PoolOfTributeHelperDescription",
            LuminaWrapper.GetContentName(243),      // 须佐之男歼灭战
            LuminaWrapper.GetStatusName(STATUS_ID), // 拘束
            LuminaWrapper.GetBNPCName(6224)         // 天之岩户
        ),
        Category = ModuleCategory.Duty
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private ZoneIndicatorHandle? handle;

    private nint gameObject;

    protected override void Init()
    {
        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);

        handle = ZoneIndicatorRenderer.Instance().RegPermanent<nint>
        (
            ZONE_ID,
            () => gameObject == nint.Zero ? [] : [gameObject],
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
        handle?.Unreg();
        handle = null;

        gameObject = nint.Zero;
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

        gameObject = nint.Zero;
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

        gameObject = player.Address;
    }

    private void OnZoneChanged(uint obj)
    {
        CharacterStatusManager.Instance().Unreg(OnStatusGain);
        CharacterStatusManager.Instance().Unreg(OnStatusLose);

        gameObject = nint.Zero;

        if (GameState.TerritoryType != ZONE_ID) return;

        CharacterStatusManager.Instance().RegGain(OnStatusGain);
        CharacterStatusManager.Instance().RegLose(OnStatusLose);
    }
    
    #region 常量

    private const uint STATUS_ID = 292;
    private const uint ZONE_ID   = 674;

    #endregion
}
