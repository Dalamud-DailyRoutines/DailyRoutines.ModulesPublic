using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Command;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;

namespace DailyRoutines.Modules;

public unsafe class GlamourPlateApplyCommand : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("GlamourPlateApplyCommandTitle"),
        Description = GetLoc("GlamourPlateApplyCommandDescription"),
        Category = ModuleCategories.System,
    };

    private const string Command = "gpapply";

    public override void Init()
    {
        CommandManager.AddSubCommand(Command,
                                             new CommandInfo(OnCommand)
                                             {
                                                 HelpMessage = Lang.Get("GlamourPlateApplyCommand-CommandHelp"),
                                             });
    }

    private static void OnCommand(string command, string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments) ||
            !int.TryParse(arguments.Trim(), out var index) || index is < 1 or > 20) return;

        var mirageManager = MirageManager.Instance();
        if (!mirageManager->GlamourPlatesLoaded)
        {
            ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.RequestGlamourPlates);
            DService.Framework.RunOnTick(() => ApplyGlamourPlate(index), TimeSpan.FromMilliseconds(500));
            return;
        }

        ApplyGlamourPlate(index);
    }

    private static void ApplyGlamourPlate(int index)
    {
        ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.EnterGlamourPlateState, 1, 1);
        ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.ApplyGlamourPlate, index - 1);
        ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.EnterGlamourPlateState, 0, 1);
    }

    public override void Uninit() { CommandManager.RemoveSubCommand(Command); }
}
