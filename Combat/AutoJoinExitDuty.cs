using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Manager;
using Dalamud.Game.Command;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoJoinExitDuty : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = Lang.Get("AutoJoinExitDutyTitle"),
        Description         = Lang.Get("AutoJoinExitDutyDescription"),
        Category            = ModuleCategory.Combat,
        ModulesPrerequisite = ["AutoCommenceDuty"]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    protected override void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 15_000 };

        CommandManager.Instance().AddSubCommand
        (
            "joinexitduty",
            new CommandInfo(OnCommand) { HelpMessage = Lang.Get("AutoJoinExitDutyTitle") }
        );
    }

    protected override void Uninit() =>
        CommandManager.Instance().RemoveSubCommand("joinexitduty");

    private void OnCommand(string command, string arguments)
    {
        if (DService.Instance().PartyList.Length > 0)
        {
            NotifyHelper.Instance().NotificationError(Lang.Get("AutoJoinExitDuty-AlreadyInParty"));
            return;
        }

        if (DService.Instance().Condition.IsBoundByDuty)
        {
            NotifyHelper.Instance().NotificationError(Lang.Get("AutoJoinExitDuty-AlreadyInDutyNotice"));
            return;
        }

        if (!LuminaGetter.TryGetRow<ContentFinderCondition>(TARGET_CONTENT, out var contentData)) return;

        if (!UIState.IsInstanceContentUnlocked(TARGET_CONTENT))
        {
            NotifyHelper.Instance().NotificationError(Lang.Get("AutoJoinExitDuty-DutyLockedNotice", contentData.Name.ToString()));
            return;
        }

        TaskHelper.Abort();
        EnqueueARound(TARGET_CONTENT, contentData.AllowExplorerMode);
    }

    private void EnqueueARound(uint targetContent, bool isExplorerMode)
    {
        TaskHelper.Enqueue(CheckAndSwitchJob);
        TaskHelper.Enqueue
        (() => ContentsFinderHelper.RequestDutyNormal
         (
             targetContent,
             new()
             {
                 Config817to820    = true,
                 UnrestrictedParty = true,
                 ExplorerMode      = isExplorerMode
             }
         )
        );
        TaskHelper.Enqueue(() => ExitDuty(targetContent));
    }

    private bool CheckAndSwitchJob()
    {
        var localPlayer = DService.Instance().ObjectTable.LocalPlayer;

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
                    ChatManager.Instance().SendMessage($"/gearset change {gearset->Id + 1}");
                    return true;
                }
            }
        }

        return true;
    }

    private static bool ExitDuty(uint targetContent)
    {
        if (GameMain.Instance()->CurrentContentFinderConditionId != targetContent) return false;

        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.TerritoryTransportFinish);
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.LeaveDuty);
        return true;
    }

    #region 常量

    // 伊弗利特讨伐战
    private const uint TARGET_CONTENT = 56U;

    #endregion
}
