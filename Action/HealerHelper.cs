using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.DutyState;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using Newtonsoft.Json;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;
using LuminaAction = Lumina.Excel.Sheets.Action;
using NotifyHelper = OmenTools.OmenService.NotifyHelper;

namespace DailyRoutines.ModulesPublic;

public class HealerHelper : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("HealerHelperTitle"),
        Description = Lang.Get("HealerHelperDescription"),
        Category    = ModuleCategory.Action,
        Author      = ["HaKu"]
    };

    private Config              config = null!;
    private EasyHealManager     easyHealService;
    private AutoPlayCardManager autoPlayCardService;
    private ActionSelectCombo?  actionSelect;

    protected override void Init()
    {
        config              = Config.Load(this) ?? new();
        easyHealService     = new(this, config.EasyHealStorage);
        autoPlayCardService = new(this, config.AutoPlayCardStorage);

        Task.Run(async () => await FetchAll());

        UseActionManager.Instance().RegPreUseActionLocation(OnPreUseAction);
        DService.Instance().DutyState.DutyRecommenced    += OnDutyRecommenced;
        DService.Instance().DutyState.DutyStarted        += OnDutyStarted;
        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        DService.Instance().Condition.ConditionChange    += OnConditionChanged;

        if (GameState.ContentFinderCondition != 0 && DService.Instance().DutyState.IsDutyStarted)
            OnDutyStarted(null);
    }

    protected override void Uninit()
    {
        UseActionManager.Instance().Unreg(OnPreUseAction);
        DService.Instance().DutyState.DutyRecommenced    -= OnDutyRecommenced;
        DService.Instance().DutyState.DutyStarted        -= OnDutyStarted;
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        DService.Instance().Condition.ConditionChange    -= OnConditionChanged;
    }

    #region Utils

    private void NotifyTargetChange(IBattleChara gameObject, string locKeySuffix)
    {
        var name = gameObject.Name.ToString();
        var job  = gameObject.ClassJob.Value;

        if (config.SendChat)
            NotifyHelper.Instance().Chat(Lang.GetSe(locKeySuffix, name, job.ToBitmapFontIcon(), job.Name));
        if (config.SendNotification)
            NotifyHelper.Instance().NotificationInfo(Lang.Get(locKeySuffix, name, string.Empty, job.Name));
    }

    #endregion

    #region RemoteCache

    private async Task FetchPlayCardOrder()
    {
        if (autoPlayCardService.DefaultCardOrderLoaded) return;

        try
        {
            var json = await HTTPClientHelper.Instance().Get().GetStringAsync($"{REMOTE_URI}/card-order.json");
            var resp = JsonConvert.DeserializeObject<AutoPlayCardManager.PlayCardOrder>(json);

            if (resp != null)
            {
                autoPlayCardService.InitDefaultCardOrder(resp);
                if (!autoPlayCardService.CustomCardOrderLoaded)
                    autoPlayCardService.InitCustomCardOrder();
            }
        }
        catch
        {
            // ignored
        }
    }

    private async Task FetchHealActions()
    {
        if (easyHealService.TargetHealActionsLoaded) return;

        try
        {
            var json = await HTTPClientHelper.Instance().Get().GetStringAsync($"{REMOTE_URI}/heal-action.json");
            var resp = JsonConvert.DeserializeObject<Dictionary<string, List<EasyHealManager.HealAction>>>(json);

            if (resp != null)
            {
                easyHealService.InitTargetHealActions(resp.SelectMany(kv => kv.Value).ToDictionary(act => act.ID, act => act));
                if (!easyHealService.ActiveHealActionsLoaded)
                    easyHealService.InitActiveHealActions();
            }
        }
        catch
        {
            /* ignored */
        }
    }

    private async Task FetchAll()
    {
        try
        {
            await Task.WhenAll(FetchPlayCardOrder(), FetchHealActions());
        }
        catch
        {
            /* ignored */
        }
        finally
        {
            actionSelect ??= new("##ActionSelect", LuminaGetter.Get<LuminaAction>().Where(x => easyHealService.TargetHealActions.ContainsKey(x.RowId)));
            if (config.EasyHealStorage.ActiveHealActions.Count == 0)
                easyHealService.InitActiveHealActions();
            actionSelect.SelectedIDs = config.EasyHealStorage.ActiveHealActions;
        }
    }

    #endregion

    private class AutoPlayCardManager
    (
        HealerHelper                module,
        AutoPlayCardManager.Storage config
    )
    {
        public DutySection CurrentDutySection { get; set; }
        public DateTime    StartTimeUTC       { get; set; }

        public bool DefaultCardOrderLoaded => config.DefaultCardOrder.Melee.Count > 0 && config.DefaultCardOrder.Range.Count > 0;
        public bool CustomCardOrderLoaded  => config.CustomCardOrder.Melee.Count  > 0 && config.CustomCardOrder.Range.Count  > 0;

        public bool IsOpener =>
            (StandardTimeManager.Instance().UTCNow - StartTimeUTC).TotalSeconds > 90;
        
        private readonly List<(uint id, double priority)> meleeCandidateOrder = [];
        private readonly List<(uint id, double priority)> rangeCandidateOrder = [];

        public void InitDefaultCardOrder(PlayCardOrder order)
        {
            config.DefaultCardOrder = order;
            OrderCandidates();
        }

        public void InitCustomCardOrder(string role = "All", string section = "All")
        {
            if (role is "Melee" or "All")
            {
                if (section is "opener" or "All")
                    config.CustomCardOrder.Melee["opener"] = config.DefaultCardOrder.Melee["opener"].ToArray();
                if (section is "2m+" or "All")
                    config.CustomCardOrder.Melee["2m+"] = config.DefaultCardOrder.Melee["2m+"].ToArray();
            }

            if (role is "Range" or "All")
            {
                if (section is "opener" or "All")
                    config.CustomCardOrder.Range["opener"] = config.DefaultCardOrder.Range["opener"].ToArray();
                if (section is "2m+" or "All")
                    config.CustomCardOrder.Range["2m+"] = config.DefaultCardOrder.Range["2m+"].ToArray();
            }

            OrderCandidates();
        }

        public unsafe void OrderCandidates()
        {
            meleeCandidateOrder.Clear();
            rangeCandidateOrder.Clear();

            var partyList = AgentHUD.Instance()->PartyMembers.ToArray();
            var isAST     = LocalPlayerState.ClassJob == 33;
            if (GameState.IsInPVPArea || partyList.Length < 2 || !isAST || config.AutoPlayCard == AutoPlayCardStatus.Disable) return;

            var sectionLabel  = IsOpener ? "opener" : "2m+";
            var activateOrder = config.AutoPlayCard == AutoPlayCardStatus.Custom ? config.CustomCardOrder : config.DefaultCardOrder;

            ProcessRoleCandidates(partyList, activateOrder.Melee[sectionLabel], meleeCandidateOrder, 3);
            ProcessRoleCandidates(partyList, activateOrder.Range[sectionLabel], rangeCandidateOrder, 2);

            meleeCandidateOrder.Sort((a, b) => b.priority.CompareTo(a.priority));
            rangeCandidateOrder.Sort((a, b) => b.priority.CompareTo(a.priority));
        }

        private static unsafe void ProcessRoleCandidates(HudPartyMember[] partyList, string[] order, List<(uint id, double priority)> candidates, int fallbackRole)
        {
            for (var idx = 0; idx < order.Length; idx++)
            {
                var member = partyList.FirstOrDefault(m => m.Object != null && m.Object->ClassJob.ToLuminaRowRef<ClassJob>().Value.NameEnglish == order[idx]);
                if (member.EntityId != 0 && candidates.All(m => m.id != member.EntityId))
                    candidates.Add((member.EntityId, 5 - idx * 0.1));
            }

            if (candidates.Count == 0)
            {
                var fallback = partyList.FirstOrDefault(m => m.Object != null && m.Object->ClassJob.ToLuminaRowRef<ClassJob>().Value.Role == fallbackRole);
                if (fallback.EntityId != 0)
                    candidates.Add((fallback.EntityId, 1));
            }
        }

        private unsafe BattleChara* FetchCandidateObject(string role)
        {
            var          candidates       = role == "Melee" ? meleeCandidateOrder : rangeCandidateOrder;
            BattleChara* fallbackObj      = null;
            var          fallbackPriority = 0.0;

            var actionRange = MathF.Pow(ActionManager.GetActionRange(37023), 2);

            foreach (var member in candidates)
            {
                var candidate = AgentHUD.Instance()->PartyMembers.ToArray().FirstOrDefault(m => m.EntityId == member.id);
                var obj       = candidate.Object;

                if (candidate.EntityId == 0    ||
                    obj                == null ||
                    obj->IsDead()              ||
                    obj->Health <= 0)
                    continue;

                if (Vector3.DistanceSquared(LocalPlayerState.Object.Position, obj->Position) >= actionRange)
                    continue;

                if (obj->IsMounted()                                  ||
                    obj->MovementState != MovementStateOptions.Normal ||
                    obj->StatusManager.HasStatus(43)                  ||
                    obj->StatusManager.HasStatus(44)) // Weakness
                {
                    fallbackObj      = candidate.Object;
                    fallbackPriority = member.priority;
                    continue;
                }

                if (member.priority >= fallbackPriority - 2)
                    return candidate.Object;
            }

            return fallbackObj;
        }

        public unsafe void OnPrePlayCard(ref ulong targetID, ref uint actionID)
        {
            if (targetID != UNSPECIFIC_TARGET_ID) return;

            var finalTarget = actionID == 37023 ? FetchCandidateObject("Melee") : FetchCandidateObject("Range");
            if (finalTarget == null || finalTarget->EntityId == targetID) return;

            targetID = finalTarget->EntityId;
            module.NotifyTargetChange
            (
                IBattleChara.Create((nint)finalTarget),
                actionID == 37023 ? "HealerHelper-AutoPlayCard-Message-Melee" : "HealerHelper-AutoPlayCard-Message-Range"
            );
        }

        public class Storage
        {
            public readonly PlayCardOrder      CustomCardOrder  = new();
            public          AutoPlayCardStatus AutoPlayCard     = AutoPlayCardStatus.Default;
            public          PlayCardOrder      DefaultCardOrder = new();
        }

        public class PlayCardOrder
        {
            [JsonProperty("melee")]
            public Dictionary<string, string[]> Melee { get; private set; } = new();

            [JsonProperty("range")]
            public Dictionary<string, string[]> Range { get; private set; } = new();
        }
        
        public enum AutoPlayCardStatus
        {
            Disable,
            Default,
            Custom
        }

        public enum DutySection
        {
            Enter,
            Start
        }

        #region 常量

        public static readonly FrozenSet<uint> PlayCardActions = [37023, 37026];

        #endregion
    }

    private class EasyHealManager
    (
        HealerHelper            module,
        EasyHealManager.Storage config
    )
    {
        public bool                         TargetHealActionsLoaded => config.TargetHealActions.Count > 0;
        public bool                         ActiveHealActionsLoaded => config.ActiveHealActions.Count > 0;
        public Dictionary<uint, HealAction> TargetHealActions       => config.TargetHealActions;

        public void InitTargetHealActions(Dictionary<uint, HealAction> actions) =>
            config.TargetHealActions = actions;

        public void InitActiveHealActions() =>
            config.ActiveHealActions = config.TargetHealActions
                                             .Where(act => act.Value.On)
                                             .Select(act => act.Key)
                                             .ToHashSet();

        private unsafe BattleChara* TargetNeedHealObject(uint actionID)
        {
            var          lowRatio   = 2f;
            BattleChara* bestTarget = null;

            foreach (var member in AgentHUD.Instance()->PartyMembers)
            {
                if (member.EntityId == 0 || member.Object == null) continue;

                var obj = member.Object;

                if (obj->IsDead()    ||
                    obj->Health <= 0 ||
                    ActionManager.GetActionInRangeOrLoS
                    (
                        actionID,
                        (GameObject*)Control.GetLocalPlayer(),
                        (GameObject*)member.Object
                    ) !=
                    0)
                    continue;

                var ratio = obj->Health / (float)obj->MaxHealth;

                if (ratio < lowRatio && ratio <= config.NeedHealThreshold)
                {
                    lowRatio   = ratio;
                    bestTarget = member.Object;
                }
            }

            return bestTarget;
        }

        private static unsafe BattleChara* FindTarget(uint actionID, Func<HudPartyMember, bool> predicate, bool reverse = false)
        {
            var members = AgentHUD.Instance()->PartyMembers.ToArray();
            var source  = reverse ? members.Reverse() : members;

            foreach (var member in source)
            {
                if (member.EntityId == 0    ||
                    member.Object   == null ||
                    ActionManager.GetActionInRangeOrLoS
                    (
                        actionID,
                        (GameObject*)Control.GetLocalPlayer(),
                        (GameObject*)member.Object
                    ) !=
                    0)
                    continue;

                if (predicate(member))
                    return member.Object;
            }

            return null;
        }

        public unsafe void OnPreHeal(ref ulong targetID, ref uint actionID, ref bool isPrevented)
        {
            if (targetID != UNSPECIFIC_TARGET_ID && IsHealable(DService.Instance().ObjectTable.SearchByID(targetID))) return;

            var needHealObject = TargetNeedHealObject(actionID);

            if (needHealObject != null && needHealObject->EntityId != targetID)
            {
                targetID = needHealObject->EntityId;

                module.NotifyTargetChange(IBattleChara.Create((nint)needHealObject), "HealerHelper-EasyHeal-Message");
                return;
            }

            switch (config.OverhealTarget)
            {
                case OverhealTarget.Prevent:
                    isPrevented = true;
                    return;

                case OverhealTarget.Local:
                    targetID = LocalPlayerState.EntityID;
                    module.NotifyTargetChange(LocalPlayerState.Object, "HealerHelper-EasyHeal-Message");
                    return;

                case OverhealTarget.FirstTank:
                {
                    var tanks = AgentHUD.Instance()->PartyMembers
                                .ToArray()
                                .Where(x => x.Object                                                             != null)
                                .OrderByDescending(x => x.Object->ClassJob.ToLuminaRowRef<ClassJob>().Value.Role == 1)
                                .ToList();

                    foreach (var info in tanks)
                    {
                        if (info.Object == null ||
                            ActionManager.GetActionInRangeOrLoS
                            (
                                actionID,
                                (GameObject*)Control.GetLocalPlayer(),
                                (GameObject*)info.Object
                            ) !=
                            0)
                            continue;

                        targetID = info.EntityId;
                        module.NotifyTargetChange(IBattleChara.Create((nint)info.Object), "HealerHelper-EasyHeal-Message");
                        return;
                    }

                    break;
                }

                default:
                    return;
            }
        }

        public unsafe void OnPreDispel(ref ulong targetID)
        {
            if (targetID != UNSPECIFIC_TARGET_ID) return;

            if (LocalPlayerState.Object.StatusList.Any(s => Sheets.DispellableStatuses.ContainsKey(s.StatusID)))
            {
                targetID = LocalPlayerState.EntityID;
                module.NotifyTargetChange(LocalPlayerState.Object, "HealerHelper-EasyDispel-Message");
            }
            else
            {
                var obj = FindTarget
                (
                    7568,
                    m =>
                    {
                        if (m.Object->IsDead() || m.Object->Health <= 0) return false;

                        foreach (var s in m.Object->StatusManager.Status)
                        {
                            if (Sheets.DispellableStatuses.ContainsKey(s.StatusId))
                                return true;
                        }

                        return false;
                    },
                    config.DispelOrder == DispelOrderStatus.Reverse
                );

                if (obj != null)
                {
                    targetID = obj->EntityId;
                    module.NotifyTargetChange(IBattleChara.Create((nint)obj), "HealerHelper-EasyDispel-Message");
                }
            }
        }

        public unsafe void OnPreRaise(ref ulong targetID, ref uint actionID)
        {
            if (targetID != UNSPECIFIC_TARGET_ID) return;

            var obj = FindTarget
            (
                actionID,
                m =>
                {
                    var obj = m.Object;
                    return (obj->IsDead() || obj->Health <= 0) && !obj->StatusManager.HasStatus(148);
                },
                config.RaiseOrder == RaiseOrderStatus.Reverse
            );

            if (obj == null || obj->EntityId == targetID) return;

            targetID = obj->EntityId;
            module.NotifyTargetChange(IBattleChara.Create((nint)obj), "HealerHelper-EasyRaise-Message");
        }

        public static unsafe bool IsHealable(IGameObject? gameObject) =>
            ActionManager.CanUseActionOnTarget(3595, gameObject.ToStruct());

        public class Storage
        {
            public HashSet<uint>                ActiveHealActions = [];
            public DispelOrderStatus            DispelOrder       = DispelOrderStatus.Order;
            public EasyDispelStatus             EasyDispel        = EasyDispelStatus.Enable;
            public EasyHealStatus               EasyHeal          = EasyHealStatus.Enable;
            public EasyRaiseStatus              EasyRaise         = EasyRaiseStatus.Enable;
            public float                        NeedHealThreshold = 0.92f;
            public OverhealTarget               OverhealTarget    = OverhealTarget.Local;
            public RaiseOrderStatus             RaiseOrder        = RaiseOrderStatus.Order;
            public Dictionary<uint, HealAction> TargetHealActions = [];
        }

        public class HealAction
        {
            [JsonProperty("id")]
            public uint ID;

            [JsonProperty("name")]
            public string Name;

            [JsonProperty("on")]
            public bool On;
        }
        
        public enum DispelOrderStatus
        {
            Order,
            Reverse
        }

        public enum EasyDispelStatus
        {
            Disable,
            Enable
        }

        public enum EasyHealStatus
        {
            Disable,
            Enable
        }

        public enum EasyRaiseStatus
        {
            Disable,
            Enable
        }

        public enum OverhealTarget
        {
            Local,
            FirstTank,
            Prevent
        }

        public enum RaiseOrderStatus
        {
            Order,
            Reverse
        }

        #region 常量

        public static readonly FrozenSet<uint> RaiseActions = [125, 173, 3603, 24287, 7670, 7523, 64556];

        #endregion
    }

    #region Config

    private class Config : ModuleConfig
    {
        public AutoPlayCardManager.Storage AutoPlayCardStorage = new();
        public EasyHealManager.Storage     EasyHealStorage     = new();
        public bool                        SendChat;
        public bool                        SendNotification = true;
    }

    #endregion

    #region UI

    private static int? CustomCardOrderDragIndex;

    protected override void ConfigUI()
    {
        AutoPlayCardUI();
        ImGui.NewLine();
        EasyHealUI();
        ImGui.NewLine();
        EasyDispelUI();
        ImGui.NewLine();
        EasyRaiseUI();
        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Notification"));
        ImGui.Spacing();

        using (ImRaii.PushIndent())
        {
            if (ImGui.Checkbox(Lang.Get("SendChat"), ref config.SendChat))
                config.Save(this);
            if (ImGui.Checkbox(Lang.Get("SendNotification"), ref config.SendNotification))
                config.Save(this);
        }
    }

    private void AutoPlayCardUI()
    {
        var playCardStorage = config.AutoPlayCardStorage;
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("HealerHelper-AutoPlayCardTitle"));
        ImGuiOm.HelpMarker(Lang.Get("HealerHelper-EasyRedirectDescription", LuminaWrapper.GetActionName(17055)));
        ImGui.Spacing();

        using (ImRaii.PushIndent())
        {
            DrawConfigRadio($"{Lang.Get("Disable")}##autocard", playCardStorage.AutoPlayCard, AutoPlayCardManager.AutoPlayCardStatus.Disable, v => playCardStorage.AutoPlayCard = v);
            DrawConfigRadio
            (
                $"{Lang.Get("Common")} ({Lang.Get("HealerHelper-AutoPlayCard-CommonDescription")})",
                playCardStorage.AutoPlayCard,
                AutoPlayCardManager.AutoPlayCardStatus.Default,
                v => playCardStorage.AutoPlayCard = v
            );
            DrawConfigRadio
            (
                $"{Lang.Get("Custom")} ({Lang.Get("HealerHelper-AutoPlayCard-CustomDescription")})",
                playCardStorage.AutoPlayCard,
                AutoPlayCardManager.AutoPlayCardStatus.Custom,
                v => playCardStorage.AutoPlayCard = v
            );

            if (playCardStorage.AutoPlayCard == AutoPlayCardManager.AutoPlayCardStatus.Custom)
            {
                ImGui.Spacing();
                CustomCardUI();
            }
        }
    }

    private void CustomCardUI()
    {
        var playCardStorage = config.AutoPlayCardStorage;
        DrawCustomCardSection("HealerHelper-AutoPlayCard-MeleeOpener", playCardStorage.CustomCardOrder.Melee["opener"], "Melee", "opener", "meleeopener");
        DrawCustomCardSection("HealerHelper-AutoPlayCard-Melee2Min",   playCardStorage.CustomCardOrder.Melee["2m+"],    "Melee", "2m+",    "melee2m");
        DrawCustomCardSection("HealerHelper-AutoPlayCard-RangeOpener", playCardStorage.CustomCardOrder.Range["opener"], "Range", "opener", "rangeopener");
        DrawCustomCardSection("HealerHelper-AutoPlayCard-Range2Min",   playCardStorage.CustomCardOrder.Range["2m+"],    "Range", "2m+",    "range2m");
        this.config.Save(this);
    }

    private void DrawCustomCardSection(string titleKey, string[] order, string role, string section, string resetKeySuffix)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightYellow.ToVector4(), Lang.Get(titleKey));

        if (CustomCardOrderUI(order))
        {
            config.Save(this);
            autoPlayCardService.OrderCandidates();
        }

        ImGui.SameLine();
        ImGuiOm.ScaledDummy(5, 0);
        ImGui.SameLine();

        if (ImGui.Button($"{Lang.Get("Reset")}##{resetKeySuffix}"))
        {
            autoPlayCardService.InitCustomCardOrder(role, section);
            config.Save(this);
        }

        ImGui.Spacing();
    }

    private static bool CustomCardOrderUI(string[] cardOrder)
    {
        var modified = false;

        for (var index = 0; index < cardOrder.Length; index++)
        {
            using var id       = ImRaii.PushId($"{index}");
            var       jobName  = JobNameMap[cardOrder[index]].ToString();
            var       textSize = ImGui.CalcTextSize(jobName);
            ImGui.Button(jobName, new(textSize.X + 20f, 0));

            if (index != cardOrder.Length - 1)
                ImGui.SameLine();

            if (ImGui.BeginDragDropSource())
            {
                CustomCardOrderDragIndex = index;
                ImGui.SetDragDropPayload("##CustomCardOrder", []);
                ImGui.EndDragDropSource();
            }

            if (ImGui.BeginDragDropTarget())
            {
                ImGui.AcceptDragDropPayload("##CustomCardOrder");

                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && CustomCardOrderDragIndex.HasValue)
                {
                    (cardOrder[index], cardOrder[CustomCardOrderDragIndex.Value]) = (cardOrder[CustomCardOrderDragIndex.Value], cardOrder[index]);
                    modified                                                      = true;
                }

                ImGui.EndDragDropTarget();
            }
        }

        return modified;
    }

    private void EasyHealUI()
    {
        var easyHealStorage = config.EasyHealStorage;
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("HealerHelper-EasyHealTitle"));
        ImGuiOm.HelpMarker(Lang.Get("HealerHelper-EasyRedirectDescription", Lang.Get("HealerHelper-SingleTargetHeal")));
        ImGui.Spacing();

        using (ImRaii.PushIndent())
        {
            DrawConfigRadio($"{Lang.Get("Disable")}##easyheal", easyHealStorage.EasyHeal, EasyHealManager.EasyHealStatus.Disable, v => easyHealStorage.EasyHeal = v);
            DrawConfigRadio
            (
                $"{Lang.Get("Enable")} ({Lang.Get("HealerHelper-EasyHeal-EnableDescription")})",
                easyHealStorage.EasyHeal,
                EasyHealManager.EasyHealStatus.Enable,
                v => easyHealStorage.EasyHeal = v
            );

            if (easyHealStorage.EasyHeal == EasyHealManager.EasyHealStatus.Enable)
            {
                ImGui.Spacing();
                ActiveHealActionsSelect();
                ImGui.Spacing();

                ImGui.TextColored(KnownColor.LightGreen.ToVector4(), Lang.Get("HealerHelper-EasyHeal-HealThreshold"));
                ImGuiOm.HelpMarker(Lang.Get("HealerHelper-EasyHeal-HealThresholdHelp"));
                ImGui.Spacing();

                if (ImGui.SliderFloat("##HealThreshold", ref easyHealStorage.NeedHealThreshold, 0.0f, 1.0f, "%.2f"))
                    this.config.Save(this);

                if (easyHealStorage.NeedHealThreshold > 0.92f)
                {
                    ImGui.Spacing();
                    ImGui.TextColored(KnownColor.Orange.ToVector4(), Lang.Get("HealerHelper-EasyHeal-OverhealWarning"));
                }

                ImGui.Spacing();
                ImGui.TextColored(KnownColor.LightYellow.ToVector4(), Lang.Get("HealerHelper-EasyHeal-OverhealTargetDescription"));
                ImGui.Spacing();

                DrawConfigRadio
                (
                    $"{Lang.Get("HealerHelper-EasyHeal-OverhealTarget-Prevent")}##overhealtarget",
                    easyHealStorage.OverhealTarget,
                    EasyHealManager.OverhealTarget.Prevent,
                    v => easyHealStorage.OverhealTarget = v
                );
                ImGui.SameLine();
                ImGuiOm.ScaledDummy(5, 0);
                ImGui.SameLine();
                DrawConfigRadio
                (
                    $"{Lang.Get("HealerHelper-EasyHeal-OverhealTarget-Local")}##overhealtarget",
                    easyHealStorage.OverhealTarget,
                    EasyHealManager.OverhealTarget.Local,
                    v => easyHealStorage.OverhealTarget = v
                );
                ImGui.SameLine();
                ImGuiOm.ScaledDummy(5, 0);
                ImGui.SameLine();
                DrawConfigRadio
                (
                    $"{Lang.Get("HealerHelper-EasyHeal-OverhealTarget-FirstTank")}##overhealtarget",
                    easyHealStorage.OverhealTarget,
                    EasyHealManager.OverhealTarget.FirstTank,
                    v => easyHealStorage.OverhealTarget = v
                );
            }
        }
    }

    private void ActiveHealActionsSelect()
    {
        ImGui.TextColored(KnownColor.YellowGreen.ToVector4(), $"{Lang.Get("HealerHelper-EasyHeal-ActiveHealAction")}");
        ImGui.Spacing();

        if (actionSelect.DrawCheckbox())
        {
            config.EasyHealStorage.ActiveHealActions = actionSelect.SelectedIDs;
            config.Save(this);
        }

        ImGui.SameLine();
        ImGuiOm.ScaledDummy(5, 0);
        ImGui.SameLine();

        if (ImGui.Button($"{Lang.Get("Reset")}##activehealactions"))
        {
            easyHealService.InitActiveHealActions();
            config.Save(this);
        }
    }

    private void EasyDispelUI()
    {
        var easyHealStorage = config.EasyHealStorage;
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("HealerHelper-EasyDispelTitle"));
        ImGuiOm.HelpMarker(Lang.Get("HealerHelper-EasyRedirectDescription", LuminaWrapper.GetActionName(7568)));
        ImGui.Spacing();

        using (ImRaii.PushIndent())
        {
            DrawConfigRadio($"{Lang.Get("Disable")}##easydispel", easyHealStorage.EasyDispel, EasyHealManager.EasyDispelStatus.Disable, v => easyHealStorage.EasyDispel = v);

            using (ImRaii.Group())
            {
                if (ImGui.RadioButton
                    (
                        $"{Lang.Get("Enable")} [{Lang.Get("InOrder")}]##easydispel",
                        easyHealStorage is { EasyDispel: EasyHealManager.EasyDispelStatus.Enable, DispelOrder: EasyHealManager.DispelOrderStatus.Order }
                    ))
                {
                    easyHealStorage.EasyDispel  = EasyHealManager.EasyDispelStatus.Enable;
                    easyHealStorage.DispelOrder = EasyHealManager.DispelOrderStatus.Order;
                    this.config.Save(this);
                }

                if (ImGui.RadioButton
                    (
                        $"{Lang.Get("Enable")} [{Lang.Get("InReverseOrder")}]##easydispel",
                        easyHealStorage is { EasyDispel: EasyHealManager.EasyDispelStatus.Enable, DispelOrder: EasyHealManager.DispelOrderStatus.Reverse }
                    ))
                {
                    easyHealStorage.EasyDispel  = EasyHealManager.EasyDispelStatus.Enable;
                    easyHealStorage.DispelOrder = EasyHealManager.DispelOrderStatus.Reverse;
                    this.config.Save(this);
                }
            }

            ImGuiOm.TooltipHover(Lang.Get("HealerHelper-OrderHelp"), 20f * GlobalUIScale);
        }
    }

    private void EasyRaiseUI()
    {
        var easyHealStorage = config.EasyHealStorage;
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("HealerHelper-EasyRaiseTitle"));
        ImGui.Spacing();

        using (ImRaii.PushIndent())
        {
            DrawConfigRadio($"{Lang.Get("Disable")}##easyraise", easyHealStorage.EasyRaise, EasyHealManager.EasyRaiseStatus.Disable, v => easyHealStorage.EasyRaise = v);

            using (ImRaii.Group())
            {
                if (ImGui.RadioButton
                    (
                        $"{Lang.Get("Enable")} [{Lang.Get("InOrder")}]##easyraise",
                        easyHealStorage is { EasyRaise: EasyHealManager.EasyRaiseStatus.Enable, RaiseOrder: EasyHealManager.RaiseOrderStatus.Order }
                    ))
                {
                    easyHealStorage.EasyRaise  = EasyHealManager.EasyRaiseStatus.Enable;
                    easyHealStorage.RaiseOrder = EasyHealManager.RaiseOrderStatus.Order;
                    this.config.Save(this);
                }

                if (ImGui.RadioButton
                    (
                        $"{Lang.Get("Enable")} [{Lang.Get("InReverseOrder")}]##easyraise",
                        easyHealStorage is { EasyRaise: EasyHealManager.EasyRaiseStatus.Enable, RaiseOrder: EasyHealManager.RaiseOrderStatus.Reverse }
                    ))
                {
                    easyHealStorage.EasyRaise  = EasyHealManager.EasyRaiseStatus.Enable;
                    easyHealStorage.RaiseOrder = EasyHealManager.RaiseOrderStatus.Reverse;
                    this.config.Save(this);
                }
            }

            ImGuiOm.TooltipHover(Lang.Get("HealerHelper-OrderHelp"), 20f * GlobalUIScale);
        }
    }

    private void DrawConfigRadio<T>(string label, T currentValue, T targetValue, Action<T> setter) where T : Enum
    {
        if (ImGui.RadioButton(label, currentValue.Equals(targetValue)))
        {
            setter(targetValue);
            config.Save(this);
        }
    }

    #endregion

    #region 事件

    private unsafe void OnPreUseAction
    (
        ref bool       isPrevented,
        ref ActionType type,
        ref uint       actionID,
        ref ulong      targetID,
        ref Vector3    location,
        ref uint       extraParam,
        ref byte       a7
    )
    {
        if (type != ActionType.Action || GameState.IsInPVPArea || AgentHUD.Instance()->PartyMemberCount < 2) return;

        var isHealer           = LocalPlayerState.ClassJobData.Role == 4;
        var isRangedWithRaised = LocalPlayerState.ClassJob is 27 or 35;
        if (!isHealer && !isRangedWithRaised) return;

        var healConfig = config.EasyHealStorage;
        var gameObject = DService.Instance().ObjectTable.SearchByID(targetID, IObjectTable.CharactersRange);

        if (isHealer)
        {
            if (LocalPlayerState.ClassJob == 33                        &&
                AutoPlayCardManager.PlayCardActions.Contains(actionID) &&
                config.AutoPlayCardStorage.AutoPlayCard != AutoPlayCardManager.AutoPlayCardStatus.Disable)
            {
                if (gameObject is not IBattleChara chara || !ActionManager.CanUseActionOnTarget(37023, (GameObject*)chara.ToStruct()))
                    targetID = UNSPECIFIC_TARGET_ID;

                autoPlayCardService.OnPrePlayCard(ref targetID, ref actionID);
            }
            else if (healConfig.EasyHeal == EasyHealManager.EasyHealStatus.Enable && healConfig.ActiveHealActions.Contains(actionID))
            {
                if (gameObject is not IBattleChara chara || !ActionManager.CanUseActionOnTarget(3595, (GameObject*)chara.ToStruct()))
                    targetID = UNSPECIFIC_TARGET_ID;

                easyHealService.OnPreHeal(ref targetID, ref actionID, ref isPrevented);
            }
            else if (healConfig.EasyDispel == EasyHealManager.EasyDispelStatus.Enable && actionID == 7568)
            {
                if (gameObject is not IBattleChara chara || !ActionManager.CanUseActionOnTarget(7568, (GameObject*)chara.ToStruct()))
                    targetID = UNSPECIFIC_TARGET_ID;

                easyHealService.OnPreDispel(ref targetID);
            }
        }

        if (healConfig.EasyRaise == EasyHealManager.EasyRaiseStatus.Enable && EasyHealManager.RaiseActions.Contains(actionID))
        {
            if (gameObject is not IBattleChara chara || chara.StatusFlags.IsSetAny(StatusFlags.Hostile))
                targetID = UNSPECIFIC_TARGET_ID;

            easyHealService.OnPreRaise(ref targetID, ref actionID);
        }
    }

    private void OnZoneChanged(uint u) =>
        autoPlayCardService.CurrentDutySection = AutoPlayCardManager.DutySection.Enter;

    private void OnDutyRecommenced(IDutyStateEventArgs args)
    {
        autoPlayCardService.CurrentDutySection = AutoPlayCardManager.DutySection.Enter;
        autoPlayCardService.OrderCandidates();
    }

    private void OnDutyStarted(IDutyStateEventArgs args)
    {
        autoPlayCardService.CurrentDutySection = AutoPlayCardManager.DutySection.Enter;
        autoPlayCardService.OrderCandidates();
    }

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag is ConditionFlag.InCombat && autoPlayCardService.CurrentDutySection == AutoPlayCardManager.DutySection.Enter)
        {
            autoPlayCardService.CurrentDutySection = AutoPlayCardManager.DutySection.Start;
            autoPlayCardService.StartTimeUTC       = StandardTimeManager.Instance().UTCNow;
        }
    }

    #endregion

    #region 常量
    
    private const string REMOTE_URI = "https://assets.sumemo.dev";

    private const uint UNSPECIFIC_TARGET_ID = 0xE000_0000;

    private static FrozenDictionary<ReadOnlySeString, ReadOnlySeString> JobNameMap { get; } =
        LuminaGetter.Get<ClassJob>()
                    .DistinctBy(x => x.NameEnglish)
                    .ToFrozenDictionary(s => s.NameEnglish, s => s.Name);

    #endregion
}
