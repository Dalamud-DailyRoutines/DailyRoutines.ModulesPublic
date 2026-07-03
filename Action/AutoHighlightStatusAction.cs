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
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using OmenTools.ImGuiOm.Widgets.Combos;
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
        Category    = ModuleCategory.Action,
        Author      = ["HaKu"]
    };

    private static readonly CompSig IsActionHighlightedSig = new("E8 ?? ?? ?? ?? 88 47 41 80 BB C9 00 00 00 01");
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool IsActionHighlightedDelegate(ActionManager* actionManager, ActionType actionType, uint actionID);
    private Hook<IsActionHighlightedDelegate>? IsActionHighlightedHook;

    private Config config = null!;

    private readonly StatusSelectCombo statusCombo = new("Status");
    private readonly ActionSelectCombo actionCombo = new("Action");

    private readonly List<uint> actionsToHighlight = new(8);

    private readonly List<(uint ActionID, uint[] Chain)> comboChainsCache = new(16);

    private readonly List<ActionCalcEntry> actionCalculationCache = new(16);

    private readonly List<TrackedStatus> trackedStatuses = new(32);

    private long lastUpdateTicks;

    // 临时缓冲区
    private readonly List<int> resyncIndices = new(16);
    private readonly List<int> removeIndices = new(16);

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        if (config.MonitoredStatus.Count == 0)
        {
            config.MonitoredStatus = StatusConfigs.ToDictionary
            (
                x => x.Key,
                x => new StatusConfig
                {
                    BindActions   = x.Value.BindActions,
                    Countdown     = x.Value.Countdown,
                    KeepHighlight = x.Value.KeepHighlight
                }
            );
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
        ExitCombat();
    }

    protected override void ConfigUI()
    {
        using var table = ImRaii.Table
        (
            "PlayersInList",
            5,
            ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY,
            new(0, 300f * GlobalUIScale)
        );
        if (!table)
            return;

        ImGui.TableSetupColumn("OPERATION");
        ImGui.TableSetupColumn("STATUS");
        ImGui.TableSetupColumn("ACTION");
        ImGui.TableSetupColumn("COUNTDOWN");
        ImGui.TableSetupColumn("KEEP_HIGHLIGHT");

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);

        ImGui.TableNextColumn();
        if (ImGuiOm.SelectableTextCentered($"{FontAwesomeIcon.Plus.ToIconString()}  {Lang.Get("Add")} / {Lang.Get("Edit")}"))
            ImGui.OpenPopup("ADD_NEW_PRESET_POPUP");

        using (ImRaii.ItemWidth(200f * GlobalUIScale))
        using (var popup = ImRaii.Popup("ADD_NEW_PRESET_POPUP"))
        {
            if (popup)
            {
                ImGui.SliderFloat($"{Lang.Get("AutoHighlightStatusAction-Countdown")}##ReminderThreshold", ref config.Countdown, 2.0f, 10.0f, "%.1f");
                if (ImGui.IsItemDeactivatedAfterEdit())
                    config.Save(this);
                ImGuiOm.HelpMarker(Lang.Get("AutoHighlightStatusAction-Countdown-Help"));

                if (ImGui.Checkbox($"{Lang.Get("AutoHighlightStatusAction-KeepHighlightAfterExpire")}##KeepHighlightAfterExpire", ref config.KeepHighlight))
                    config.Save(this);
                ImGuiOm.HelpMarker(Lang.Get("AutoHighlightStatusAction-KeepHighlightAfterExpire-Help"));

                ImGui.Spacing();

                using (ImRaii.PushId("Status"))
                    statusCombo.DrawRadio();

                ImGui.SameLine();
                ImGui.TextUnformatted(Lang.Get("Status"));

                using (ImRaii.PushId("Action"))
                    actionCombo.DrawCheckbox();

                ImGui.SameLine();
                ImGui.TextUnformatted(Lang.Get("Action"));

                ImGui.Spacing();

                if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, Lang.Get("Add")))
                {
                    if (statusCombo.SelectedID != 0 && actionCombo.SelectedIDs.Count > 0)
                    {
                        config.MonitoredStatus[statusCombo.SelectedID] = new StatusConfig
                        {
                            BindActions   = actionCombo.SelectedIDs.ToArray(),
                            Countdown     = config.Countdown,
                            KeepHighlight = config.KeepHighlight
                        };
                        config.Save(this);
                        RebuildComboChains();
                    }
                }
            }
        }

        ImGui.TableNextColumn();
        ImGuiOm.SelectableTextCentered(Lang.Get("Status"));

        ImGui.TableNextColumn();
        ImGuiOm.SelectableTextCentered(Lang.Get("Action"));

        ImGui.TableNextColumn();
        ImGuiOm.SelectableTextCentered(Lang.Get("AutoHighlightStatusAction-Countdown"));

        ImGui.TableNextColumn();
        ImGuiOm.SelectableTextCentered(Lang.Get("AutoHighlightStatusAction-KeepHighlightAfterExpire"));

        uint pendingDeleteKey = 0;
        var  hasPendingDelete = false;

        foreach (var (status, statusConfig) in config.MonitoredStatus)
        {
            using var id = ImRaii.PushId($"{status}");

            if (!LuminaGetter.TryGetRow<Status>(status, out var statusRow) ||
                !DService.Instance().Texture.TryGetFromGameIcon(new(statusRow.Icon), out var texture))
                continue;

            ImGui.TableNextRow();

            ImGui.TableNextColumn();

            if (ImGuiOm.ButtonStretch($"{FontAwesomeIcon.TrashAlt.ToIconString()}  {Lang.Get("Delete")}"))
            {
                pendingDeleteKey = status;
                hasPendingDelete = true;
            }

            ImGui.TableNextColumn();

            using (ImRaii.Group())
            {
                ImGui.Image
                (
                    texture.GetWrapOrEmpty().Handle,
                    new(ImGui.GetTextLineHeight())
                );

                ImGui.SameLine();
                ImGui.TextUnformatted(statusRow.Name.ToString());
            }

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

                    using (ImRaii.Group())
                    {
                        ImGui.Image
                        (
                            actionTexture.GetWrapOrEmpty().Handle,
                            new(ImGui.GetTextLineHeight())
                        );

                        ImGui.SameLine();
                        ImGui.TextUnformatted(actionRow.Name.ToString());
                    }

                    ImGui.SameLine();
                }
            }

            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsItemClicked())
                actionCombo.SelectedIDs = statusConfig.BindActions.ToHashSet();

            ImGui.TableNextColumn();
            var countdown = statusConfig.Countdown;
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputFloat($"##Countdown_{status}", ref countdown, 0.1f, 0.5f, "%.1f"))
                statusConfig.Countdown = Math.Clamp(countdown, 0.5f, 30.0f);

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                config.Save(this);
                RebuildComboChains();
            }

            ImGui.TableNextColumn();
            var keepHighlight = statusConfig.KeepHighlight;
            ImGuiOm.CenterCursorXFor(ImGui.GetFrameHeight());

            if (ImGui.Checkbox($"##KeepHighlight_{status}", ref keepHighlight))
            {
                statusConfig.KeepHighlight = keepHighlight;
                config.Save(this);
            }
        }

        if (hasPendingDelete)
        {
            config.MonitoredStatus.Remove(pendingDeleteKey);
            config.Save(this);
            RebuildComboChains();
        }
    }

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.InCombat) return;
        if (value && !GameState.IsInPVPArea)
            EnterCombat();
        else
            ExitCombat();
    }

    private void EnterCombat()
    {
        ExitCombat();

        lastUpdateTicks = Environment.TickCount64;
        SeedTrackedStatuses();

        FrameworkManager.Instance().Reg(OnUpdate, 500);
        UseActionManager.Instance().RegPostUseActionLocation(OnPostUseActionLocation);
        CharacterStatusManager.Instance().RegGain(OnGainStatus);
        CharacterStatusManager.Instance().RegLose(OnLoseStatus);
    }

    private void ExitCombat()
    {
        FrameworkManager.Instance().Unreg(OnUpdate);
        UseActionManager.Instance().Unreg(OnPostUseActionLocation);
        CharacterStatusManager.Instance().Unreg(OnGainStatus);
        CharacterStatusManager.Instance().Unreg(OnLoseStatus);

        actionsToHighlight.Clear();
        actionCalculationCache.Clear();
        trackedStatuses.Clear();
        resyncIndices.Clear();
        removeIndices.Clear();
        lastUpdateTicks = 0;
    }

    [SkipLocalsInit]
    private void OnUpdate(IFramework _)
    {
        if (GameState.IsInPVPArea || !DService.Instance().Condition[ConditionFlag.InCombat])
        {
            ExitCombat();
            return;
        }

        var nowTicks = Environment.TickCount64;
        var dt       = (nowTicks - lastUpdateTicks) / 1000f;
        lastUpdateTicks = nowTicks;
        if (dt <= 0f)
            dt = 1f;
        if (dt > 5f)
            dt = 5f;

        resyncIndices.Clear();
        removeIndices.Clear();

        // 第一遍: 递减 + 收集需要 resync 的索引
        for (var i = 0; i < trackedStatuses.Count; i++)
        {
            ref var entry = ref CollectionsMarshal.AsSpan(trackedStatuses)[i];
            if (!entry.State.Active)
                continue;

            var remaining = entry.State.RemainingTime - dt;
            entry.State.RemainingTime = remaining;

            if (remaining <= 0f)
            {
                resyncIndices.Add(i);
                continue;
            }

            if (TryGetStatusResyncCutoff(entry.Key.StatusID, out var cutoff) && remaining <= cutoff)
                resyncIndices.Add(i);
        }

        // 第二遍: resync
        foreach (var idx in resyncIndices)
        {
            ref var entry        = ref CollectionsMarshal.AsSpan(trackedStatuses)[idx];
            ref var statusConfig = ref CollectionsMarshal.GetValueRefOrNullRef(config.MonitoredStatus, entry.Key.StatusID);

            if (Unsafe.IsNullRef(ref statusConfig))
            {
                removeIndices.Add(idx);
                continue;
            }

            if (TryResyncStatus(entry.Key.EntityID, entry.Key.StatusID, out var remaining))
            {
                entry.State.Active        = true;
                entry.State.RemainingTime = remaining;
            }
            else
            {
                if (statusConfig.KeepHighlight)
                {
                    entry.State.Active        = false;
                    entry.State.RemainingTime = 0f;
                }
                else
                    removeIndices.Add(idx);
            }
        }

        for (var i = removeIndices.Count - 1; i >= 0; i--)
            trackedStatuses.RemoveAt(removeIndices[i]);

        removeIndices.Clear();
        actionCalculationCache.Clear();

        uint currentTargetEntityID = 0;
        if (TargetManager.Target is IBattleNPC { IsDead: false } battleNpcTarget)
            currentTargetEntityID = battleNpcTarget.EntityID;

        var localPlayerEntityID = LocalPlayerState.EntityID;

        for (var i = 0; i < trackedStatuses.Count; i++)
        {
            ref var entry = ref CollectionsMarshal.AsSpan(trackedStatuses)[i];

            if (entry.Key.EntityID != currentTargetEntityID && entry.Key.EntityID != localPlayerEntityID)
                continue;

            ref var statusConfig = ref CollectionsMarshal.GetValueRefOrNullRef(config.MonitoredStatus, entry.Key.StatusID);

            if (Unsafe.IsNullRef(ref statusConfig))
            {
                removeIndices.Add(i);
                continue;
            }

            var effectiveRemaining = entry.State.Active ? entry.State.RemainingTime : 0f;
            var countdown          = statusConfig.Countdown;

            foreach (var actionID in statusConfig.BindActions)
            {
                var chainIdx = FindComboChainIndex(actionID);
                if (chainIdx < 0) continue;

                ref var chain = ref CollectionsMarshal.AsSpan(comboChainsCache)[chainIdx];
                var     score = effectiveRemaining - countdown * chain.Chain.Length;

                var found = false;

                for (var j = 0; j < actionCalculationCache.Count; j++)
                {
                    if (actionCalculationCache[j].ActionID != actionID)
                        continue;

                    if (score < actionCalculationCache[j].Score)
                        actionCalculationCache[j] = new ActionCalcEntry(actionID, effectiveRemaining, countdown, score);

                    found = true;
                    break;
                }

                if (!found)
                    actionCalculationCache.Add(new ActionCalcEntry(actionID, effectiveRemaining, countdown, score));
            }
        }

        for (var i = removeIndices.Count - 1; i >= 0; i--)
            trackedStatuses.RemoveAt(removeIndices[i]);

        actionsToHighlight.Clear();

        foreach (var calc in actionCalculationCache)
        {
            if (calc.Score > 0f) continue;

            var chainIdx = FindComboChainIndex(calc.ActionID);
            if (chainIdx < 0) continue;

            ref var chain = ref CollectionsMarshal.AsSpan(comboChainsCache)[chainIdx];

            // 此处经过 Detour 回到自身, 由于上方刚 Clear 且尚未 Add,
            // 实际只检查游戏原生高亮 (如连击进行中游戏会高亮下一步, 据此判断连击是否在进行)
            var notInChain = true;

            foreach (var actionIDChain in chain.Chain)
            {
                if (ActionManager.Instance()->IsActionHighlighted(ActionType.Action, actionIDChain))
                {
                    notInChain = false;
                    break;
                }
            }

            if (!notInChain) continue;

            actionsToHighlight.Add(chain.Chain[0]);
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
            ExitCombat();
            return;
        }

        actionsToHighlight.Remove(actionID);
    }

    [return: MarshalAs(UnmanagedType.U1)]
    private bool IsActionHighlightedDetour(ActionManager* actionManager, ActionType actionType, uint actionID)
    {
        foreach (var action in actionsToHighlight)
        {
            if (action == actionID)
                return true;
        }

        return IsActionHighlightedHook.Original(actionManager, actionType, actionID);
    }

    private void OnGainStatus
    (
        IBattleChara player,
        ushort       statusID,
        ushort       param,
        ushort       stackCount,
        TimeSpan     remainingTime,
        ulong        sourceID
    )
    {
        if (sourceID                   != LocalPlayerState.EntityID ||
            remainingTime.TotalSeconds <= 0)
            return;

        ref var statusConfig = ref CollectionsMarshal.GetValueRefOrNullRef(config.MonitoredStatus, statusID);
        if (Unsafe.IsNullRef(ref statusConfig)) return;

        AddOrUpdateTrackedStatus(player.EntityID, statusID, true, (float)remainingTime.TotalSeconds);
    }

    private void OnLoseStatus
    (
        IBattleChara player,
        ushort       statusID,
        ushort       param,
        ushort       stackCount,
        ulong        sourceID
    )
    {
        if (sourceID != LocalPlayerState.EntityID) return;

        ref var statusConfig = ref CollectionsMarshal.GetValueRefOrNullRef(config.MonitoredStatus, statusID);

        if (Unsafe.IsNullRef(ref statusConfig))
        {
            RemoveTrackedStatus(player.EntityID, statusID);
            return;
        }

        if (statusConfig.KeepHighlight)
            AddOrUpdateTrackedStatus(player.EntityID, statusID, false, 0f);
        else
            RemoveTrackedStatus(player.EntityID, statusID);
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

                AddOrUpdateTrackedStatus(battleChara.EntityID, status.StatusId, true, status.RemainingTime);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindComboChainIndex(uint actionID)
    {
        for (var i = 0; i < comboChainsCache.Count; i++)
            if (comboChainsCache[i].ActionID == actionID)
                return i;

        return -1;
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

        var maxChainLen = 0;

        foreach (var actionID in statusConfig.BindActions)
        {
            var chainIdx = FindComboChainIndex(actionID);
            if (chainIdx < 0) continue;

            ref var chain = ref CollectionsMarshal.AsSpan(comboChainsCache)[chainIdx];
            if (chain.Chain.Length > maxChainLen)
                maxChainLen = chain.Chain.Length;
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
    private void AddOrUpdateTrackedStatus(uint entityID, ushort statusID, bool active, float remainingTime)
    {
        var span = CollectionsMarshal.AsSpan(trackedStatuses);

        for (var i = 0; i < span.Length; i++)
        {
            if (span[i].Key.EntityID != entityID || span[i].Key.StatusID != statusID)
                continue;

            span[i].State.Active        = active;
            span[i].State.RemainingTime = remainingTime;
            return;
        }

        trackedStatuses.Add
        (
            new TrackedStatus
            (
                new StatusKey(entityID, statusID),
                new StatusState(active, remainingTime)
            )
        );
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RemoveTrackedStatus(uint entityID, ushort statusID)
    {
        for (var i = 0; i < trackedStatuses.Count; i++)
        {
            if (trackedStatuses[i].Key.EntityID != entityID || trackedStatuses[i].Key.StatusID != statusID)
                continue;

            trackedStatuses.RemoveAt(i);
            return;
        }
    }

    private void RebuildComboChains()
    {
        comboChainsCache.Clear();

        foreach (var statusConfig in config.MonitoredStatus.Values)
        {
            foreach (var actionID in statusConfig.BindActions)
            {
                var exists = false;

                for (var i = 0; i < comboChainsCache.Count; i++)
                {
                    if (comboChainsCache[i].ActionID != actionID)
                        continue;

                    exists = true;
                    break;
                }

                if (exists) continue;

                comboChainsCache.Add(new(actionID, FetchComboChain(actionID)));
            }
        }

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
        public bool                           KeepHighlight   = true;
        public Dictionary<uint, StatusConfig> MonitoredStatus = [];
    }

    private class StatusConfig
    {
        public uint[] BindActions   { get; set; } = [];
        public float  Countdown     { get; set; } = 4.0f;
        public bool   KeepHighlight { get; set; } = true;
    }

    private readonly record struct StatusKey
    (
        uint   EntityID,
        ushort StatusID
    );

    private struct StatusState
    (
        bool  active,
        float remainingTime
    )
    {
        public bool  Active        = active;
        public float RemainingTime = remainingTime;
    }

    private struct TrackedStatus
    (
        StatusKey   key,
        StatusState state
    )
    {
        public StatusKey   Key   = key;
        public StatusState State = state;
    }

    private struct ActionCalcEntry
    (
        uint  actionID,
        float remainingTime,
        float countdown,
        float score
    )
    {
        public readonly uint  ActionID      = actionID;
        public          float RemainingTime = remainingTime;
        public          float Countdown     = countdown;
        public          float Score         = score;
    }

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
