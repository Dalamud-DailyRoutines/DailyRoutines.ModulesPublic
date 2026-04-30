using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Agent;
using Dalamud.Game.Agent.AgentArgTypes;
using Dalamud.Game.Text;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class SystemMenuToOpenDalamud : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("SystemMenuToOpenDalamudTitle"),
        Description = Lang.Get("SystemMenuToOpenDalamudDescription"),
        Category    = ModuleCategory.System
    };

    // TODO: 看看后续羊圈怎么弄
    public override ModulePermission Permission { get; } = new() { CNDefaultEnabled = true };

    protected override void Init()
    {
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SystemMenu", OnAddon);
        DService.Instance().AgentLifecycle.RegisterListener(AgentEvent.PreReceiveEvent, AgentId.Hud, OnAgent);
    }
    
    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
        DService.Instance().AgentLifecycle.UnregisterListener(OnAgent);
    }

    private static void OnAgent(AgentEvent type, AgentArgs args)
    {
        var receiveEventArgs = args as AgentReceiveEventArgs;
        if (receiveEventArgs.EventKind != 102) return;

        var atkValues = (AtkValue*)receiveEventArgs.AtkValues;
        var index     = atkValues[0].Int;

        switch (index)
        {
            case 0:
                ChatManager.Instance().SendCommand("/xlplugins");
                atkValues[0].SetInt(int.MaxValue);
                break;
            case 1:
                ChatManager.Instance().SendCommand("/xlsettings");
                atkValues[0].SetInt(int.MaxValue);
                break;
        }
    }

    private static void OnAddon(AddonEvent type, AddonArgs args)
    {
        var setupArgs = args as AddonSetupArgs;
        var atkValues = (AtkValue*)setupArgs.AtkValues;

        using var builderOpenPluginInstaller = new RentedSeStringBuilder();
        builderOpenPluginInstaller.Builder
                                  .PushColorType(34)
                                  .Append($"{(char)SeIconChar.BoxedLetterD}")
                                  .PopColorType()
                                  .Append($" {Lang.Get("SystemMenuToOpenDalamud-OpenPluginInstaller")}");
        atkValues[7].SetManagedString(builderOpenPluginInstaller.Builder.GetViewAsSpan());
        
        using var builderSetting = new RentedSeStringBuilder();
        builderSetting.Builder
                      .PushColorType(34)
                      .Append($"{(char)SeIconChar.BoxedLetterD}")
                      .PopColorType()
                      .Append($" {Lang.Get("SystemMenuToOpenDalamud-OpenSettings")}");
        atkValues[8].SetManagedString(builderSetting.Builder.GetViewAsSpan());

        args.Addon.ToStruct()->OnRefresh(setupArgs.AtkValueCount, atkValues);
    }
}
