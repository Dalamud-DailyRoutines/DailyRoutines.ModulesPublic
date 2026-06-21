using Dalamud.Game.ClientState.Keys;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.Info.Game.AetheryteRecord;
using OmenTools.OmenService;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace DailyRoutines.ModulesPublic.Interface.BetterTeleport;

public unsafe partial class BetterTeleport
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
            DService.Instance().Condition.IsBetweenAreas              ||
            Control.GetLocalPlayer() == null                          ||
            !UIModule.IsScreenReady())
            return;

        UIGlobals.PlaySoundEffect(23);

        ToggleDefaultPage();
    }

    private void OnCommand(string command, string args)
    {
        args = args.Trim();

        if (string.IsNullOrWhiteSpace(args))
        {
            ToggleDefaultPage();
            return;
        }

        AetheryteRecord? result;

        if (recordMatcher != null)
            result = SortSearchMatches(args, recordMatcher.Search(args, CompareCommandResult, int.MaxValue).ToList()).FirstOrDefault();
        else
        {
            result = SortSearchMatches
            (
                args,
                AetheryteRecordManager.Instance().AllRecords
                                      .Where
                                      (x => x.Name.Contains(args, StringComparison.OrdinalIgnoreCase) ||
                                            (config.Remarks.TryGetValue(x.ToString(), out var remark) &&
                                             remark.Contains(args, StringComparison.OrdinalIgnoreCase))
                                      )
                                      .OrderByDescending(x => x.IsAetheryte)
                                      .ThenBy(x => x.Name.Length)
                                      .ToList()
            ).FirstOrDefault();
        }

        if (result == null) return;

        HandleTeleport(result, args);

        return;

        static int CompareCommandResult(AetheryteRecord a, AetheryteRecord b)
        {
            var byAetheryte = b.IsAetheryte.CompareTo(a.IsAetheryte);
            if (byAetheryte != 0) return byAetheryte;

            return a.Name.Length.CompareTo(b.Name.Length);
        }
    }

    private void OnPreInputIDPressed(ref bool? overrideResult, ref InputId id)
    {
        if (!Overlay.IsOpen && !fullWindow.IsOpen)
            return;

        // Enter 键
        if (id == InputId.CMD_CHAT && DService.Instance().KeyState[VirtualKey.RETURN])
        {
            shouldFocusSearchBar = true;
            overrideResult       = false;
        }
    }
}
