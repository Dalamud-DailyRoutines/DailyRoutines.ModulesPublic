using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoHideExpBar : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoHideExpBarTitle"),
        Description = GetLoc("AutoHideExpBarDescription"),
        Category    = ModuleCategories.UIOptimization
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private static readonly CompSig UpdateExpSig = new("48 8B C4 4C 89 48 20 4C 89 40 18 53");
    private delegate void UpdateExpDelegate(AgentHUD* agent, NumberArrayData* expNumberArray, StringArrayData* expStringArray, StringArrayData* characterStringArray);
    private static Hook<UpdateExpDelegate>? UpdateExpHook;
    
    protected override void Init()
    {
        UpdateExpHook = UpdateExpSig.GetHook<UpdateExpDelegate>(UpdateExpDetour);
        UpdateExpHook.Enable();
    }

    private static void UpdateExpDetour(AgentHUD* agent, NumberArrayData* expNumberArray, StringArrayData* expStringArray, StringArrayData* characterStringArray)
    {
        UpdateExpHook.Original(agent, expNumberArray, expStringArray, characterStringArray);

        if (Exp != null)
            Exp->IsVisible = !agent->ExpFlags.HasFlag(AgentHudExpFlag.MaxLevel);
    }
}
