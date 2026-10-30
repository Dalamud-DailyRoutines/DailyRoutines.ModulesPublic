using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;

namespace DailyRoutines.ModulesPublic;

public class AutoQuestComplete : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoQuestCompleteTitle"),
        Description = Lang.Get("AutoQuestCompleteDescription"),
        Category    = ModuleCategory.Interface
    };

    protected override void Init()
    {
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup, "JournalResult", OnAddonJournalResultSetup);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostDraw,  "JournalResult", OnAddonJournalResultSetup);

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup, "SatisfactionSupplyResult", OnAddonSatisfactionSupplyResultSetup);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostDraw,  "SatisfactionSupplyResult", OnAddonSatisfactionSupplyResultSetup);
    }

    protected override void Uninit()
    {
        IAddonLifecycle.Instance().UnregisterListener(OnAddonSatisfactionSupplyResultSetup);
        IAddonLifecycle.Instance().UnregisterListener(OnAddonJournalResultSetup);
    }

    private static unsafe void OnAddonJournalResultSetup
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        var addon = JournalResult;
        if (addon == null) return;

        var itemID = addon->AtkValues[82].UInt;

        if (itemID == 0)
        {
            addon->Callback(0, 0);
            return;
        }

        addon->Callback(0, itemID);
    }

    private static unsafe void OnAddonSatisfactionSupplyResultSetup
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        var addon = SatisfactionSupplyResult;
        if (addon == null) return;

        addon->Callback(1);
    }
}
