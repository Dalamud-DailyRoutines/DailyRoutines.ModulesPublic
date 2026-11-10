using Dalamud.Game.ClientState.Keys;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.Info.Game.AetheryteRecord;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace DailyRoutines.ModulesPublic;

internal unsafe partial class BetterTeleport
{
    private void OnPostUseAction
    (
        ref bool                        isPrevented,
        ref ActionType                  actionType,
        ref uint                        actionID,
        ref ulong                       targetID,
        ref uint                        extraParam,
        ref ActionManager.UseActionMode queueState,
        ref uint                        comboRouteID
    )
    {
        if (actionType != ActionType.GeneralAction || actionID != 7)
            return;

        isPrevented = true;

        if (GameMain.Instance()->CurrentContentFinderConditionId != 0 ||
            ICondition.Instance().IsBetweenAreas                      ||
            Control.GetLocalPlayer() == null                          ||
            !UIModule.IsScreenReady())
            return;

        UIGlobals.PlaySoundEffect(23);

        ToggleDefaultPage();
    }

    private void OnCommand
    (
        string command,
        string args
    )
    {
        args = args.Trim();

        if (string.IsNullOrWhiteSpace(args))
        {
            ToggleDefaultPage();
            return;
        }

        AetheryteRecord? result;

        if (recordMatcher != null)
            result = SortSearchMatches(args, [.. recordMatcher.Search(args)]).FirstOrDefault();
        else
        {
            result = SortSearchMatches
            (
                args,
                [
                    .. AetheryteRecordManager.Instance().AllRecords
                                             .Where
                                             (x => x.Name.Contains(args, StringComparison.OrdinalIgnoreCase) ||
                                                   (config.Remarks.TryGetValue(x.ToString(), out var remark) &&
                                                    remark.Contains(args, StringComparison.OrdinalIgnoreCase))
                                             )
                                             .OrderByDescending(x => x.IsAetheryte)
                                             .ThenBy(x => x.Name.Length)
                ]
            ).FirstOrDefault();
        }

        if (result == null) return;

        HandleTeleport(result, args);


    }

    private void OnPreInputIDPressed
    (
        ref bool?   overrideResult,
        ref InputId id
    )
    {
        if (!Overlay.IsOpen && !fullWindow.IsOpen)
            return;

        // Enter 键
        if (id == InputId.CMD_CHAT && IKeyState.Instance()[VirtualKey.RETURN])
        {
            shouldFocusSearchBar = true;
            overrideResult       = false;
        }
    }
}
