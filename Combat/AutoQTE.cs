using System.Windows.Forms;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public class AutoQTE : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoQTETitle"),
        Description = GetLoc("AutoQTEDescription"),
        Category    = ModuleCategories.Combat,
    };

    private static readonly CompSig                         IsInputIDPressedSig = new("E9 ?? ?? ?? ?? 83 7F 44 02");
    private unsafe delegate byte                            IsInputIDPressedDelegate(void* data, InputId id);
    private static          Hook<IsInputIDPressedDelegate>? IsInputIDPressedHook;
    
    private static readonly string[] QTETypes = ["_QTEKeep", "_QTEMash", "_QTEKeepTime", "_QTEButton"];

    protected override unsafe void Init()
    {
        IsInputIDPressedHook ??= IsInputIDPressedSig.GetHook<IsInputIDPressedDelegate>(IsInputIDPressedDetour);
        IsInputIDPressedHook.Enable();
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, QTETypes, OnQTEAddon);
    }

    private static unsafe byte IsInputIDPressedDetour(void* data, InputId id)
    {
        var orig = IsInputIDPressedHook.Original(data, id);
        
        if (!Throttler.Check("AutoQTE-QTE"))
            return 0;
        
        return orig;
    }

    private static unsafe void OnQTEAddon(AddonEvent type, AddonArgs args)
    {
        Throttler.Throttle("AutoQTE-QTE", 1_000, true);
        SendKeypress(Keys.Space);
        AtkStage.Instance()->ClearFocus();
    }

    protected override void Uninit() => 
        DService.AddonLifecycle.UnregisterListener(OnQTEAddon);
}
