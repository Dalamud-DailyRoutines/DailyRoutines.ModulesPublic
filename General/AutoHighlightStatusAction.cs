using System.Collections.Frozen;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using LuminaAction = Lumina.Excel.Sheets.Action;
using Status = Lumina.Excel.Sheets.Status;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoHighlightStatusAction : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoHighlightStatusActionTitle"),
        Description = Lang.Get("AutoHighlightStatusActionDescription"),
        Category    = ModuleCategory.General,
        Author      = ["HaKu"]
    };
    
    private static readonly CompSig                            IsActionHighlightedSig = new("E8 ?? ?? ?? ?? 88 47 41 80 BB C9 00 00 00 01");
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool IsActionHighlightedDelegate(ActionManager* actionManager, ActionType actionType, uint actionID);
    private          Hook<IsActionHighlightedDelegate>? IsActionHighlightedHook;

    private Config config = null!;

    private readonly StatusSelectCombo statusCombo = new("Status");
    private readonly ActionSelectCombo actionCombo = new("Action");

    private bool keepHighlightAfterExpire = true;

    private readonly HashSet<uint> actionsToHighlight = [];

    private uint lastActionID;

    private Dictionary<uint, uint[]> comboChainsCache = [];

    private readonly Dictionary<uint, (float RemainingTime, float Countdown, float Score)> actionCalculationCache = new(32);

    private readonly Dictionary<StatusKey, StatusState> trackedStatuses    = new(64);
    private readonly Dictionary<StatusKey, int>         trackedStatusIndex = new(64);
    private readonly List<StatusKey>                    trackedStatusKeys  = new(64);

    private          long            lastUpdateTicks;
    private readonly List<StatusKey> resyncKeys = new(16);
    private readonly List<StatusKey> removeKeys = new(16);

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        if (config.MonitoredStatus.Count == 0)
        {
            config.MonitoredStatus = StatusConfigs.ToDictionary(x => x.Key, x => x.Value);
            config.Save(this);
        }

        RebuildComboChains();

        IsActionHighlightedHook = IsActionHighlightedSig.GetHook<IsActionHighlightedDelegate>(IsActionHighlightedDetour);
        IsActionHighlightedHook.Enable();

        DService.Instance().Condition.ConditionChange += OnConditionChanged;
        OnConditionChanged(ConditionFlag.InCombat, DService.Instance().Condition[ConditionFlag.InCombat]);
    }

    protected override void Uninit()
    {
        DService.Instance().Condition.ConditionChange -= OnConditionChanged;
        UseActionManager.Instance().Unreg(OnPostUseActionLocation);
        FrameworkManager.Instance().Unreg(OnUpdate);

        CharacterStatusManager.Instance().Unreg(OnGainStatus);
        CharacterStatusManager.Instance().Unreg(OnLoseStatus);

        actionsToHighlight.Clear();
        actionCalculationCache.Clear();
        trackedStatuses.Clear();
        trackedStatusIndex.Clear();
        trackedStatusKeys.Clear();
        resyncKeys.Clear();
        removeKeys.Clear();
        lastUpdateTicks = 0;
    }

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(100f * GlobalUIScale);
        if (ImGui.SliderFloat($"{Lang.Get("AutoHighlightStatusAction-Countdown")}##ReminderThreshold", ref config.Countdown, 2.0f, 10.0f, "%.1f"))
            config.Save(this);
        ImGuiOm.HelpMarker(Lang.Get("AutoHighlightStatusAction-Countdown-Help"));

        ImGui.SameLine(0, 5f * GlobalUIScale);
        if (ImGui.Checkbox($"{Lang.Get("AutoHighlightStatusAction-KeepHighlightAfterExpire")}##KeepHighlightAfterExpire", ref keepHighlightAfterExpire))
            config.Save(this);
        ImGuiOm.HelpMarker(Lang.Get("AutoHighlightStatusAction-KeepHighlightAfterExpire-Help"));

        ImGui.Spacing();

        ImGui.SetNextItemWidth(300f * GlobalUIScale);
        using (ImRaii.PushId("Status"))
            statusCombo.DrawRadio();
        ImGui.TextDisabled("↓");
        ImGui.SetNextItemWidth(300f * GlobalUIScale);
        using (ImRaii.PushId("Action"))
            actionCombo.DrawCheckbox();

        ImGui.Spacing();

        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, Lang.Get("Add")))
        {
            if (statusCombo.SelectedID != 0 && actionCombo.SelectedIDs.Count > 0)
            {
                config.MonitoredStatus[statusCombo.SelectedID] = new StatusConfig
                {
                    BindActions   = actionCombo.SelectedIDs.ToArray(),
                    Countdown     = config.Countdown,
                    KeepHighlight = keepHighlightAfterExpire
                };
                config.Save(this);
                RebuildComboChains();
            }
        }

        ImGui.NewLine();

        using var table = ImRaii.Table("PlayersInList", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY, new(0, 200f * GlobalUIScale));
        if (!table)
            return;

        ImGui.TableSetupColumn("##Delete",                                                     ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn(Lang.Get("Status"),                                             ImGuiTableColumnFlags.WidthStretch, 15);
        ImGui.TableSetupColumn(Lang.Get("Action"),                                             ImGuiTableColumnFlags.WidthStretch, 40);
        ImGui.TableSetupColumn(Lang.Get("AutoHighlightStatusAction-Countdown"),                ImGuiTableColumnFlags.WidthStretch, 20);
        ImGui.TableSetupColumn(Lang.Get("AutoHighlightStatusAction-KeepHighlightAfterExpire"), ImGuiTableColumnFlags.WidthStretch, 20);

        ImGui.TableHeadersRow();

        foreach (var (status, statusConfig) in config.MonitoredStatus)
        {
            using var id = ImRaii.PushId($"{status}");

            ImGui.TableNextRow();

            ImGui.TableNextColumn();

            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.TrashAlt, Lang.Get("Delete")))
            {
                config.MonitoredStatus.Remove(status);
                config.Save(this);
                RebuildComboChains();
                break;
            }

            ImGui.TableNextColumn();
            if (!LuminaGetter.TryGetRow<Status>(status, out var statusRow) ||
                !DService.Instance().Texture.TryGetFromGameIcon(new GameIconLookup(statusRow.Icon), out var texture))
                continue;

            ImGui.SameLine();
            ImGuiOm.TextImage(statusRow.Name.ToString(), texture.GetWrapOrEmpty().Handle, new Vector2(ImGui.GetTextLineHeight()));
            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsItemClicked())
                statusCombo.SelectedID = status;

            ImGui.TableNextColumn();

            using (ImRaii.Group())
            {
                foreach (var action in statusConfig.BindActions)
                {
                    if (!LuminaGetter.TryGetRow<LuminaAction>(action, out var actionRow) ||
                        !DService.Instance().Texture.TryGetFromGameIcon(new GameIconLookup(actionRow.Icon), out var actionTexture))
                        continue;

                    ImGuiOm.TextImage(actionRow.Name.ToString(), actionTexture.GetWrapOrEmpty().Handle, new Vector2(ImGui.GetTextLineHeight()));
                    ImGui.SameLine();
                }
            }

            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsItemClicked())
                actionCombo.SelectedIDs = statusConfig.BindActions.ToHashSet();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{statusConfig.Countdown:0.0}");
            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsItemClicked())
                config.Countdown = statusConfig.Countdown;

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(statusConfig.KeepHighlight ? Lang.Get("Yes") : Lang.Get("No"));
            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsItemClicked())
                keepHighlightAfterExpire = statusConfig.KeepHighlight;
        }
    }

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.InCombat) return;

        actionsToHighlight.Clear();
        actionCalculationCache.Clear();
        trackedStatuses.Clear();
        trackedStatusIndex.Clear();
        trackedStatusKeys.Clear();
        resyncKeys.Clear();
        removeKeys.Clear();

        FrameworkManager.Instance().Unreg(OnUpdate);
        UseActionManager.Instance().Unreg(OnPostUseActionLocation);
        CharacterStatusManager.Instance().Unreg(OnGainStatus);
        CharacterStatusManager.Instance().Unreg(OnLoseStatus);

        if (!value || GameState.IsInPVPArea) return;

        lastUpdateTicks = Environment.TickCount64;
        SeedTrackedStatuses();

        FrameworkManager.Instance().Reg(OnUpdate, 1000);
        UseActionManager.Instance().RegPostUseActionLocation(OnPostUseActionLocation);
        CharacterStatusManager.Instance().RegGain(OnGainStatus);
        CharacterStatusManager.Instance().RegLose(OnLoseStatus);
    }

    [SkipLocalsInit]
    private void OnUpdate(IFramework _)
    {
        if (GameState.IsInPVPArea || !DService.Instance().Condition[ConditionFlag.InCombat])
        {
            OnConditionChanged(ConditionFlag.InCombat, false);
            return;
        }

        var nowTicks = Environment.TickCount64;
        var dt       = (nowTicks - lastUpdateTicks) / 1000f;
        lastUpdateTicks = nowTicks;
        if (dt <= 0f)
            dt = 1f;
        if (dt > 5f)
            dt = 5f;

        if (TargetManager.Target is not IBattleNPC { IsDead: false } battleNpcTarget)
        {
            actionsToHighlight.Clear();
            return;
        }

        var currentTargetEntityID = battleNpcTarget.EntityID;
        resyncKeys.Clear();
        removeKeys.Clear();

        foreach (var key in trackedStatusKeys)
        {
            if (key.EntityID != currentTargetEntityID) continue;
            ref var state = ref CollectionsMarshal.GetValueRefOrNullRef(trackedStatuses, key);
            if (Unsafe.IsNullRef(ref state))
                continue;

            if (!state.Active) continue;

            var remaining = state.RemainingTime - dt;
            state.RemainingTime = remaining;

            if (remaining <= 0f)
            {
                resyncKeys.Add(key);
                continue;
            }

            if (TryGetStatusResyncCutoff(key.StatusID, out var cutoff) && remaining <= cutoff)
                resyncKeys.Add(key);
        }

        foreach (var key in resyncKeys)
        {
            if (key.EntityID != currentTargetEntityID) continue;
            ref var statusConfig = ref CollectionsMarshal.GetValueRefOrNullRef(config.MonitoredStatus, key.StatusID);

            if (Unsafe.IsNullRef(ref statusConfig))
            {
                removeKeys.Add(key);
                continue;
            }

            if (TryResyncStatus(key.EntityID, key.StatusID, out var remaining))
            {
                ref var state = ref CollectionsMarshal.GetValueRefOrNullRef(trackedStatuses, key);

                if (!Unsafe.IsNullRef(ref state))
                {
                    state.Active        = true;
                    state.RemainingTime = remaining;
                }
            }
            else
            {
                if (statusConfig.KeepHighlight)
                {
                    ref var state = ref CollectionsMarshal.GetValueRefOrNullRef(trackedStatuses, key);

                    if (!Unsafe.IsNullRef(ref state))
                    {
                        state.Active        = false;
                        state.RemainingTime = 0f;
                    }
                }
                else
                    removeKeys.Add(key);
            }
        }

        foreach (var key in removeKeys)
            RemoveTrackedStatus(key);

        removeKeys.Clear();

        actionCalculationCache.Clear();

        foreach (var key in trackedStatusKeys)
        {
            if (key.EntityID != currentTargetEntityID) continue;
            ref var state = ref CollectionsMarshal.GetValueRefOrNullRef(trackedStatuses, key);
            if (Unsafe.IsNullRef(ref state))
                continue;

            ref var statusConfig = ref CollectionsMarshal.GetValueRefOrNullRef(config.MonitoredStatus, key.StatusID);

            if (Unsafe.IsNullRef(ref statusConfig))
            {
                removeKeys.Add(key);
                continue;
            }

            var effectiveRemaining = state.Active ? state.RemainingTime : 0f;
            var countdown          = statusConfig.Countdown;

            var actions = statusConfig.BindActions;

            foreach (var actionID in actions)
            {
                ref var actionChain = ref CollectionsMarshal.GetValueRefOrNullRef(comboChainsCache, actionID);
                if (Unsafe.IsNullRef(ref actionChain))
                    continue;

                var score = effectiveRemaining - countdown * actionChain.Length;

                ref var current = ref CollectionsMarshal.GetValueRefOrAddDefault(actionCalculationCache, actionID, out var exists);
                if (!exists || score < current.Score)
                    current = (effectiveRemaining, countdown, score);
            }
        }

        foreach (var key in removeKeys)
            RemoveTrackedStatus(key);

        actionsToHighlight.Clear();

        foreach (var (actionID, time) in actionCalculationCache)
        {
            ref var actionChain = ref CollectionsMarshal.GetValueRefOrNullRef(comboChainsCache, actionID);
            if (Unsafe.IsNullRef(ref actionChain)) continue;

            if (time.Score > 0f) continue;

            var notInChain = true;

            foreach (var actionIDChain in actionChain)
            {
                if (ActionManager.Instance()->IsActionHighlighted(ActionType.Action, actionIDChain))
                {
                    notInChain = false;
                    break;
                }
            }

            if (!notInChain) continue;

            var notLastAction = true;
            var checkLen      = actionChain.Length - 1;

            for (var i = 0; i < checkLen; i++)
                if (actionChain[i] == lastActionID)
                {
                    notLastAction = false;
                    break;
                }

            if (notLastAction)
                actionsToHighlight.Add(actionChain[0]);
        }
    }

    private void OnPostUseActionLocation
    (
        bool       result,
        ActionType actionType,
        uint       actionID,
        ulong      targetID,
        Vector3    location,
        uint       extraParam,
        byte       a7
    )
    {
        if (GameState.IsInPVPArea || !DService.Instance().Condition[ConditionFlag.InCombat])
        {
            OnConditionChanged(ConditionFlag.InCombat, false);
            return;
        }

        actionsToHighlight.Remove(actionID);
        lastActionID = actionID;
    }

    [return: MarshalAs(UnmanagedType.U1)]
    private bool IsActionHighlightedDetour(ActionManager* actionManager, ActionType actionType, uint actionID) =>
        actionsToHighlight.Contains(actionID) || IsActionHighlightedHook.Original(actionManager, actionType, actionID);

    private void OnGainStatus(IBattleChara player, ushort statusID, ushort param, ushort stackCount, TimeSpan remainingTime, ulong sourceID)
    {
        if (sourceID                   != LocalPlayerState.EntityID ||
            remainingTime.TotalSeconds <= 0)
            return;

        ref var statusConfig = ref CollectionsMarshal.GetValueRefOrNullRef(config.MonitoredStatus, statusID);
        if (Unsafe.IsNullRef(ref statusConfig)) return;

        var key = new StatusKey(player.EntityID, statusID);
        AddOrUpdateTrackedStatus(key, new StatusState(true, (float)remainingTime.TotalSeconds));
    }

    private void OnLoseStatus(IBattleChara player, ushort statusID, ushort param, ushort stackCount, ulong sourceID)
    {
        if (sourceID != LocalPlayerState.EntityID) return;

        ref var statusConfig = ref CollectionsMarshal.GetValueRefOrNullRef(config.MonitoredStatus, statusID);

        if (Unsafe.IsNullRef(ref statusConfig))
        {
            RemoveTrackedStatus(new StatusKey(player.EntityID, statusID));
            return;
        }

        var key = new StatusKey(player.EntityID, statusID);
        if (statusConfig.KeepHighlight)
            AddOrUpdateTrackedStatus(key, new StatusState(false, 0f));
        else
            RemoveTrackedStatus(key);
    }

    private void SeedTrackedStatuses()
    {
        var counter = -1;

        foreach (var obj in DService.Instance().ObjectTable)
        {
            counter++;
            if (counter >= 200)
                break;

            if (obj is not IBattleChara { IsDead: false } battleChara)
                continue;

            var bc = battleChara.ToBCStruct();
            if (bc == null)
                continue;

            var statuses = bc->StatusManager.Status;

            for (var i = 0; i < statuses.Length; i++)
            {
                ref var status = ref statuses[i];
                if (status.StatusId              == 0) continue;
                if (status.SourceObject.ObjectId != LocalPlayerState.EntityID) continue;

                ref var statusConfig = ref CollectionsMarshal.GetValueRefOrNullRef(config.MonitoredStatus, status.StatusId);
                if (Unsafe.IsNullRef(ref statusConfig)) continue;

                var key = new StatusKey(battleChara.EntityID, status.StatusId);
                AddOrUpdateTrackedStatus(key, new StatusState(true, status.RemainingTime));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryGetStatusResyncCutoff(ushort statusID, out float cutoff)
    {
        ref var statusConfig = ref CollectionsMarshal.GetValueRefOrNullRef(config.MonitoredStatus, statusID);

        if (Unsafe.IsNullRef(ref statusConfig))
        {
            cutoff = 0f;
            return false;
        }

        var actions     = statusConfig.BindActions;
        var maxChainLen = 0;

        foreach (var actionID in actions)
        {
            ref var actionChain = ref CollectionsMarshal.GetValueRefOrNullRef(comboChainsCache, actionID);
            if (Unsafe.IsNullRef(ref actionChain)) continue;
            if (actionChain.Length > maxChainLen)
                maxChainLen = actionChain.Length;
        }

        if (maxChainLen == 0)
        {
            cutoff = 0f;
            return false;
        }

        cutoff = statusConfig.Countdown * maxChainLen;
        return cutoff > 0f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryResyncStatus(uint entityID, ushort statusID, out float remaining)
    {
        var target = CharacterManager.Instance()->LookupBattleCharaByEntityId(entityID);

        if (target == null || target->IsDead())
        {
            remaining = 0f;
            return false;
        }

        var statuses = target->StatusManager.Status;

        for (var i = 0; i < statuses.Length; i++)
        {
            ref var status = ref statuses[i];
            if (status.StatusId              != statusID) continue;
            if (status.SourceObject.ObjectId != LocalPlayerState.EntityID) continue;

            remaining = status.RemainingTime;
            return remaining > 0f;
        }

        remaining = 0f;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddOrUpdateTrackedStatus(StatusKey key, StatusState state)
    {
        if (trackedStatuses.TryGetValue(key, out _))
        {
            trackedStatuses[key] = state;
            return;
        }

        trackedStatuses.Add(key, state);
        trackedStatusIndex.Add(key, trackedStatusKeys.Count);
        trackedStatusKeys.Add(key);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RemoveTrackedStatus(StatusKey key)
    {
        if (!trackedStatuses.Remove(key))
            return;

        if (!trackedStatusIndex.Remove(key, out var index))
            return;

        var lastIndex = trackedStatusKeys.Count - 1;

        if ((uint)index < (uint)lastIndex)
        {
            var lastKey = trackedStatusKeys[lastIndex];
            trackedStatusKeys[index]    = lastKey;
            trackedStatusIndex[lastKey] = index;
        }

        trackedStatusKeys.RemoveAt(lastIndex);
    }

    private void RebuildComboChains()
    {
        var newCache = new Dictionary<uint, uint[]>(config.MonitoredStatus.Count * 2);

        foreach (var statusConfig in config.MonitoredStatus.Values)
        {
            foreach (var actionID in statusConfig.BindActions)
            {
                if (newCache.ContainsKey(actionID)) continue;
                newCache[actionID] = FetchComboChain(actionID);
            }
        }

        comboChainsCache = newCache;
        return;

        static uint[] FetchComboChain(uint actionID)
        {
            var chain = new List<uint>();
            var cur   = actionID;

            while (cur != 0 && LuminaGetter.TryGetRow<LuminaAction>(cur, out var action))
            {
                chain.Add(cur);

                var comboRef = action.ActionCombo;
                if (comboRef.RowId == 0)
                    break;
                cur = comboRef.RowId;
            }

            chain.Reverse();
            return chain.ToArray();
        }
    }

    private class Config : ModuleConfig
    {
        public float                          Countdown       = 4;
        public Dictionary<uint, StatusConfig> MonitoredStatus = [];
    }

    private class StatusConfig
    {
        public uint[] BindActions   { get; init; } = [];
        public float  Countdown     { get; init; } = 4.0f;
        public bool   KeepHighlight { get; init; } = true;
    }

    private readonly record struct StatusKey
    (
        uint   EntityID,
        ushort StatusID
    );

    private record struct StatusState
    (
        bool  Active,
        float RemainingTime
    );

    #region 常量

    private static readonly FrozenDictionary<uint, StatusConfig> StatusConfigs = new Dictionary<uint, StatusConfig>
    {

        [838]  = new() { BindActions = [3599], Countdown  = 4.0f, KeepHighlight  = true },
        [843]  = new() { BindActions = [3608], Countdown  = 4.0f, KeepHighlight  = true },
        [1881] = new() { BindActions = [16554], Countdown = 4.0f, KeepHighlight  = true },
        [1248] = new() { BindActions = [8324], Countdown  = 10.0f, KeepHighlight = false },

        [143]  = new() { BindActions = [121], Countdown   = 4.0f, KeepHighlight = true },
        [144]  = new() { BindActions = [132], Countdown   = 4.0f, KeepHighlight = true },
        [1871] = new() { BindActions = [16532], Countdown = 4.0f, KeepHighlight = true },

        [2614] = new() { BindActions = [24290], Countdown = 6.0f, KeepHighlight = true },
        [2615] = new() { BindActions = [24290], Countdown = 6.0f, KeepHighlight = true },
        [2616] = new() { BindActions = [24290], Countdown = 6.0f, KeepHighlight = true },

        [179]  = new() { BindActions = [17864], Countdown = 4.0f, KeepHighlight = true },
        [189]  = new() { BindActions = [17865], Countdown = 4.0f, KeepHighlight = true },
        [1895] = new() { BindActions = [16540], Countdown = 4.0f, KeepHighlight = true },

        [124]  = new() { BindActions = [100], Countdown        = 4.0f, KeepHighlight = true },
        [1200] = new() { BindActions = [7406, 3560], Countdown = 4.0f, KeepHighlight = true },
        [129]  = new() { BindActions = [113], Countdown        = 4.0f, KeepHighlight = true },
        [1201] = new() { BindActions = [7407, 3560], Countdown = 4.0f, KeepHighlight = true },

        [1299] = new() { BindActions = [7485], Countdown  = 4.0f, KeepHighlight = true },
        [2719] = new() { BindActions = [25772], Countdown = 4.0f, KeepHighlight = true },

        [2677] = new() { BindActions = [45], Countdown = 4.0f, KeepHighlight = true }
    }.ToFrozenDictionary();

    #endregion
}
