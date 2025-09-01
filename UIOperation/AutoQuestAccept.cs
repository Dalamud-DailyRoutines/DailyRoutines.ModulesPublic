using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public class AutoQuestAccept : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoQuestAcceptTitle"),
        Description = GetLoc("AutoQuestAcceptDescription"),
        Category    = ModuleCategories.UIOperation,
    };

    protected override void Init() => 
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "JournalAccept", OnAddonSetup);

    protected override void ConfigUI() => 
        ConflictKeyText();

    private unsafe void OnAddonSetup(AddonEvent type, AddonArgs args)
    {
        InterruptByConflictKey(TaskHelper, this);

        var addon = (AtkUnitBase*)args.Addon;
        if (addon == null) return;

        var questID = addon->AtkValues[261].UInt;
        if (questID == 0) return;

        var isAcceptable = addon->AtkValues[4].UInt;
        if (isAcceptable == 0) return;
        
        Callback(addon, true, 3, questID);
    }

    protected override void Uninit() => 
        DService.AddonLifecycle.UnregisterListener(OnAddonSetup);
}
