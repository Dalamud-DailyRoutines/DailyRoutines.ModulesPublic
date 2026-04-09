using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Interop.Game.Models;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoHideExpBar : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoHideExpBarTitle"),
        Description = Lang.Get("AutoHideExpBarDescription"),
        Category    = ModuleCategory.UIOptimization
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static readonly CompSig UpdateExpSig = new("48 8B C4 4C 89 48 20 4C 89 40 18 53");
    private delegate void UpdateExpDelegate
    (
        AgentHUD*        agent,
        NumberArrayData* expNumberArray,
        StringArrayData* expStringArray,
        StringArrayData* characterStringArray
    );
    private Hook<UpdateExpDelegate>? UpdateExpHook;

    protected override void Init()
    {
        UpdateExpHook = UpdateExpSig.GetHook<UpdateExpDelegate>(UpdateExpDetour);
        UpdateExpHook.Enable();
    }

    private void UpdateExpDetour
    (
        AgentHUD*        agent,
        NumberArrayData* expNumberArray,
        StringArrayData* expStringArray,
        StringArrayData* characterStringArray
    )
    {
        UpdateExpHook.Original(agent, expNumberArray, expStringArray, characterStringArray);

        if (Exp != null)
            Exp->IsVisible = !agent->ExpFlags.HasFlag(AgentHudExpFlag.MaxLevel);
    }
}
