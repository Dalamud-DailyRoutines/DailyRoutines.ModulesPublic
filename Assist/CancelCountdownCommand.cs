using System;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace DailyRoutines.ModulesPublic;

public class CancelCountdownCommand : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("CancelCountdownCommandTitle"),
        Description = GetLoc("CancelCountdownCommandDescription", COMMAND),
        Category    = ModuleCategories.Assist,
        Author      = ["decorwdyun"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private const string COMMAND = "ccd";

    private static readonly Action CancelCountdown =
        new CompSig("E8 ?? ?? ?? ?? 45 33 E4 41 C6 47 ?? ?? 45 89 66 30").GetDelegate<Action>();

    protected override void Init() =>
        CommandManager.AddSubCommand
        (
            COMMAND,
            new(OnCommand) { HelpMessage = GetLoc("CancelCountdownCommand-CommandHelp") }
        );

    public static unsafe void OnCommand(string command, string arguments)
    {
        if (!AgentCountDownSettingDialog.Instance()->Active) return;
        CancelCountdown();
    }

    protected override void Uninit() =>
        CommandManager.RemoveSubCommand(COMMAND);
}
