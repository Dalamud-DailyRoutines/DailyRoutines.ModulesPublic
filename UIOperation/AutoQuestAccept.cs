using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public class AutoQuestAccept : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoQuestAcceptTitle"),
        Description = Lang.Get("AutoQuestAcceptDescription"),
        Category    = ModuleCategory.UIOperation
    };

    protected override void Init() =>
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "JournalAccept", OnAddonSetup);
    
    protected override void Uninit() =>
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonSetup);

    protected override void ConfigUI() =>
        ImGuiOm.ConflictKeyText();

    private unsafe void OnAddonSetup(AddonEvent type, AddonArgs args)
    {
        TaskHelper.AbortByConflictKey(this);

        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null) return;

        var questID = addon->AtkValues[261].UInt;
        if (questID == 0) return;

        var isAcceptable = addon->AtkValues[4].UInt;
        if (isAcceptable == 0) return;

        addon->Callback(3, questID);
    }
}
