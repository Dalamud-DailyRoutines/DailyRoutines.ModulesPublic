using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.KamiToolKit.Addons.InputNumeric;
using OmenTools.OmenService;
using AgentEmote = FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent.AgentEmote;

namespace DailyRoutines.ModulesPublic;

public class AutoReuseEmote : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoReuseEmoteTitle"),
        Description = Lang.Get("AutoReuseEmoteDescription", COMMAND),
        Category    = ModuleCategory.System,
        Author      = ["Xww"]
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private Config config = null!;
    
    private AutoReuseEmoteMenuItem menuItem = null!;

    private uint currentEmoteID;

    protected override void Init()
    {
        TaskHelper = new();

        config = LoadConfig<Config>() ?? new();

        menuItem = new(this);
        ContextMenuManager.Instance().Reg(menuItem);

        CommandManager.Instance().AddSubCommand
        (
            COMMAND,
            new(OnCommand)
            {
                HelpMessage = Lang.Get
                (
                    "AutoReuseEmote-CommandHelp",
                    new Dictionary<string, object>
                    {
                        ["command"] = COMMAND
                    }
                )
            }
        );
    }

    protected override void Uninit()
    {
        ContextMenuManager.Instance().Unreg(menuItem);

        CommandManager.Instance().RemoveSubCommand(COMMAND);
    }

    private void OnCommand
    (
        string command,
        string args
    )
    {
        args = args.Trim();

        if (string.IsNullOrEmpty(args))
        {
            StopReuse();
            return;
        }

        var splitted = args.Split(' ');
        if (splitted.Length is not (1 or 2)) return;

        var emoteName = splitted[0];
        var repeatInterval = splitted.Length == 2 && uint.TryParse(splitted[1], out var repeatIntervalTime) ?
                                 repeatIntervalTime :
                                 config.DefaultInterval;
        if (!TryParseEmoteByName(emoteName, out var emoteID)) return;

        StartReuse(emoteID, repeatInterval);
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

    private void StartReuse
    (
        ushort emoteID,
        uint   interval
    )
    {
        TaskHelper.Abort();

        currentEmoteID = emoteID;
        TaskHelper.Enqueue(CanReuseEmoteNow);
        TaskHelper.Enqueue
        (() =>
            {
                var message = Lang.Get
                (
                    "AutoReuseEmote-Notification-Start",
                    new Dictionary<string, object>
                    {
                        ["ms"]    = interval,
                        ["emote"] = LuminaWrapper.GetEmoteName(emoteID)
                    }
                );
                NotifyHelper.Toast(message);
                NotifyHelper.Instance().Chat(message);
            }
        );
        UseEmoteByID(emoteID, interval);
    }

    private void StopReuse()
    {
        TaskHelper.Abort();
        currentEmoteID = 0;
        
        var message = Lang.Get("AutoReuseEmote-Notification-Stop");
        NotifyHelper.Instance().Chat(message);
        NotifyHelper.Toast(message);
    }

    private unsafe void UseEmoteByID
    (
        ushort id,
        uint   interval
    )
    {
        TaskHelper.Enqueue
        (() =>
            {
                if (!CanReuseEmoteNow())
                {
                    StopReuse();
                    return;
                }

                AgentEmote.Instance()->ExecuteEmote(id, null, false, false);
            }
        );

        TaskHelper.DelayNext((int)interval);
        TaskHelper.Enqueue(() => UseEmoteByID(id, interval));
    }

    private static unsafe bool CanReuseEmoteNow() =>
        !AgentMap.Instance()->IsPlayerMoving     &&
        LocalPlayerState.Object != null          &&
        !ICondition.Instance().IsBetweenAreas    &&
        !ICondition.Instance().IsOccupiedInEvent &&
        !ICondition.Instance()[ConditionFlag.InCombat];

    private sealed class AutoReuseEmoteMenuItem
    (
        AutoReuseEmote module
    ) : ContextMenuEntry
    {
        public override string Identifier => nameof(AutoReuseEmote);

        public override unsafe IReadOnlyList<ContextMenuItem>? CreateMultiple
        (
            ContextMenuOpenedArgs args
        )
        {
            if (args.AddonName != "Emote")
                return null;

            var addon = args.Addon;
            if (addon == null)
                return null;

            var agent = AgentEmote.Instance();
            if (agent is null)
                return null;

            var emoteID = agent->ContextMenuEmoteID;
            if (emoteID is 0)
                return null;

            List<ContextMenuItem> items = [];
            
            if (module.currentEmoteID == emoteID)
            {
                items.Add
                (
                    new()
                    {
                        Name      = Lang.Get("AutoReuseEmote-StopReuse"),
                        OnClicked = _ => module.StopReuse()
                    }
                );
            }
            else
            {
                items.Add
                (
                    new()
                    {
                        Name      = Lang.Get("AutoReuseEmote-StartReuse"),
                        OnClicked = _ => module.StartReuse((ushort)emoteID, module.config.DefaultInterval)
                    }
                );
            }
            
            items.Add(new()
            {
                Name = Lang.Get("AutoReuseEmote-ConfigureDefualtInterval"),
                OnClicked = _ =>
                {
                    InputNumericAddon.Open
                    (
                        new()
                        {
                            Prompt          = Lang.Get("AutoReuseEmote-ConfigureDefualtInterval-PleaseInput"),
                            Value           = (int)module.config.DefaultInterval,
                            Min             = 100,
                            Max             = int.MaxValue,
                            Step            = 1000,
                            BlockedParentID = addon->Id,
                            ParentID        = addon->Id,
                            Callback = (_, info) =>
                            {
                                if (info.Result != InputNumericAddonResult.Confirmed)
                                    return;

                                module.config.DefaultInterval = (uint)info.Value;
                                module.config.Save(module);
                            },
                            Position = new
                            (
                                args.Addon->RootNode->GetNodeState().Center,
                                AddonPositionAlignment.TopCenter
                            )
                        }
                    );
                }
            });

            return items;
        }
    }

    private class Config : ModuleConfig
    {
        public uint DefaultInterval = 2000;
    }

    #region 常量

    private const string COMMAND = "remote";

    #endregion
}
