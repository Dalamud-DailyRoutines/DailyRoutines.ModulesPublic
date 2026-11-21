using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using OmenTools.Info.Game.AetheryteRecord;

namespace DailyRoutines.ModulesPublic;

internal unsafe class QueueCombatTeleport : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("QueueCombatTeleportTitle"),
        Description = Lang.Get("QueueCombatTeleportDescription"),
        Category    = ModuleCategory.System
    };

    public override string ModuleName => nameof(QueueCombatTeleport);

    public override ModuleBehaviour Behaviour { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private Hook<Conditions.Delegates.HasPermission> HasPermissionHook = null!;
    private Hook<Telepo.Delegates.Teleport>          TeleportHook      = null!;

    private Config config = null!;

    private TeleportRequest? queuedTeleport;
    private uint?            cancelLinkID;
    private int              queueVersion;

    protected override void Init()
    {
        config       = Config.Load(this) ?? new();
        config.Delay = Math.Max(0, config.Delay);

        TaskHelper ??= new()
        {
            TimeoutMS       = 0,
            RetryIntervalMS = 250
        };

        HasPermissionHook = IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(Conditions.MemberFunctionPointers),
            nameof(Conditions.HasPermission),
            (Conditions.Delegates.HasPermission)HasPermissionDetour
        );
        HasPermissionHook.Enable();

        TeleportHook = IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(Telepo.MemberFunctionPointers),
            nameof(Telepo.Teleport),
            (Telepo.Delegates.Teleport)TeleportDetour
        );
        TeleportHook.Enable();

        ExecuteCommandManager.Instance().RegPre(OnPreUseCommand);
        ICondition.Instance().ConditionChange    += OnConditionChanged;
        IClientState.Instance().TerritoryChanged += OnTerritoryChanged;
        GameState.Instance().Logout              += OnLogout;
    }

    protected override void Uninit()
    {
        GameState.Instance().Logout              -= OnLogout;
        IClientState.Instance().TerritoryChanged -= OnTerritoryChanged;
        ICondition.Instance().ConditionChange    -= OnConditionChanged;
        ExecuteCommandManager.Instance().Unreg(OnPreUseCommand);

        ClearQueuedTeleport();
    }

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(200f * GlobalUIScale);
        if (ImGui.InputInt(Lang.Get("QueueCombatTeleport-Delay"), ref config.Delay))
            config.Delay = Math.Max(0, config.Delay);
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);

        ImGuiOm.TextDisabledWrapped(Lang.Get("QueueCombatTeleport-Delay-Help"));

        if (ImGui.Checkbox(Lang.Get("QueueCombatTeleport-WaitForFateEnd"), ref config.WaitForFateEnd))
            config.Save(this);
        ImGuiOm.TextDisabledWrapped(Lang.Get("QueueCombatTeleport-WaitForFateEnd-Help"));
    }

    private bool HasPermissionDetour(Conditions* conditions, uint permissionID, int excludedCondition1, int excludedCondition2)
    {
        if (permissionID == TELEPORT_PERMISSION_ID &&
            GameState.IsLoggedIn                   &&
            GameState.ContentFinderCondition == 0  &&
            conditions->InCombat)
        {
            if (excludedCondition1 == 0)
                excludedCondition1 = (int)ConditionFlag.InCombat;
            else if (excludedCondition2 == 0)
                excludedCondition2 = (int)ConditionFlag.InCombat;
        }

        return HasPermissionHook.Original(conditions, permissionID, excludedCondition1, excludedCondition2);
    }

    private void OnPreUseCommand
    (
        ref bool               isPrevented,
        ref ExecuteCommandFlag command,
        ref uint               param1,
        ref uint               param2,
        ref uint               param3,
        ref uint               param4
    )
    {
        if (isPrevented ||
            command != ExecuteCommandFlag.Teleport)
            return;

        if (!ICondition.Instance()[ConditionFlag.InCombat])
        {
            ClearQueuedTeleport();
            return;
        }

        if (!GameState.IsLoggedIn || GameState.ContentFinderCondition != 0 || ICondition.Instance().IsBetweenAreas)
            return;

        var aetheryteID = param1;
        var subIndex    = param3;
        var destination = AetheryteRecordManager.Instance().GameRecords.FirstOrDefault
        (record => record.IsAetheryte             &&
                   record.RowID    == aetheryteID &&
                   record.SubIndex == subIndex
        );
        if (destination == null)
            return;

        isPrevented = true;
        SaveTeleportRequest(destination, param2, param4);
    }

    private bool TeleportDetour(Telepo* telepo, uint aetheryteID, byte subIndex)
    {
        if (!GameState.IsLoggedIn                          ||
            GameState.ContentFinderCondition != 0          ||
            !ICondition.Instance()[ConditionFlag.InCombat] ||
            ICondition.Instance().IsBetweenAreas)
            return TeleportHook.Original(telepo, aetheryteID, subIndex);

        var destination = AetheryteRecordManager.Instance().GameRecords.FirstOrDefault
        (record => record.IsAetheryte             &&
                   record.RowID    == aetheryteID &&
                   record.SubIndex == subIndex
        );
        if (destination == null)
            return TeleportHook.Original(telepo, aetheryteID, subIndex);

        SaveTeleportRequest(destination, null, 0);
        return true;
    }

    private void SaveTeleportRequest(AetheryteRecord destination, uint? useTicket, uint extraParam)
    {
        ClearQueuedTeleport();
        queuedTeleport = new(destination, useTicket, extraParam);
        var cancelLink = LinkPayloadManager.Instance().Reg
        (
            (_, _) => ClearQueuedTeleport("QueueCombatTeleport-RequestCancelled"),
            out var linkID
        );
        cancelLinkID = linkID;

        using var rented = new RentedSeStringBuilder();
        var message = rented.Builder
                            .Append
                            (
                                Lang.Get
                                (
                                    "QueueCombatTeleport-RequestSaved",
                                    new Dictionary<string, object>
                                    {
                                        ["destination"] = destination.Name
                                    }
                                )
                            )
                            .AppendNewLine()
                            .AppendDalamudSeString(cancelLink)
                            .Append(CANCEL_LINK_PREFIX)
                            .Append(Lang.Get("QueueCombatTeleport-CancelRequest"))
                            .PopLink()
                            .ToReadOnlySeString();
        NotifyHelper.Instance().Chat(message);
    }

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag == ConditionFlag.Unconscious && value)
        {
            ClearQueuedTeleport("QueueCombatTeleport-RequestCancelled");
            return;
        }

        if (flag != ConditionFlag.InCombat)
            return;

        queueVersion++;
        TaskHelper.Abort();

        if (!value && queuedTeleport != null)
            ScheduleTeleport();
    }

    private void OnTerritoryChanged(uint territory) =>
        ClearQueuedTeleport();

    private void OnLogout() =>
        ClearQueuedTeleport();

    private void ScheduleTeleport()
    {
        var request = queuedTeleport;
        var version = queueVersion;
        var fate    = FateManager.Instance()->CurrentFate;
        var fateID = fate == null ?
                         (ushort)0 :
                         fate->FateId;

        var onTimeout = () =>
        {
            if (version == queueVersion)
                ClearQueuedTeleport("QueueCombatTeleport-RequestTimedOut");
        };

        TaskHelper.Enqueue
        (
            () =>
            {
                if (version != queueVersion ||
                    !config.WaitForFateEnd  ||
                    fateID == 0)
                    return true;

                var currentFate = FateManager.Instance()->CurrentFate;
                return currentFate         == null   ||
                       currentFate->FateId != fateID ||
                       currentFate->State is FateState.Ended or FateState.Failed;
            },
            timeoutMS: REQUEST_TIMEOUT_MS,
            timeoutAction: onTimeout,
            exceptionAction: onTimeout
        );

        var delay = config.Delay;

        if (delay > 0)
        {
            TaskHelper.Enqueue
            (() =>
                {
                    if (version != queueVersion)
                        return;

                    NotifyHelper.Instance().Chat
                    (
                        Lang.Get
                        (
                            "QueueCombatTeleport-RequestWaiting",
                            new Dictionary<string, object>
                            {
                                ["delay"]       = delay,
                                ["destination"] = request.Destination.Name
                            }
                        )
                    );
                }
            );
            TaskHelper.DelayNext(delay);
        }

        TaskHelper.Enqueue
        (
            () => TryExecuteTeleport(request, version),
            timeoutMS: REQUEST_TIMEOUT_MS,
            timeoutAction: onTimeout,
            exceptionAction: onTimeout
        );
    }

    private bool TryExecuteTeleport(TeleportRequest request, int version)
    {
        if (version != queueVersion)
            return true;

        if (!GameState.IsLoggedIn                            ||
            GameState.ContentFinderCondition != 0            ||
            ICondition.Instance()[ConditionFlag.Unconscious] ||
            ICondition.Instance().IsBetweenAreas)
        {
            ClearQueuedTeleport("QueueCombatTeleport-RequestCancelled");
            return true;
        }

        if (ICondition.Instance()[ConditionFlag.InCombat] || ActionManager.Instance()->GetActionStatus(ActionType.Action, TELEPORT_ACTION_ID) != 0)
            return false;

        var destination = request.Destination;
        ClearQueuedTeleport();

        if (request.UseTicket is { } useTicket)
        {
            ExecuteCommandManager.Instance().ExecuteCommand
            (
                ExecuteCommandFlag.Teleport,
                destination.RowID,
                useTicket,
                destination.SubIndex,
                request.ExtraParam
            );
        }
        else
            TeleportHook.Original(Telepo.Instance(), destination.RowID, destination.SubIndex);

        var message = Lang.Get
        (
            "QueueCombatTeleport-RequestSubmitted",
            new Dictionary<string, object>
            {
                ["destination"] = destination.Name
            }
        );
        NotifyHelper.Instance().Chat(message);

        return true;
    }

    private void ClearQueuedTeleport(string? messageKey = null)
    {
        var request = queuedTeleport;
        queuedTeleport = null;
        queueVersion++;
        TaskHelper.Abort();

        if (cancelLinkID is { } linkID)
        {
            LinkPayloadManager.Instance().Unreg(linkID);
            cancelLinkID = null;
        }

        if (request != null && messageKey != null)
            NotifyHelper.Instance().ChatError
            (
                Lang.Get
                (
                    messageKey,
                    new Dictionary<string, object>
                    {
                        ["destination"] = request.Destination.Name
                    }
                )
            );
    }

    private sealed record TeleportRequest
    (
        AetheryteRecord Destination,
        uint?           UseTicket,
        uint            ExtraParam
    );

    private class Config : ModuleConfig
    {
        public int  Delay          = 500;
        public bool WaitForFateEnd = true;
    }

    #region 常量

    private const uint TELEPORT_PERMISSION_ID = 142;
    private const uint TELEPORT_ACTION_ID     = 5;
    private const int  REQUEST_TIMEOUT_MS     = 300_000;

    private const string CANCEL_LINK_PREFIX = "  \ue06f ";

    #endregion
}
