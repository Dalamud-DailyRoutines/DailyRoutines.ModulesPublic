using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

// TODO: 加入右键菜单
public class AutoReuseEmote : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoReuseEmoteTitle"),
        Description = Lang.Get("AutoReuseEmoteDescription", COMMAND, Lang.Get("AutoReuseEmote-CommandHelp")),
        Category    = ModuleCategory.System,
        Author      = ["Xww"]
    };

    protected override void Init()
    {
        TaskHelper = new();

        CommandManager.Instance().AddSubCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("AutoReuseEmote-CommandHelp") });
    }

    protected override void Uninit() =>
        CommandManager.Instance().RemoveSubCommand(COMMAND);

    private void OnCommand
    (
        string command,
        string args
    )
    {
        TaskHelper.Abort();

        args = args.Trim();
        if (string.IsNullOrWhiteSpace(args)) return;

        var spilited = args.Split(' ');
        if (spilited.Length is not (1 or 2)) return;

        var emoteName = spilited[0];
        var repeatInterval = spilited.Length == 2 && int.TryParse(spilited[1], out var repeatIntervalTime) ?
                                 repeatIntervalTime :
                                 2000;
        if (!TryParseEmoteByName(emoteName, out var emoteID)) return;

        UseEmoteByID(emoteID, repeatInterval);
    }

    private static unsafe bool TryParseEmoteByName
    (
        string     name,
        out ushort id
    )
    {
        id   = 0;
        name = name.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(name)) return false;

        var first = LuminaGetter
                    .Get<Emote>()
                    .Where
                    (x => !string.IsNullOrWhiteSpace(x.Name.ToString()) &&
                          x.TextCommand.ValueNullable != null
                    )
                    .FirstOrDefault
                    (x => x.Name.ToString().Equals(name, StringComparison.InvariantCultureIgnoreCase) ||
                          x.TextCommand.Value.Command.ToString().ToLowerInvariant().Trim('/') ==
                          name
                    );
        if (first.RowId == 0) return false;
        // 情感动作需要解锁
        if (first.UnlockLink != 0 && !UIState.Instance()->IsUnlockLinkUnlockedOrQuestCompleted(first.UnlockLink))
            return false;

        id = (ushort)first.RowId;
        return true;
    }

    private unsafe void UseEmoteByID
    (
        ushort id,
        int    interval
    )
    {
        TaskHelper.Enqueue
        (() =>
            {
                if (AgentMap.Instance()->IsPlayerMoving     ||
                    LocalPlayerState.Object == null         ||
                    ICondition.Instance().IsBetweenAreas    ||
                    ICondition.Instance().IsOccupiedInEvent ||
                    ICondition.Instance()[ConditionFlag.InCombat])
                {
                    TaskHelper.Abort();
                    return;
                }

                AgentEmote.Instance()->ExecuteEmote(id, null, false, false);
            }
        );

        TaskHelper.DelayNext(interval);
        TaskHelper.Enqueue(() => UseEmoteByID(id, interval));
    }

    #region 常量

    private const string COMMAND = "remote";

    #endregion
}
