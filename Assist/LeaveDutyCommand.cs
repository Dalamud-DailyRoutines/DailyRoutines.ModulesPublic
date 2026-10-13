using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.ClientState.Conditions;
using OmenTools.Info.Game.Enums;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class LeaveDutyCommand : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = Lang.Get("LeaveDutyCommandTitle"),
        Description = Lang.Get
        (
            "LeaveDutyCommandDescription",
            new Dictionary<string, object>
            {
                ["command"] = COMMAND
            }
        ),
        Category = ModuleCategory.Assist
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    protected override void Init() =>
        CommandManager.Instance().AddSubCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("LeaveDutyCommand-CommandHelp") });

    protected override void Uninit() =>
        CommandManager.Instance().RemoveSubCommand(COMMAND);

    protected override void ConfigUI()
    {
        using (ImRaii.Heading1(Lang.Get("Command")))
            ImGui.TextUnformatted($"/pdr {COMMAND} {Lang.Get("LeaveDutyCommand-CommandHelp")}");
    }

    private static void OnCommand
    (
        string command,
        string args
    )
    {
        if (ICondition.Instance()[ConditionFlag.InCombat])
            return;

        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.LeaveDuty);
    }

    #region 常量

    private const string COMMAND = "leaveduty";

    #endregion
}
