using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using OmenTools.Info.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Duty;

public class AutoNotifyDutyName : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoNotifyDutyNameTitle"),
        Description = Lang.Get("AutoNotifyDutyNameDescription"),
        Category    = ModuleCategory.Duty
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    protected override void Init() =>
        IClientState.Instance().TerritoryChanged += OnZoneChange;

    protected override void Uninit() =>
        IClientState.Instance().TerritoryChanged -= OnZoneChange;

    private static unsafe void OnZoneChange
    (
        uint u
    )
    {
        if (GameState.ContentFinderCondition == 0)
            return;

        var content = GameState.ContentFinderConditionData;

        var maxLevel = Math.Max(content.ClassJobLevelRequired, content.ClassJobLevelSync);

        NotifyHelper.Instance().TrayInfo
        (
            Lang.Get
            (
                "AutoNotifyDutyName-Notification-Message",
                new Dictionary<string, object>
                {
                    ["level"]   = content.ClassJobLevelRequired,
                    ["content"] = content.Name
                }
            ),
            Lang.Get("AutoNotifyDutyName-Notification-Title")
        );

        var minIL = content.ItemLevelRequired;
        var maxIL =
            content is { ItemLevelRequired: 0, ClassJobLevelSync: 0 } ?
                0 :
                Sheets.Gears.Values
                      .Where
                      (x => x.LevelEquip != 1 &&
                            x.LevelEquip <= maxLevel
                      )
                      .OrderByDescending(x => x.LevelItem.RowId)
                      .FirstOrDefault()
                      .LevelItem.RowId;

        if (content.ClassJobLevelRequired == 0)
        {
            NotifyHelper.Instance().Chat
            (
                Lang.Get
                (
                    "AutoNotifyDutyName-Message-NoLevel",
                    new Dictionary<string, object>
                    {
                        ["content"] = content.Name
                    }
                ),
                false
            );
        }
        else if (minIL == 0 && maxIL == 0)
        {
            NotifyHelper.Instance().Chat
            (
                Lang.Get
                (
                    "AutoNotifyDutyName-Message-NoItemLevel",
                    new Dictionary<string, object>
                    {
                        ["level"]   = content.ClassJobLevelRequired,
                        ["content"] = content.Name
                    }
                ),
                false
            );
        }
        else
        {
            NotifyHelper.Instance().Chat
            (
                Lang.Get
                (
                    "AutoNotifyDutyName-Message",
                    new Dictionary<string, object>
                    {
                        ["level"]   = content.ClassJobLevelRequired,
                        ["content"] = content.Name,
                        ["minIL"]   = minIL,
                        ["maxIL"]   = maxIL
                    }
                ),
                false
            );
        }
    }
}
