using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Manager;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.ExecuteCommand.Implementations;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class GlamourPlateApplyCommand : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("GlamourPlateApplyCommandTitle"),
        Description = Lang.Get("GlamourPlateApplyCommandDescription"),
        Category    = ModuleCategory.Assist
    };

    protected override void Init() =>
        CommandManager.Instance().AddSubCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("GlamourPlateApplyCommand-CommandHelp") });
    
    protected override void Uninit() =>
        CommandManager.Instance().RemoveSubCommand(COMMAND);

    private static void OnCommand(string command, string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)           ||
            !int.TryParse(arguments.Trim(), out var index) ||
            index is < 1 or > 20) return;

        var mirageManager = MirageManager.Instance();

        if (!mirageManager->GlamourPlatesLoaded)
        {
            ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.RequestGlamourPlates);
            DService.Instance().Framework.RunOnTick(() => ApplyGlamourPlate(index), TimeSpan.FromMilliseconds(500));
            return;
        }

        ApplyGlamourPlate(index);
    }

    private static void ApplyGlamourPlate(int index)
    {
        GlamourPlateCommand.Enter();
        GlamourPlateCommand.Apply((uint)index - 1);
        GlamourPlateCommand.Exit();
    }

    #region 常量

    private const string COMMAND = "gpapply";

    #endregion
}
