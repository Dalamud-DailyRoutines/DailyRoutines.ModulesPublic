using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Command;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DailyRoutines.ModulesPublic;

public class BetterMountCast : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("BetterMountCastTitle"),
        Description = GetLoc("BetterMountCastDescription"),
        Category = ModuleCategories.Action,
        Author = ["Bill"],
        ModulesRecommend = ["BetterMountRoulette"]
    };

    private static readonly string Command = "mount";

    protected override void Init()
    {
        TaskHelper ??= new TaskHelper { AbortOnTimeout = true, TimeLimitMS = 20000, ShowDebug = false };

        CommandManager.AddSubCommand(
            Command, new CommandInfo(OnCommand) { HelpMessage = GetLoc("BetterMountCast-CommandHelp") });
    }

    private void OnCommand(string SubCommand, string args)
    {
        if (IsCasting && DService.ObjectTable.LocalPlayer.CastActionType == ActionType.Mount)
            ExecuteCancelCast();


        if (!IsCasting && (CanMount || IsOnMount))
        {
            TaskHelper.Abort();
            TaskHelper.Enqueue(UseMount);
        }

        void ExecuteCancelCast()
        {
            if (Throttler.Throttle("BetterMountCast-CancelCast", 100))
                ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.CancelCast);
        }
    }

    private unsafe bool? UseMount()
    {
        if (!Throttler.Throttle("BetterMountCast-UseMount")) return false;
        if (ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, 9) != 0) return false;

        TaskHelper.DelayNext(100);
        TaskHelper.Enqueue(() => UseActionManager.UseAction(ActionType.GeneralAction, 9));
        return true;
    }

    protected override void Uninit()
    {
        TaskHelper?.Abort();

        CommandManager.RemoveSubCommand(Command);
    }
}
