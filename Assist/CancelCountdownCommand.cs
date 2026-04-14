using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Manager;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class CancelCountdownCommand : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("CancelCountdownCommandTitle"),
        Description = Lang.Get("CancelCountdownCommandDescription", COMMAND),
        Category    = ModuleCategory.Assist,
        Author      = ["decorwdyun"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private const string COMMAND = "ccd";

    private readonly Action cancelCountdown =
        new CompSig("E8 ?? ?? ?? ?? 45 33 E4 41 C6 47 ?? ?? 45 89 66 30").GetDelegate<Action>();

    protected override void Init() =>
        CommandManager.Instance().AddSubCommand
        (
            COMMAND,
            new(OnCommand) { HelpMessage = Lang.Get("CancelCountdownCommand-CommandHelp") }
        );

    protected override void Uninit() =>
        CommandManager.Instance().RemoveSubCommand(COMMAND);
    
    public unsafe void OnCommand(string command, string arguments)
    {
        if (!AgentCountDownSettingDialog.Instance()->Active) return;
        cancelCountdown();
    }
}
