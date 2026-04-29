using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.Interop.Game.Models.Packets.Upstream;
using OmenTools.OmenService;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;

namespace DailyRoutines.ModulesPublic;

public class NeverreapHelper : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("NeverreapHelperTitle"),
        Description = Lang.Get("NeverreapHelperDescription"),
        Category    = ModuleCategory.Assist
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true, AllDefaultEnabled = true };
    
    private Config config = null!;

    protected override void Init()
    {
        config     =   Config.Load(this) ?? new();
        TaskHelper ??= new() { TimeoutMS = 30_000 };

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
    }
    
    protected override void Uninit() =>
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("OnlyValidWhenSolo"), ref config.ValidWhenSolo))
            config.Save(this);
    }

    private unsafe void OnZoneChanged(uint u)
    {
        TaskHelper.Abort();

        if (GameState.TerritoryType != 420) return;

        TaskHelper.Enqueue
        (() =>
            {
                if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return false;
                if (DService.Instance().Condition.IsBetweenAreas || !UIModule.IsScreenReady()) return false;

                if (config.ValidWhenSolo && (DService.Instance().PartyList.Length > 1 || PlayersManager.Instance().PlayersAroundCount > 0))
                {
                    TaskHelper.Abort();
                    return true;
                }

                if (!EventFramework.Instance()->IsEventIDNearby(1638407)) return false;

                new EventStartPackt(localPlayer.EntityID, 1638407).Send();
                return true;
            }
        );
    }
    
    private class Config : ModuleConfig
    {
        public bool ValidWhenSolo = true;
    }
}
