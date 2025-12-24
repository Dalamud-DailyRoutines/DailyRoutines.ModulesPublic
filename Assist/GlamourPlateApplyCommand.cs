using System;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DailyRoutines.ModulesPublic;

public unsafe class GlamourPlateApplyCommand : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("GlamourPlateApplyCommandTitle"),
        Description = GetLoc("GlamourPlateApplyCommandDescription"),
        Category    = ModuleCategories.Assist,
    };

    private const string Command = "gpapply";

    protected override void Init() => 
        CommandManager.AddSubCommand(Command, new(OnCommand) { HelpMessage = GetLoc("GlamourPlateApplyCommand-CommandHelp") });

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
        ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.ApplyGlamourPlate,      (uint)index - 1);
        ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.EnterGlamourPlateState, 0, 1);
    }

    protected override void Uninit() => 
        CommandManager.RemoveSubCommand(Command);
}
