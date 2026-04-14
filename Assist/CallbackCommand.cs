using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class CallbackCommand : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("CallbackCommandTitle"),
        Description = Lang.Get("CallbackCommandDescription", COMMAND),
        Category    = ModuleCategory.Assist
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private const string COMMAND = "callback";

    protected override void Init() =>
        CommandManager.Instance().AddSubCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("CallbackCommand-CommandHelp") });

    protected override void Uninit() =>
        CommandManager.Instance().RemoveSubCommand(COMMAND);

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Command"));

        using (ImRaii.PushIndent())
        {
            ImGui.TextUnformatted($"/pdr {COMMAND} {Lang.Get("CallbackCommand-CommandHelp")}");

            ImGui.NewLine();
            
            ImGui.TextWrapped(Lang.Get("CallbackCommand-CommandHelp-Detailed", COMMAND));
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
        
        var splited = arguments.Split(' ', 2);
        if (splited.Length != 2)
        {
            NotifyCommandError();
            return;
        }
        
        var addonName = splited[0];
        if (!AddonHelper.TryGetByName(addonName, out var addon))
        {
            NotifyHelper.Instance().ChatError(Lang.Get("CallbackCommand-Notification-AddonNotReady", addonName));
            return;
        }

        var callbackArguments = splited[1].Split(' ');
        try
        {
            using var atkValues = AtkValueArray.FromString(callbackArguments);
            addon->Callback(atkValues);
            NotifyHelper.Instance().Chat(Lang.Get("CallbackCommand-Notification-ExecuteSuccuess", addonName, splited[1]));
        }
        catch
        {
            NotifyHelper.Instance().ChatError(Lang.Get("CallbackCommand-Notification-ExecuteError", addonName, splited[1]));
        }

        return;

        void NotifyCommandError() =>
            NotifyHelper.Instance().ChatError(Lang.Get("Commands-InvalidArgs", $"/pdr {COMMAND}", arguments));
    }
}
