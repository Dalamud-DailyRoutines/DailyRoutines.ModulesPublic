using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class ReceiveEventCommand : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("ReceiveEventCommandTitle"),
        Description = Lang.Get("ReceiveEventCommandDescription", COMMAND),
        Category    = ModuleCategory.Assist
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private const string COMMAND = "agentevent";

    protected override void Init() =>
        CommandManager.Instance().AddSubCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("ReceiveEventCommand-CommandHelp") });

    protected override void Uninit() =>
        CommandManager.Instance().RemoveSubCommand(COMMAND);

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Command"));

        using (ImRaii.PushIndent())
        {
            ImGui.TextUnformatted($"/pdr {COMMAND} {Lang.Get("ReceiveEventCommand-CommandHelp")}");

            ImGui.NewLine();

            ImGui.TextWrapped(Lang.Get("ReceiveEventCommand-CommandHelp-Detailed", COMMAND));
        }
    }

    private static void OnCommand(string command, string arguments)
    {
        arguments = arguments.Trim();

        if (string.IsNullOrEmpty(arguments))
        {
            NotifyCommandError();
            return;
        }

        var splited = arguments.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (splited.Length < 2)
        {
            NotifyCommandError();
            return;
        }

        var agentName = splited[0];
        if (!Enum.TryParse<AgentId>(agentName, true, out var agentID) || !Enum.IsDefined(agentID))
        {
            NotifyHelper.Instance().ChatError(Lang.Get("ReceiveEventCommand-Notification-AgentNotReady", agentName));
            return;
        }

        if (!ulong.TryParse(splited[1], out var eventKind))
        {
            NotifyHelper.Instance().ChatError(Lang.Get("ReceiveEventCommand-Notification-InvalidEventKind", splited[1]));
            return;
        }

        var agent = AgentModule.Instance()->GetAgentByInternalId(agentID);
        if (agent == null)
        {
            NotifyHelper.Instance().ChatError(Lang.Get("ReceiveEventCommand-Notification-AgentNotReady", agentID));
            return;
        }

        var eventArgumentsText = splited.Length > 2 ? splited[2] : string.Empty;
        var eventArguments = string.IsNullOrWhiteSpace(eventArgumentsText)
                                 ? []
                                 : eventArgumentsText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        try
        {
            using var atkValues = AtkValueArray.FromString(eventArguments);
            agent->SendEvent(eventKind, atkValues);
            NotifyHelper.Instance().Chat(Lang.Get("ReceiveEventCommand-Notification-ExecuteSuccess", agentID, eventKind, eventArgumentsText));
        }
        catch
        {
            NotifyHelper.Instance().ChatError(Lang.Get("ReceiveEventCommand-Notification-ExecuteError", agentID, eventKind, eventArgumentsText));
        }

        return;

        void NotifyCommandError()
        {
            NotifyHelper.Instance().ChatError(Lang.Get("Commands-InvalidArgs", $"/pdr {COMMAND}", arguments));
        }
    }
}
