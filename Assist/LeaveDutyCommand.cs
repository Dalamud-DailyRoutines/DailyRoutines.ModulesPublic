using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Manager;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using OmenTools.Interop.Game.ExecuteCommand.Implementations;

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

    protected override void Init()
    {
        LogMessageManager.Instance().RegPre(OnPreReceiveLogMessage);
        CommandManager.Instance().AddSubCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("LeaveDutyCommand-CommandHelp") });
    }

    protected override void Uninit()
    {
        CommandManager.Instance().RemoveSubCommand(COMMAND);
        LogMessageManager.Instance().Unreg(OnPreReceiveLogMessage);
    }

    protected override void ConfigUI()
    {
        using (ImRaii.Heading1(Lang.Get("Command")))
            ImGui.TextUnformatted($"/pdr {COMMAND} {Lang.Get("LeaveDutyCommand-CommandHelp")}");
    }

    private static unsafe void OnCommand
    (
        string command,
        string args
    )
    {
        if (GameState.ContentFinderCondition == KEYBOUND_BRAWLER_CONTENT_ID)
        {
            var agent = AgentKTGResult.Instance();
            if (agent == null   ||
                !agent->IsShown ||
                !agent->HasAddon)
                return;

            if (!agent->Finished)
                agent->Finish(false);
            
            return;
        }
        
        var isInCombat = ICondition.Instance()[ConditionFlag.InCombat];

        if (GameState.IsDutyCompleted || IsInSoloDuty())
        {
            DutyCommand.Leave
            (
                isInCombat ?
                    DutyCommand.LeaveDutyKind.Inactive :
                    DutyCommand.LeaveDutyKind.Normal
            );
        }

        if (isInCombat)
            return;

        DutyCommand.Leave();
    }

    private static bool IsInSoloDuty()
    {
        if (IPartyList.Instance().Length > 1)
            return false;
        
        if (PlayersManager.Instance().PlayersTargetingMe.Count > 0)
            return false;
        
        return true;
    }
    
    private static void OnPreReceiveLogMessage
    (
        ref bool                isPrevented,
        ref uint                logMessageID,
        ref LogMessageQueueItem values
    )
    {
        if (logMessageID != 914) return;
        isPrevented = true;
    }

    #region 常量

    private const string COMMAND = "leaveduty";
    
    private const uint KEYBOUND_BRAWLER_CONTENT_ID = 1095;

    #endregion
}
