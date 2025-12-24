using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Command;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoJoinExitDuty : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = GetLoc("AutoJoinExitDutyTitle"),
        Description         = GetLoc("AutoJoinExitDutyDescription"),
        Category            = ModuleCategories.Combat,
        ModulesPrerequisite = ["AutoCommenceDuty"]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    // 伊弗利特讨伐战
    private const uint TargetContent = 56U;

    protected override void Init()
    {
        TaskHelper ??= new() { TimeLimitMS = 15_000 };
        
        CommandManager.AddSubCommand("joinexitduty",
                                             new CommandInfo(OnCommand) { HelpMessage = GetLoc("AutoJoinExitDutyTitle") });
    }

    protected override void Uninit() => 
        CommandManager.RemoveSubCommand("joinexitduty");

    private void OnCommand(string command, string arguments)
    {
        if (DService.PartyList.Length > 0)
        {
            NotificationError(GetLoc("AutoJoinExitDuty-AlreadyInParty"));
            return;
        }

        if (BoundByDuty)
        {
            NotificationError(GetLoc("AutoJoinExitDuty-AlreadyInDutyNotice"));
            return;
        }
        
        if (!LuminaGetter.TryGetRow<ContentFinderCondition>(TargetContent, out var contentData)) return;
        if (!UIState.IsInstanceContentUnlocked(TargetContent))
        {
            NotificationError(GetLoc("AutoJoinExitDuty-DutyLockedNotice", contentData.Name.ExtractText()));
            return;
        }

        TaskHelper.Abort();
        EnqueueARound(TargetContent, contentData.AllowExplorerMode);
    }

    private void EnqueueARound(uint targetContent, bool isExplorerMode)
    {
        TaskHelper.Enqueue(CheckAndSwitchJob);
        TaskHelper.Enqueue(() => ContentsFinderHelper.RequestDutyNormal(targetContent,
                                                              new()
                                                              {
                                                                  Config817to820 = true,
                                                                  UnrestrictedParty = true,
                                                                  ExplorerMode = isExplorerMode
                                                              }));
        TaskHelper.Enqueue(() => ExitDuty(targetContent));
    }

    private bool? CheckAndSwitchJob()
    {
        var localPlayer = DService.ObjectTable.LocalPlayer;
        if (localPlayer == null)
        {
            TaskHelper.Abort();
            return true;
        }

        if (localPlayer.ClassJob.RowId is >= 8 and <= 18)
        {
            var gearsetModule = RaptureGearsetModule.Instance();
            for (var i = 0; i < 100; i++)
            {
                var gearset = gearsetModule->GetGearset(i);
                if (gearset == null) continue;
                if (!gearset->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists)) continue;
                if (gearset->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.MainHandMissing)) continue;
                if (gearset->Id != i) continue;
                if (gearset->ClassJob > 18)
                {
                    ChatManager.SendMessage($"/gearset change {gearset->Id + 1}");
                    return true;
                }
            }
        }

        return true;
    }

    private static bool? ExitDuty(uint targetContent)
    {
        if (GameMain.Instance()->CurrentContentFinderConditionId != targetContent) return false;

        ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.TerritoryTransportFinish);
        ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.LeaveDuty);
        return true;
    }
}
