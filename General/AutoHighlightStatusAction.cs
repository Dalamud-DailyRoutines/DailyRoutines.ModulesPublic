using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DailyRoutines.Abstracts;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using LuminaAction = Lumina.Excel.Sheets.Action;
using Status = Lumina.Excel.Sheets.Status;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoHighlightStatusAction : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoHighlightStatusActionTitle"),
        Description = GetLoc("AutoHighlightStatusActionDescription"),
        Category    = ModuleCategories.General,
        Author      = ["HaKu"]
    };

    private static readonly CompSig IsActionHighlightedSig = new("E8 ?? ?? ?? ?? 88 47 41 80 BB C9 00 00 00 01");
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool IsActionHighlightedDelegate(ActionManager* actionManager, ActionType actionType, uint actionID);
    private static Hook<IsActionHighlightedDelegate>? IsActionHighlightedHook;

    private static Config ModuleConfig = null!;

    private static StatusSelectCombo? StatusCombo;
    private static ActionSelectCombo? ActionCombo;

    public static bool KeepHighlightAfterExpire = true;

    public static readonly HashSet<uint> ActionsToHighlight = [];

    private static uint LastActionID;

    private static Dictionary<uint, uint[]> ComboChainsCache = [];

    private static readonly Dictionary<uint, (float RemainingTime, float Countdown, float Score)> ActionCalculationCache = new(32);

    private static readonly Dictionary<StatusKey, StatusState> TrackedStatuses    = new(64);
    private static readonly Dictionary<StatusKey, int>         TrackedStatusIndex = new(64);
    private static readonly List<StatusKey>                    TrackedStatusKeys  = new(64);

    private static          long            LastUpdateTicks;
    private static readonly List<StatusKey> ResyncKeys = new(16);
    private static readonly List<StatusKey> RemoveKeys = new(16);

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        if (ModuleConfig.MonitoredStatus.Count == 0)
        {
            ModuleConfig.MonitoredStatus = StatusConfigs.ToDictionary(x => x.Key, x => x.Value);
            SaveConfig(ModuleConfig);
        }

        RebuildComboChains();

        StatusCombo ??= new("StatusCombo", PresetSheet.Statuses.Values);
        ActionCombo ??= new("ActionCombo", PresetSheet.PlayerActions.Values);

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

        ActionsToHighlight.Clear();
        ActionCalculationCache.Clear();
        TrackedStatuses.Clear();
        TrackedStatusIndex.Clear();
        TrackedStatusKeys.Clear();
        ResyncKeys.Clear();
        RemoveKeys.Clear();
        LastUpdateTicks = 0;
    }

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(100f * GlobalFontScale);
        if (ImGui.SliderFloat($"{GetLoc("AutoHighlightStatusAction-Countdown")}##ReminderThreshold", ref ModuleConfig.Countdown, 2.0f, 10.0f, "%.1f"))
            SaveConfig(ModuleConfig);
        ImGuiOm.HelpMarker(GetLoc("AutoHighlightStatusAction-Countdown-Help"));

        ImGui.SameLine(0, 5f * GlobalFontScale);
        if (ImGui.Checkbox($"{GetLoc("AutoHighlightStatusAction-KeepHighlightAfterExpire")}##KeepHighlightAfterExpire", ref KeepHighlightAfterExpire))
            SaveConfig(ModuleConfig);
        ImGuiOm.HelpMarker(GetLoc("AutoHighlightStatusAction-KeepHighlightAfterExpire-Help"));

        ImGui.Spacing();

        ImGui.SetNextItemWidth(300f * GlobalFontScale);
        using (ImRaii.PushId("Status"))
            StatusCombo.DrawRadio();
        ImGui.TextDisabled("↓");
        ImGui.SetNextItemWidth(300f * GlobalFontScale);
        using (ImRaii.PushId("Action"))
            ActionCombo.DrawCheckbox();

        ImGui.Spacing();

        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, GetLoc("Add")))
        {
            if (StatusCombo.SelectedID != 0 && ActionCombo.SelectedIDs.Count > 0)
            {
                ModuleConfig.MonitoredStatus[StatusCombo.SelectedID] = new StatusConfig
                {
                    BindActions   = ActionCombo.SelectedIDs.ToArray(),
                    Countdown     = ModuleConfig.Countdown,
                    KeepHighlight = KeepHighlightAfterExpire
                };
                ModuleConfig.Save(this);
                RebuildComboChains();
            }
        }

        ImGui.NewLine();

        using var table = ImRaii.Table("PlayersInList", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY, new(0, 200f * GlobalFontScale));
        if (!table)
            return;

        ImGui.TableSetupColumn("##Delete",                                                   ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn(GetLoc("Status"),                                             ImGuiTableColumnFlags.WidthStretch, 15);
        ImGui.TableSetupColumn(GetLoc("Action"),                                             ImGuiTableColumnFlags.WidthStretch, 40);
        ImGui.TableSetupColumn(GetLoc("AutoHighlightStatusAction-Countdown"),                ImGuiTableColumnFlags.WidthStretch, 20);
        ImGui.TableSetupColumn(GetLoc("AutoHighlightStatusAction-KeepHighlightAfterExpire"), ImGuiTableColumnFlags.WidthStretch, 20);

        ImGui.TableHeadersRow();

        foreach (var (status, statusConfig) in ModuleConfig.MonitoredStatus)
        {
            using var id = ImRaii.PushId($"{status}");

            ImGui.TableNextRow();

            ImGui.TableNextColumn();

            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.TrashAlt, GetLoc("Delete")))
            {
                ModuleConfig.MonitoredStatus.Remove(status);
                ModuleConfig.Save(this);
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
                StatusCombo.SelectedID = status;

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
                ActionCombo.SelectedIDs = statusConfig.BindActions.ToHashSet();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{statusConfig.Countdown:0.0}");
            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsItemClicked())
                ModuleConfig.Countdown = statusConfig.Countdown;

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(statusConfig.KeepHighlight ? GetLoc("Yes") : GetLoc("No"));
            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsItemClicked())
                KeepHighlightAfterExpire = statusConfig.KeepHighlight;
        }
    }

    private static void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.InCombat) return;

        ActionsToHighlight.Clear();
        ActionCalculationCache.Clear();
        TrackedStatuses.Clear();
        TrackedStatusIndex.Clear();
        TrackedStatusKeys.Clear();
        ResyncKeys.Clear();
        RemoveKeys.Clear();

        FrameworkManager.Instance().Unreg(OnUpdate);
        UseActionManager.Instance().Unreg(OnPostUseActionLocation);
        CharacterStatusManager.Instance().Unreg(OnGainStatus);
        CharacterStatusManager.Instance().Unreg(OnLoseStatus);

        if (!value || GameState.IsInPVPArea) return;

        LastUpdateTicks = Environment.TickCount64;
        SeedTrackedStatuses();
        
        FrameworkManager.Instance().Reg(OnUpdate, 1000);
        UseActionManager.Instance().RegPostUseActionLocation(OnPostUseActionLocation);
        CharacterStatusManager.Instance().RegGain(OnGainStatus);
        CharacterStatusManager.Instance().RegLose(OnLoseStatus);
    }

    [SkipLocalsInit]
    private static void OnUpdate(IFramework _)
    {
        if (GameState.IsInPVPArea || !DService.Instance().Condition[ConditionFlag.InCombat])
        {
            OnConditionChanged(ConditionFlag.InCombat, false);
            return;
        }

        var nowTicks = Environment.TickCount64;
        var dt       = (nowTicks - LastUpdateTicks) / 1000f;
        LastUpdateTicks = nowTicks;
        if (dt <= 0f) 
            dt = 1f;
        if (dt > 5f) 
            dt  = 5f;

        if (TargetManager.Target is not IBattleNPC { IsDead: false } battleNpcTarget)
        {
            ActionsToHighlight.Clear();
            return;
        }
        
        var currentTargetEntityID = battleNpcTarget.EntityID;
        ResyncKeys.Clear();
        RemoveKeys.Clear();

        foreach (var key in TrackedStatusKeys)
        {
            if (key.EntityID != currentTargetEntityID) continue;
            ref var state = ref CollectionsMarshal.GetValueRefOrNullRef(TrackedStatuses, key);
            if (Unsafe.IsNullRef(ref state))
                continue;

            if (!state.Active) continue;

            var remaining = state.RemainingTime - dt;
            state.RemainingTime = remaining;

            if (remaining <= 0f)
            {
                ResyncKeys.Add(key);
                continue;
            }

            if (TryGetStatusResyncCutoff(key.StatusID, out var cutoff) && remaining <= cutoff)
                ResyncKeys.Add(key);
        }

        foreach (var key in ResyncKeys)
        {
            if (key.EntityID != currentTargetEntityID) continue;
            ref var statusConfig = ref CollectionsMarshal.GetValueRefOrNullRef(ModuleConfig.MonitoredStatus, key.StatusID);

            if (Unsafe.IsNullRef(ref statusConfig))
            {
                RemoveKeys.Add(key);
                continue;
            }

            if (TryResyncStatus(key.EntityID, key.StatusID, out var remaining))
            {
                ref var state = ref CollectionsMarshal.GetValueRefOrNullRef(TrackedStatuses, key);

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
                    ref var state = ref CollectionsMarshal.GetValueRefOrNullRef(TrackedStatuses, key);

                    if (!Unsafe.IsNullRef(ref state))
                    {
                        state.Active        = false;
                        state.RemainingTime = 0f;
                    }
                }
                else
                    RemoveKeys.Add(key);
            }
        }

        foreach (var key in RemoveKeys)
            RemoveTrackedStatus(key);

        RemoveKeys.Clear();

        ActionCalculationCache.Clear();

        foreach (var key in TrackedStatusKeys)
        {
            if (key.EntityID != currentTargetEntityID) continue;
            ref var state = ref CollectionsMarshal.GetValueRefOrNullRef(TrackedStatuses, key);
            if (Unsafe.IsNullRef(ref state))
                continue;

            ref var statusConfig = ref CollectionsMarshal.GetValueRefOrNullRef(ModuleConfig.MonitoredStatus, key.StatusID);

            if (Unsafe.IsNullRef(ref statusConfig))
            {
                RemoveKeys.Add(key);
                continue;
            }

            var effectiveRemaining = state.Active ? state.RemainingTime : 0f;
            var countdown          = statusConfig.Countdown;

            var actions = statusConfig.BindActions;

            foreach (var actionID in actions)
            {
                ref var actionChain = ref CollectionsMarshal.GetValueRefOrNullRef(ComboChainsCache, actionID);
                if (Unsafe.IsNullRef(ref actionChain))
                    continue;

                var score = effectiveRemaining - countdown * actionChain.Length;

                ref var current = ref CollectionsMarshal.GetValueRefOrAddDefault(ActionCalculationCache, actionID, out var exists);
                if (!exists || score < current.Score)
                    current = (effectiveRemaining, countdown, score);
            }
        }

        foreach (var key in RemoveKeys)
            RemoveTrackedStatus(key);

        ActionsToHighlight.Clear();

        foreach (var (actionID, time) in ActionCalculationCache)
        {
            ref var actionChain = ref CollectionsMarshal.GetValueRefOrNullRef(ComboChainsCache, actionID);
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
                if (actionChain[i] == LastActionID)
                {
                    notLastAction = false;
                    break;
                }

            if (notLastAction)
                ActionsToHighlight.Add(actionChain[0]);
        }
    }

    private static void OnPostUseActionLocation
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

        ActionsToHighlight.Remove(actionID);
        LastActionID = actionID;
    }

    [return: MarshalAs(UnmanagedType.U1)]
    private static bool IsActionHighlightedDetour(ActionManager* actionManager, ActionType actionType, uint actionID) =>
        ActionsToHighlight.Contains(actionID) || IsActionHighlightedHook.Original(actionManager, actionType, actionID);

    private static void OnGainStatus(IBattleChara player, ushort statusID, ushort param, ushort stackCount, TimeSpan remainingTime, ulong sourceID)
    {
        if (sourceID                   != LocalPlayerState.EntityID ||
            remainingTime.TotalSeconds <= 0)
            return;

        ref var statusConfig = ref CollectionsMarshal.GetValueRefOrNullRef(ModuleConfig.MonitoredStatus, statusID);
        if (Unsafe.IsNullRef(ref statusConfig)) return;

        var key = new StatusKey(player.EntityID, statusID);
        AddOrUpdateTrackedStatus(key, new StatusState(true, (float)remainingTime.TotalSeconds));
    }

    private static void OnLoseStatus(IBattleChara player, ushort statusID, ushort param, ushort stackCount, ulong sourceID)
    {
        if (sourceID != LocalPlayerState.EntityID) return;

        ref var statusConfig = ref CollectionsMarshal.GetValueRefOrNullRef(ModuleConfig.MonitoredStatus, statusID);

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

    private static void SeedTrackedStatuses()
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

                ref var statusConfig = ref CollectionsMarshal.GetValueRefOrNullRef(ModuleConfig.MonitoredStatus, status.StatusId);
                if (Unsafe.IsNullRef(ref statusConfig)) continue;

                var key = new StatusKey(battleChara.EntityID, status.StatusId);
                AddOrUpdateTrackedStatus(key, new StatusState(true, status.RemainingTime));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetStatusResyncCutoff(ushort statusID, out float cutoff)
    {
        ref var statusConfig = ref CollectionsMarshal.GetValueRefOrNullRef(ModuleConfig.MonitoredStatus, statusID);

        if (Unsafe.IsNullRef(ref statusConfig))
        {
            cutoff = 0f;
            return false;
        }

        var actions     = statusConfig.BindActions;
        var maxChainLen = 0;

        foreach (var actionID in actions)
        {
            ref var actionChain = ref CollectionsMarshal.GetValueRefOrNullRef(ComboChainsCache, actionID);
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
    private static void AddOrUpdateTrackedStatus(StatusKey key, StatusState state)
    {
        if (TrackedStatuses.TryGetValue(key, out _))
        {
            TrackedStatuses[key] = state;
            return;
        }

        TrackedStatuses.Add(key, state);
        TrackedStatusIndex.Add(key, TrackedStatusKeys.Count);
        TrackedStatusKeys.Add(key);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RemoveTrackedStatus(StatusKey key)
    {
        if (!TrackedStatuses.Remove(key))
            return;

        if (!TrackedStatusIndex.Remove(key, out var index))
            return;

        var lastIndex = TrackedStatusKeys.Count - 1;

        if ((uint)index < (uint)lastIndex)
        {
            var lastKey = TrackedStatusKeys[lastIndex];
            TrackedStatusKeys[index]    = lastKey;
            TrackedStatusIndex[lastKey] = index;
        }

        TrackedStatusKeys.RemoveAt(lastIndex);
    }

    private static void RebuildComboChains()
    {
        var newCache = new Dictionary<uint, uint[]>(ModuleConfig.MonitoredStatus.Count * 2);

        foreach (var config in ModuleConfig.MonitoredStatus.Values)
        {
            foreach (var actionID in config.BindActions)
            {
                if (newCache.ContainsKey(actionID)) continue;
                newCache[actionID] = FetchComboChain(actionID);
            }
        }

        ComboChainsCache = newCache;
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

    private class Config : ModuleConfiguration
    {
        public Dictionary<uint, StatusConfig> MonitoredStatus = [];

        public float Countdown = 4;
    }


    private static readonly Dictionary<uint, StatusConfig> StatusConfigs = new()
    {

        [838]  = new StatusConfig { BindActions = [3599], Countdown  = 4.0f, KeepHighlight  = true },
        [843]  = new StatusConfig { BindActions = [3608], Countdown  = 4.0f, KeepHighlight  = true },
        [1881] = new StatusConfig { BindActions = [16554], Countdown = 4.0f, KeepHighlight  = true },
        [1248] = new StatusConfig { BindActions = [8324], Countdown  = 10.0f, KeepHighlight = false },

        [143]  = new StatusConfig { BindActions = [121], Countdown   = 4.0f, KeepHighlight = true },
        [144]  = new StatusConfig { BindActions = [132], Countdown   = 4.0f, KeepHighlight = true },
        [1871] = new StatusConfig { BindActions = [16532], Countdown = 4.0f, KeepHighlight = true },

        [2614] = new StatusConfig { BindActions = [24290], Countdown = 6.0f, KeepHighlight = true },
        [2615] = new StatusConfig { BindActions = [24290], Countdown = 6.0f, KeepHighlight = true },
        [2616] = new StatusConfig { BindActions = [24290], Countdown = 6.0f, KeepHighlight = true },

        [179]  = new StatusConfig { BindActions = [17864], Countdown = 4.0f, KeepHighlight = true },
        [189]  = new StatusConfig { BindActions = [17865], Countdown = 4.0f, KeepHighlight = true },
        [1895] = new StatusConfig { BindActions = [16540], Countdown = 4.0f, KeepHighlight = true },

        [124]  = new StatusConfig { BindActions = [100], Countdown        = 4.0f, KeepHighlight = true },
        [1200] = new StatusConfig { BindActions = [7406, 3560], Countdown = 4.0f, KeepHighlight = true },
        [129]  = new StatusConfig { BindActions = [113], Countdown        = 4.0f, KeepHighlight = true },
        [1201] = new StatusConfig { BindActions = [7407, 3560], Countdown = 4.0f, KeepHighlight = true },

        [1299] = new StatusConfig { BindActions = [7485], Countdown  = 4.0f, KeepHighlight = true },
        [2719] = new StatusConfig { BindActions = [25772], Countdown = 4.0f, KeepHighlight = true },

        [2677] = new StatusConfig { BindActions = [45], Countdown = 4.0f, KeepHighlight = true }
    };

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
}
