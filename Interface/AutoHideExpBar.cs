using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe class AutoHideExpBar : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoHideExpBarTitle"),
        Description = Lang.Get("AutoHideExpBarDescription"),
        Category    = ModuleCategory.Interface
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private Hook<AgentHUD.Delegates.UpdateExp>? UpdateExpHook;

    protected override void Init()
    {
        UpdateExpHook = IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(AgentHUD.MemberFunctionPointers),
            "UpdateExp",
            (AgentHUD.Delegates.UpdateExp)UpdateExpDetour
        );
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
