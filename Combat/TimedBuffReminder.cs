using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Newtonsoft.Json;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace DailyRoutines.ModulesPublic;

public class TimedBuffReminder : DailyModuleBase
{
    #region Core

    // info
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("TimedBuffReminderTitle"),
        Description = GetLoc("TimedBuffReminderDescription"),
        Category    = ModuleCategories.Combat,
        Author      = ["HaKu"]
    };

    // storage
    private static ModuleStorage? moduleConfig;

    // managers
    private static StatusMonitor statusService;

    public override void Init()
    {
        moduleConfig = LoadConfig<ModuleStorage>() ?? new ModuleStorage();

        // status monitor
        statusService = new StatusMonitor(moduleConfig.StatusStorage);

        // fetch remote resource
        Task.Run(async () => await RemoteRepoManager.FetchTimedBuffList());

        // highlight manager
        UseActionManager.RegPreUseActionLocation(OnPreUseAction);
        HighlightManager.Enable();

        // refresh highlight
        FrameworkManager.Register(OnFrameworkUpdateInterval, throttleMS: 200);
    }

    public override void Uninit()
    {
        UseActionManager.UnregPreUseActionLocation(OnPreUseAction);
        HighlightManager.Disable();
        FrameworkManager.Unregister(OnFrameworkUpdateInterval);

        base.Uninit();
    }

    public override void ConfigUI()
    {
        ImGui.TextColored(LightGreen, GetLoc("TimedBuffReminder-Threshold"));
        ImGui.Spacing();
        if (ImGui.SliderFloat("##ReminderThreshold", ref moduleConfig.StatusStorage.Threshold, 2.0f, 10.0f, "%.1f"))
            SaveConfig(moduleConfig);
    }

    #endregion

    #region Hooks

    private static unsafe void OnFrameworkUpdateInterval(IFramework _)
    {
        if (DService.ClientState.IsPvP || !DService.Condition[ConditionFlag.InCombat] || Control.GetLocalPlayer() is null)
            return;

        // update status
        statusService.Update();
    }

    private static void OnPreUseAction(
        ref bool  isPrevented, ref ActionType type,     ref uint actionId,
        ref ulong targetId,    ref Vector3    location, ref uint extraParam
    )
    {
        HighlightManager.HighlightActions.Remove(actionId);
        statusService.LastActionId = actionId;
    }

    #endregion

    #region RemoteCache

    private static class RemoteRepoManager
    {
        // const
        private const string Uri = "https://dr-cache.sumemo.dev";

        public static async Task FetchTimedBuffList()
        {
            try
            {
                var json = await HttpClientHelper.Get().GetStringAsync($"{Uri}/timed-buff");
                var resp = JsonConvert.DeserializeObject<List<StatusMonitor.Status>>(json);
                if (resp == null)
                    Error($"[TimedBuffReminder] 远程延续性状态文件解析失败: {json}");
                else
                {
                    moduleConfig.StatusStorage.Statuses = resp.ToArray();
                    statusService.InitStatusMap();
                }
            }
            catch (Exception ex) { Error($"[TimedBuffReminder] 远程延续性状态文件获取失败: {ex}"); }
        }
    }

    #endregion

    #region HighlightManager

    private static class HighlightManager
    {
        // cache
        public static readonly HashSet<uint> HighlightActions = [];

        #region Hooks

        // is action highlight
        private static readonly CompSig                                            isActionHighlightedSig = new("E8 ?? ?? ?? ?? 88 46 41 80 BF ?? ?? ?? ?? ?? ??");
        private static          Hook<ActionManager.Delegates.IsActionHighlighted>? isActionHighlightedHook;

        public static unsafe void Enable()
        {
            isActionHighlightedHook?.Dispose();
            isActionHighlightedHook = isActionHighlightedSig.GetHook<ActionManager.Delegates.IsActionHighlighted>(IsActionHighlightedDetour);
            isActionHighlightedHook.Enable();
        }

        public static void Disable()
            => isActionHighlightedHook?.Dispose();

        private static unsafe bool IsActionHighlightedDetour(ActionManager* actionManager, ActionType actionType, uint actionId)
            => HighlightActions.Contains(actionId) || isActionHighlightedHook!.Original(actionManager, actionType, actionId);

        #endregion
    }

    #endregion

    #region StatusMonitor

    private unsafe class StatusMonitor(StatusMonitor.Storage config)
    {
        // cache
        private         Dictionary<uint, Status>  statusDict       = [];
        public readonly Dictionary<Status, float> ActiveStatusList = [];
        public          uint                      LastActionId;

        // config
        public class Storage
        {
            public Status[] Statuses  = [];
            public float    Threshold = 3.0f;
        }

        #region Funcs

        public void InitStatusMap()
            => statusDict = config.Statuses.ToDictionary(x => x.StatusId, x => x);

        public void Update()
        {
            // clear cache
            Clear();

            // local player
            var localPlayer = Control.GetLocalPlayer();
            if (localPlayer == null)
                return;

            // status
            foreach (var status in localPlayer->StatusManager.Status)
            {
                if (statusDict.TryGetValue(status.StatusId, out var mitigation))
                    ActiveStatusList.TryAdd(mitigation, status.RemainingTime);
            }

            // battle npc
            var currentTarget = DService.Targets.Target;
            if (currentTarget is IBattleNpc battleNpc)
            {
                foreach (var status in battleNpc.ToBCStruct()->StatusManager.Status)
                {
                    if (statusDict.TryGetValue(status.StatusId, out var mitigation))
                        ActiveStatusList.TryAdd(mitigation, status.RemainingTime);
                }
            }

            // refresh highlight
            var manager = ActionManager.Instance();
            HighlightManager.HighlightActions.Clear();
            foreach (var status in ActiveStatusList)
            {
                foreach (var actionId in status.Key.ActionId)
                {
                    var actionChain = FetchComboChain(actionId);

                    var cutoff        = config.Threshold * actionChain.Length;
                    var notInChain    = actionChain.All(id => !manager->IsActionHighlighted(ActionType.Action, id));
                    var notLastAction = actionChain[..^1].All(id => id != LastActionId);

                    if (status.Value <= cutoff && notInChain && notLastAction)
                        HighlightManager.HighlightActions.Add(actionChain[0]);
                }
            }
        }

        public void Clear()
            => ActiveStatusList.Clear();

        #endregion

        public struct Status : IEquatable<Status>
        {
            [JsonProperty("status_id")]
            public uint StatusId { get; private set; }

            [JsonProperty("action_id")]
            public uint[] ActionId { get; private set; }

            [JsonProperty("name")]
            public string Name { get; private set; }

            #region Equals

            public bool Equals(Status other) => StatusId == other.StatusId;

            public override bool Equals(object? obj) => obj is Status other && Equals(other);

            public override int GetHashCode() => (int)StatusId;

            public static bool operator ==(Status left, Status right) => left.Equals(right);

            public static bool operator !=(Status left, Status right) => !left.Equals(right);

            #endregion
        }

        #region Funcs

        private static uint[] FetchComboChain(uint actionId)
        {
            var chain = new List<uint>();

            var cur = actionId;
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

        #endregion
    }

    #endregion

    #region Config

    private class ModuleStorage : ModuleConfiguration
    {
        // status
        public readonly StatusMonitor.Storage StatusStorage = new();
    }

    #endregion
}
