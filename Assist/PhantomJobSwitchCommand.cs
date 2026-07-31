using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using TerritoryIntendedUse = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace DailyRoutines.ModulesPublic;

public class PhantomJobSwitchCommand : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("PhantomJobSwitchCommandTitle"),
        Description = Lang.Get("PhantomJobSwitchCommandDescription", COMMAND),
        Category    = ModuleCategory.Assist
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    protected override void Init() =>
        CommandManager.Instance().AddSubCommand
        (
            COMMAND,
            new(OnCommand) { HelpMessage = Lang.Get("PhantomJobSwitchCommand-CommandHelp") }
        );

    protected override void Uninit() =>
        CommandManager.Instance().RemoveSubCommand(COMMAND);

    private static unsafe void OnCommand
    (
        string command,
        string args
    )
    {
        if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent)
        {
            RaptureLogModule.Instance()->ShowLogMessage(10970);
            return;
        }

        args = args.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(args)) return;

        if (byte.TryParse(args, out var parsedJobID))
        {
            AgentMKDSupportJobList.Instance()->ChangeSupportJob(parsedJobID);
            return;
        }

        var matchingJob = LuminaGetter.Get<MKDSupportJob>()
                                      .Select
                                      (data => new
                                          {
                                              Data        = data,
                                              NameMale    = data.Name.ToString(),
                                              NameFemale  = data.NameFemale.ToString(),
                                              NameEnglish = data.NameEnglish.ToString()
                                          }
                                      )
                                      .Where
                                      (x => x.NameMale.Contains(args, StringComparison.OrdinalIgnoreCase)   ||
                                            x.NameFemale.Contains(args, StringComparison.OrdinalIgnoreCase) ||
                                            x.NameEnglish.Contains(args, StringComparison.OrdinalIgnoreCase)
                                      )
                                      .OrderBy(x => Math.Min(Math.Min(x.NameMale.Length, x.NameFemale.Length), x.NameEnglish.Length))
                                      .FirstOrDefault();
        if (matchingJob != null)
            AgentMKDSupportJobList.Instance()->ChangeSupportJob((byte)matchingJob.Data.RowId);
    }

    #region 常量

    private const string COMMAND = "pjob";

    #endregion
}
