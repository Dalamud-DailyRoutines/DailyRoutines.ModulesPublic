using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using System.Windows.Forms;

namespace DailyRoutines.Modules;

public class AutoQTE : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoQTETitle"),
        Description = GetLoc("AutoQTEDescription"),
        Category = ModuleCategories.Combat,
    };

    private static readonly string[] QTETypes = ["_QTEKeep", "_QTEMash", "_QTEKeepTime", "_QTEButton"];

    public override void Init()
    {
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, QTETypes, OnQTEAddon);
    }

    private static void OnQTEAddon(AddonEvent type, AddonArgs args)
    {
        SendKeypress(Keys.Space);
    }

    public override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnQTEAddon);
    }
}
