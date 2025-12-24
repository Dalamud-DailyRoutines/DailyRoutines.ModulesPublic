using System;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Lumina.Excel.Sheets;
using TinyPinyin;

namespace DailyRoutines.ModulesPublic;

public class JobSwitchCommand : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("JobSwitchCommandTitle"),
        Description = GetLoc("JobSwitchCommandDescription", Command),
        Category    = ModuleCategories.Assist
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private const string Command = "job";

    protected override void Init() => 
        CommandManager.AddSubCommand(Command, new(OnCommand) { HelpMessage = GetLoc("JobSwitchCommand-CommandHelp") });
    
    protected override void Uninit() => 
        CommandManager.RemoveSubCommand(Command);
    
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
                string.IsNullOrWhiteSpace(classJob.Name.ExtractText()))
                continue;

            if (classJob.Name.ExtractText().Contains(args, StringComparison.OrdinalIgnoreCase)                                       ||
                PinyinHelper.GetPinyin(classJob.Name.ExtractText(), string.Empty).Contains(args, StringComparison.OrdinalIgnoreCase) ||
                classJob.NameEnglish.ExtractText().Contains(args, StringComparison.OrdinalIgnoreCase))
            {
                LocalPlayerState.SwitchGearset(classJob.RowId);
                return;
            }
        }
    }
}
