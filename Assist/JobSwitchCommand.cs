using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Manager;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using TinyPinyin;

namespace DailyRoutines.ModulesPublic;

public class JobSwitchCommand : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("JobSwitchCommandTitle"),
        Description = Lang.Get("JobSwitchCommandDescription", COMMAND),
        Category    = ModuleCategory.Assist
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    protected override void Init() =>
        CommandManager.Instance().AddSubCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("JobSwitchCommand-CommandHelp") });

    protected override void Uninit() =>
        CommandManager.Instance().RemoveSubCommand(COMMAND);

    private static void OnCommand(string command, string args)
    {
        args = args.ToLowerInvariant().Trim();
        if (string.IsNullOrEmpty(args)) return;

        if (byte.TryParse(args, out var jobID) &&
            jobID > 0                          &&
            LuminaGetter.TryGetRow<ClassJob>(jobID, out _))
        {
            LocalPlayerState.SwitchGearset((uint)jobID);
            return;
        }

        foreach (var classJob in LuminaGetter.Get<ClassJob>())
        {
            if (classJob.RowId == 0 ||
                string.IsNullOrWhiteSpace(classJob.Name.ToString()))
                continue;

            if (classJob.Name.ToString().Contains(args, StringComparison.OrdinalIgnoreCase)                                       ||
                PinyinHelper.GetPinyin(classJob.Name.ToString(), string.Empty).Contains(args, StringComparison.OrdinalIgnoreCase) ||
                classJob.NameEnglish.ToString().Contains(args, StringComparison.OrdinalIgnoreCase))
            {
                LocalPlayerState.SwitchGearset(classJob.RowId);
                return;
            }
        }
    }

    #region 常量

    private const string COMMAND = "job";

    #endregion
}
