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
        Category    = ModuleCategory.UIOperation
    };

    protected override void Init()
    {
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "JournalResult", OnAddonJournalResultSetup);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,  "JournalResult", OnAddonJournalResultSetup);

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SatisfactionSupplyResult", OnAddonSatisfactionSupplyResultSetup);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,  "SatisfactionSupplyResult", OnAddonSatisfactionSupplyResultSetup);
    }
    
    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonSatisfactionSupplyResultSetup);
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonJournalResultSetup);
    }

    private static unsafe void OnAddonJournalResultSetup(AddonEvent type, AddonArgs args)
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

    private static unsafe void OnAddonSatisfactionSupplyResultSetup(AddonEvent type, AddonArgs args)
    {
        var addon = SatisfactionSupplyResult;
        if (addon == null) return;

        addon->Callback(1);
    }
}
