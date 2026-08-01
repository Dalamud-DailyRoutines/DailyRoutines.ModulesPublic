using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Utility;
using OmenTools.OmenService;
using TerritoryIntendedUse = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace DailyRoutines.ModulesPublic.Duty;

public class AutoDisplayZoneServerID : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoDisplayZoneServerIDTitle"),
        Description = Lang.Get("AutoDisplayZoneServerIDDescription"),
        Category    = ModuleCategory.Duty
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private Config config = null!;
    private IDtrBarEntry? entry;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();
        entry  = IDtrBar.Instance().Get("DailyRoutines-AutoDisplayZoneServerID");

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
    }

    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;

        entry?.Remove();
        entry = null;
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("SendChat"), ref config.IsEnabledChat))
            config.Save(this);
    }

    private void OnZoneChanged
    (
        uint u
    )
    {
        var isValidZone = ContentMemberListValidZones.Contains(GameState.TerritoryIntendedUse);

        entry.Shown = isValidZone;
        if (!isValidZone) return;

        var zoneServerID = GameState.ZoneServerID;
        entry.Text = $"{Lang.Get("AutoDisplayZoneServerID-ServerID")}: {zoneServerID}";

        if (!config.IsEnabledChat) return;

        using var rented = new RentedSeStringBuilder();

        var message = rented.Builder
                            .Append($"{Lang.Get("AutoDisplayZoneServerID-ServerID")}: ")
                            .PushColorType(45)
                            .Append(zoneServerID.ToString())
                            .PopColorType()
                            .ToReadOnlySeString();

        NotifyHelper.Instance().Chat(message);
    }

    private class Config : ModuleConfig
    {
        public bool IsEnabledChat = true;
    }

    #region 常量

    private static readonly FrozenSet<TerritoryIntendedUse> ContentMemberListValidZones =
    [
        TerritoryIntendedUse.OccultCrescent,
        TerritoryIntendedUse.Bozja,
        TerritoryIntendedUse.Eureka
    ];

    #endregion
}
