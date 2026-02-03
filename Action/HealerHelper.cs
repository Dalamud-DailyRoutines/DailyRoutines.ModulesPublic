using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using Newtonsoft.Json;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace DailyRoutines.ModulesPublic;

public class HealerHelper : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("HealerHelperTitle"),
        Description = GetLoc("HealerHelperDescription"),
        Category    = ModuleCategories.Action,
        Author      = ["HaKu"]
    };

    private const uint UNSPECIFIC_TARGET_ID = 0xE000_0000;

    private static readonly Dictionary<ReadOnlySeString, ReadOnlySeString> JobNameMap =
        LuminaGetter.Get<ClassJob>()
                    .DistinctBy(x => x.NameEnglish)
                    .ToDictionary(s => s.NameEnglish, s => s.Name);

    private static ModuleStorage       ModuleConfig = null!;
    private static EasyHealManager     EasyHealService;
    private static AutoPlayCardManager AutoPlayCardService;
    private static ActionSelectCombo?  ActionSelect;

    protected override void Init()
    {
        ModuleConfig        = LoadConfig<ModuleStorage>() ?? new ModuleStorage();
        EasyHealService     = new(ModuleConfig.EasyHealStorage);
        AutoPlayCardService = new(ModuleConfig.AutoPlayCardStorage);

        Task.Run(async () => await RemoteRepoManager.FetchAll());

        UseActionManager.Instance().RegPreUseActionLocation(OnPreUseAction);
        DService.Instance().DutyState.DutyRecommenced    += OnDutyRecommenced;
        DService.Instance().DutyState.DutyStarted        += OnDutyStarted;
        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        DService.Instance().Condition.ConditionChange    += OnConditionChanged;

        if (GameState.ContentFinderCondition != 0 && DService.Instance().DutyState.IsDutyStarted)
            OnDutyStarted(null, 0);
    }

    protected override void Uninit()
    {
        UseActionManager.Instance().Unreg(OnPreUseAction);
        DService.Instance().DutyState.DutyRecommenced    -= OnDutyRecommenced;
        DService.Instance().DutyState.DutyStarted        -= OnDutyStarted;
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        DService.Instance().Condition.ConditionChange    -= OnConditionChanged;
    }

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

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("Notification"));
        ImGui.Spacing();

        using (ImRaii.PushIndent())
        {
            if (ImGui.Checkbox(GetLoc("SendChat"), ref ModuleConfig.SendChat))
                SaveConfig(ModuleConfig);
            if (ImGui.Checkbox(GetLoc("SendNotification"), ref ModuleConfig.SendNotification))
                SaveConfig(ModuleConfig);
        }
    }

    private void AutoPlayCardUI()
    {
        var config = ModuleConfig.AutoPlayCardStorage;
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("HealerHelper-AutoPlayCardTitle"));
        ImGuiOm.HelpMarker(GetLoc("HealerHelper-EasyRedirectDescription", LuminaWrapper.GetActionName(17055)));
        ImGui.Spacing();

        using (ImRaii.PushIndent())
        {
            DrawConfigRadio($"{GetLoc("Disable")}##autocard", config.AutoPlayCard, AutoPlayCardManager.AutoPlayCardStatus.Disable, v => config.AutoPlayCard = v);
            DrawConfigRadio
            (
                $"{GetLoc("Common")} ({GetLoc("HealerHelper-AutoPlayCard-CommonDescription")})",
                config.AutoPlayCard,
                AutoPlayCardManager.AutoPlayCardStatus.Default,
                v => config.AutoPlayCard = v
            );
            DrawConfigRadio
            (
                $"{GetLoc("Custom")} ({GetLoc("HealerHelper-AutoPlayCard-CustomDescription")})",
                config.AutoPlayCard,
                AutoPlayCardManager.AutoPlayCardStatus.Custom,
                v => config.AutoPlayCard = v
            );

            if (config.AutoPlayCard == AutoPlayCardManager.AutoPlayCardStatus.Custom)
            {
                ImGui.Spacing();
                CustomCardUI();
            }
        }
    }

    private void CustomCardUI()
    {
        var config = ModuleConfig.AutoPlayCardStorage;
        DrawCustomCardSection("HealerHelper-AutoPlayCard-MeleeOpener", config.CustomCardOrder.Melee["opener"], "Melee", "opener", "meleeopener");
        DrawCustomCardSection("HealerHelper-AutoPlayCard-Melee2Min",   config.CustomCardOrder.Melee["2m+"],    "Melee", "2m+",    "melee2m");
        DrawCustomCardSection("HealerHelper-AutoPlayCard-RangeOpener", config.CustomCardOrder.Range["opener"], "Range", "opener", "rangeopener");
        DrawCustomCardSection("HealerHelper-AutoPlayCard-Range2Min",   config.CustomCardOrder.Range["2m+"],    "Range", "2m+",    "range2m");
        SaveConfig(ModuleConfig);
    }

    private void DrawCustomCardSection(string titleKey, string[] order, string role, string section, string resetKeySuffix)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightYellow.ToVector4(), GetLoc(titleKey));

        if (CustomCardOrderUI(order))
        {
            SaveConfig(ModuleConfig);
            AutoPlayCardService.OrderCandidates();
        }

        ImGui.SameLine();
        ScaledDummy(5, 0);
        ImGui.SameLine();

        if (ImGui.Button($"{GetLoc("Reset")}##{resetKeySuffix}"))
        {
            AutoPlayCardService.InitCustomCardOrder(role, section);
            SaveConfig(ModuleConfig);
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
        var config = ModuleConfig.EasyHealStorage;
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("HealerHelper-EasyHealTitle"));
        ImGuiOm.HelpMarker(GetLoc("HealerHelper-EasyRedirectDescription", GetLoc("HealerHelper-SingleTargetHeal")));
        ImGui.Spacing();

        using (ImRaii.PushIndent())
        {
            DrawConfigRadio($"{GetLoc("Disable")}##easyheal", config.EasyHeal, EasyHealManager.EasyHealStatus.Disable, v => config.EasyHeal = v);
            DrawConfigRadio
            (
                $"{GetLoc("Enable")} ({GetLoc("HealerHelper-EasyHeal-EnableDescription")})",
                config.EasyHeal,
                EasyHealManager.EasyHealStatus.Enable,
                v => config.EasyHeal = v
            );

            if (config.EasyHeal == EasyHealManager.EasyHealStatus.Enable)
            {
                ImGui.Spacing();
                ActiveHealActionsSelect();
                ImGui.Spacing();

                ImGui.TextColored(KnownColor.LightGreen.ToVector4(), GetLoc("HealerHelper-EasyHeal-HealThreshold"));
                ImGuiOm.HelpMarker(GetLoc("HealerHelper-EasyHeal-HealThresholdHelp"));
                ImGui.Spacing();

                if (ImGui.SliderFloat("##HealThreshold", ref config.NeedHealThreshold, 0.0f, 1.0f, "%.2f"))
                    SaveConfig(ModuleConfig);

                if (config.NeedHealThreshold > 0.92f)
                {
                    ImGui.Spacing();
                    ImGui.TextColored(KnownColor.Orange.ToVector4(), GetLoc("HealerHelper-EasyHeal-OverhealWarning"));
                }

                ImGui.Spacing();
                ImGui.TextColored(KnownColor.LightYellow.ToVector4(), GetLoc("HealerHelper-EasyHeal-OverhealTargetDescription"));
                ImGui.Spacing();

                DrawConfigRadio
                (
                    $"{GetLoc("HealerHelper-EasyHeal-OverhealTarget-Prevent")}##overhealtarget",
                    config.OverhealTarget,
                    EasyHealManager.OverhealTarget.Prevent,
                    v => config.OverhealTarget = v
                );
                ImGui.SameLine();
                ScaledDummy(5, 0);
                ImGui.SameLine();
                DrawConfigRadio
                (
                    $"{GetLoc("HealerHelper-EasyHeal-OverhealTarget-Local")}##overhealtarget",
                    config.OverhealTarget,
                    EasyHealManager.OverhealTarget.Local,
                    v => config.OverhealTarget = v
                );
                ImGui.SameLine();
                ScaledDummy(5, 0);
                ImGui.SameLine();
                DrawConfigRadio
                (
                    $"{GetLoc("HealerHelper-EasyHeal-OverhealTarget-FirstTank")}##overhealtarget",
                    config.OverhealTarget,
                    EasyHealManager.OverhealTarget.FirstTank,
                    v => config.OverhealTarget = v
                );
            }
        }
    }

    private void ActiveHealActionsSelect()
    {
        ImGui.TextColored(KnownColor.YellowGreen.ToVector4(), $"{GetLoc("HealerHelper-EasyHeal-ActiveHealAction")}");
        ImGui.Spacing();

        if (ActionSelect.DrawCheckbox())
        {
            ModuleConfig.EasyHealStorage.ActiveHealActions = ActionSelect.SelectedIDs;
            SaveConfig(ModuleConfig);
        }

        ImGui.SameLine();
        ScaledDummy(5, 0);
        ImGui.SameLine();

        if (ImGui.Button($"{GetLoc("Reset")}##activehealactions"))
        {
            EasyHealService.InitActiveHealActions();
            SaveConfig(ModuleConfig);
        }
    }

    private void EasyDispelUI()
    {
        var config = ModuleConfig.EasyHealStorage;
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("HealerHelper-EasyDispelTitle"));
        ImGuiOm.HelpMarker(GetLoc("HealerHelper-EasyRedirectDescription", LuminaWrapper.GetActionName(7568)));
        ImGui.Spacing();

        using (ImRaii.PushIndent())
        {
            DrawConfigRadio($"{GetLoc("Disable")}##easydispel", config.EasyDispel, EasyHealManager.EasyDispelStatus.Disable, v => config.EasyDispel = v);

            using (ImRaii.Group())
            {
                if (ImGui.RadioButton
                    (
                        $"{GetLoc("Enable")} [{GetLoc("InOrder")}]##easydispel",
                        config is { EasyDispel: EasyHealManager.EasyDispelStatus.Enable, DispelOrder: EasyHealManager.DispelOrderStatus.Order }
                    ))
                {
                    config.EasyDispel  = EasyHealManager.EasyDispelStatus.Enable;
                    config.DispelOrder = EasyHealManager.DispelOrderStatus.Order;
                    SaveConfig(ModuleConfig);
                }

                if (ImGui.RadioButton
                    (
                        $"{GetLoc("Enable")} [{GetLoc("InReverseOrder")}]##easydispel",
                        config is { EasyDispel: EasyHealManager.EasyDispelStatus.Enable, DispelOrder: EasyHealManager.DispelOrderStatus.Reverse }
                    ))
                {
                    config.EasyDispel  = EasyHealManager.EasyDispelStatus.Enable;
                    config.DispelOrder = EasyHealManager.DispelOrderStatus.Reverse;
                    SaveConfig(ModuleConfig);
                }
            }

            ImGuiOm.TooltipHover(GetLoc("HealerHelper-OrderHelp"), 20f * GlobalFontScale);
        }
    }

    private void EasyRaiseUI()
    {
        var config = ModuleConfig.EasyHealStorage;
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("HealerHelper-EasyRaiseTitle"));
        ImGui.Spacing();

        using (ImRaii.PushIndent())
        {
            DrawConfigRadio($"{GetLoc("Disable")}##easyraise", config.EasyRaise, EasyHealManager.EasyRaiseStatus.Disable, v => config.EasyRaise = v);

            using (ImRaii.Group())
            {
                if (ImGui.RadioButton
                    (
                        $"{GetLoc("Enable")} [{GetLoc("InOrder")}]##easyraise",
                        config is { EasyRaise: EasyHealManager.EasyRaiseStatus.Enable, RaiseOrder: EasyHealManager.RaiseOrderStatus.Order }
                    ))
                {
                    config.EasyRaise  = EasyHealManager.EasyRaiseStatus.Enable;
                    config.RaiseOrder = EasyHealManager.RaiseOrderStatus.Order;
                    SaveConfig(ModuleConfig);
                }

                if (ImGui.RadioButton
                    (
                        $"{GetLoc("Enable")} [{GetLoc("InReverseOrder")}]##easyraise",
                        config is { EasyRaise: EasyHealManager.EasyRaiseStatus.Enable, RaiseOrder: EasyHealManager.RaiseOrderStatus.Reverse }
                    ))
                {
                    config.EasyRaise  = EasyHealManager.EasyRaiseStatus.Enable;
                    config.RaiseOrder = EasyHealManager.RaiseOrderStatus.Reverse;
                    SaveConfig(ModuleConfig);
                }
            }

            ImGuiOm.TooltipHover(GetLoc("HealerHelper-OrderHelp"), 20f * GlobalFontScale);
        }
    }

    private void DrawConfigRadio<T>(string label, T currentValue, T targetValue, Action<T> setter) where T : Enum
    {
        if (ImGui.RadioButton(label, currentValue.Equals(targetValue)))
        {
            setter(targetValue);
            SaveConfig(ModuleConfig);
        }
    }

    #endregion

    #region 事件

    private static unsafe void OnPreUseAction
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

        var healConfig = ModuleConfig.EasyHealStorage;
        var gameObject = DService.Instance().ObjectTable.SearchByID(targetID, IObjectTable.CharactersRange);
        if (isHealer)
        {
            if (LocalPlayerState.ClassJob == 33                        &&
                AutoPlayCardManager.PlayCardActions.Contains(actionID) &&
                ModuleConfig.AutoPlayCardStorage.AutoPlayCard != AutoPlayCardManager.AutoPlayCardStatus.Disable)
            {
                if (gameObject is not IBattleChara chara || !ActionManager.CanUseActionOnTarget(37023, (GameObject*)chara.ToStruct()))
                    targetID = UNSPECIFIC_TARGET_ID;
                
                AutoPlayCardService.OnPrePlayCard(ref targetID, ref actionID);
            }
            else if (healConfig.EasyHeal == EasyHealManager.EasyHealStatus.Enable && healConfig.ActiveHealActions.Contains(actionID))
            {
                if (gameObject is not IBattleChara chara || !ActionManager.CanUseActionOnTarget(3595, (GameObject*)chara.ToStruct()))
                    targetID = UNSPECIFIC_TARGET_ID;
                
                EasyHealService.OnPreHeal(ref targetID, ref actionID, ref isPrevented);
            }
            else if (healConfig.EasyDispel == EasyHealManager.EasyDispelStatus.Enable && actionID == 7568)
            {
                if (gameObject is not IBattleChara chara || !ActionManager.CanUseActionOnTarget(7568, (GameObject*)chara.ToStruct()))
                    targetID = UNSPECIFIC_TARGET_ID;
                
                EasyHealService.OnPreDispel(ref targetID);
            }
        }

        if (healConfig.EasyRaise == EasyHealManager.EasyRaiseStatus.Enable && EasyHealManager.RaiseActions.Contains(actionID))
        {
            if (gameObject is not IBattleChara chara || chara.StatusFlags.IsSetAny(StatusFlags.Hostile))
                targetID = UNSPECIFIC_TARGET_ID;
            
            EasyHealService.OnPreRaise(ref targetID, ref actionID);
        }
    }

    private static void OnZoneChanged(ushort _) =>
        AutoPlayCardService.CurrentDutySection = AutoPlayCardManager.DutySection.Enter;

    private static void OnDutyRecommenced(object? sender, ushort e)
    {
        AutoPlayCardService.CurrentDutySection = AutoPlayCardManager.DutySection.Enter;
        AutoPlayCardService.OrderCandidates();
    }

    private static void OnDutyStarted(object? sender, ushort e)
    {
        AutoPlayCardService.CurrentDutySection = AutoPlayCardManager.DutySection.Enter;
        AutoPlayCardService.OrderCandidates();
    }

    private static void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag is ConditionFlag.InCombat && AutoPlayCardService.CurrentDutySection == AutoPlayCardManager.DutySection.Enter)
        {
            AutoPlayCardService.CurrentDutySection = AutoPlayCardManager.DutySection.Start;
            AutoPlayCardService.StartTimeUTC       = StandardTimeManager.Instance().UTCNow;
        }
    }

    #endregion

    #region Utils

    private static void NotifyTargetChange(IBattleChara gameObject, string locKeySuffix)
    {
        var name = gameObject.Name.ToString();
        var job  = gameObject.ClassJob.Value;

        if (ModuleConfig.SendChat)
            Chat(GetSLoc(locKeySuffix, name, job.ToBitmapFontIcon(), job.Name));
        if (ModuleConfig.SendNotification)
            NotificationInfo(GetLoc(locKeySuffix, name, string.Empty, job.Name));
    }

    #endregion

    #region RemoteCache

    private static class RemoteRepoManager
    {
        private const string URI = "https://assets.sumemo.dev";

        public static async Task FetchPlayCardOrder()
        {
            if (AutoPlayCardService.DefaultCardOrderLoaded) return;

            try
            {
                var json = await HTTPClientHelper.Get().GetStringAsync($"{URI}/card-order.json");
                var resp = JsonConvert.DeserializeObject<AutoPlayCardManager.PlayCardOrder>(json);

                if (resp != null)
                {
                    AutoPlayCardService.InitDefaultCardOrder(resp);
                    if (!AutoPlayCardService.CustomCardOrderLoaded)
                        AutoPlayCardService.InitCustomCardOrder();
                }
            }
            catch
            {
                // ignored
            }
        }

        public static async Task FetchHealActions()
        {
            if (EasyHealService.TargetHealActionsLoaded) return;

            try
            {
                var json = await HTTPClientHelper.Get().GetStringAsync($"{URI}/heal-action.json");
                var resp = JsonConvert.DeserializeObject<Dictionary<string, List<EasyHealManager.HealAction>>>(json);

                if (resp != null)
                {
                    EasyHealService.InitTargetHealActions(resp.SelectMany(kv => kv.Value).ToDictionary(act => act.ID, act => act));
                    if (!EasyHealService.ActiveHealActionsLoaded)
                        EasyHealService.InitActiveHealActions();
                }
            }
            catch
            {
                /* ignored */
            }
        }

        public static async Task FetchAll()
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
                ActionSelect ??= new("##ActionSelect", LuminaGetter.Get<LuminaAction>().Where(x => EasyHealService.TargetHealActions.ContainsKey(x.RowId)));
                if (ModuleConfig.EasyHealStorage.ActiveHealActions.Count == 0)
                    EasyHealService.InitActiveHealActions();
                ActionSelect.SelectedIDs = ModuleConfig.EasyHealStorage.ActiveHealActions;
            }
        }
    }

    #endregion

    #region AutoPlayCard

    private class AutoPlayCardManager
    (
        AutoPlayCardManager.Storage config
    )
    {
        public static readonly FrozenSet<uint> PlayCardActions = [37023, 37026];

        public bool DefaultCardOrderLoaded => config.DefaultCardOrder.Melee.Count > 0 && config.DefaultCardOrder.Range.Count > 0;
        public bool CustomCardOrderLoaded  => config.CustomCardOrder.Melee.Count  > 0 && config.CustomCardOrder.Range.Count  > 0;

        private readonly List<(uint id, double priority)> meleeCandidateOrder = [];
        private readonly List<(uint id, double priority)> rangeCandidateOrder = [];

        public DutySection CurrentDutySection;
        public DateTime    StartTimeUTC;

        public bool IsOpener =>
            (StandardTimeManager.Instance().UTCNow - StartTimeUTC).TotalSeconds > 90;

        public class Storage
        {
            public          AutoPlayCardStatus AutoPlayCard     = AutoPlayCardStatus.Default;
            public          PlayCardOrder      DefaultCardOrder = new();
            public readonly PlayCardOrder      CustomCardOrder  = new();
        }

        public enum AutoPlayCardStatus
        {
            Disable,
            Default,
            Custom
        }

        public class PlayCardOrder
        {
            [JsonProperty("melee")] public Dictionary<string, string[]> Melee { get; private set; } = new();
            [JsonProperty("range")] public Dictionary<string, string[]> Range { get; private set; } = new();
        }

        public enum DutySection
        {
            Enter,
            Start
        }

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
            NotifyTargetChange
            (
                IBattleChara.Create((nint)finalTarget),
                actionID == 37023 ? "HealerHelper-AutoPlayCard-Message-Melee" : "HealerHelper-AutoPlayCard-Message-Range"
            );
        }
    }

    #endregion

    #region EasyHeal

    private class EasyHealManager
    (
        EasyHealManager.Storage config
    )
    {
        public static readonly FrozenSet<uint> RaiseActions = [125, 173, 3603, 24287, 7670, 7523, 64556];

        public bool                         TargetHealActionsLoaded => config.TargetHealActions.Count > 0;
        public bool                         ActiveHealActionsLoaded => config.ActiveHealActions.Count > 0;
        public Dictionary<uint, HealAction> TargetHealActions       => config.TargetHealActions;

        public class Storage
        {
            public EasyHealStatus               EasyHeal          = EasyHealStatus.Enable;
            public float                        NeedHealThreshold = 0.92f;
            public OverhealTarget               OverhealTarget    = OverhealTarget.Local;
            public Dictionary<uint, HealAction> TargetHealActions = [];
            public HashSet<uint>                ActiveHealActions = [];
            public EasyDispelStatus             EasyDispel        = EasyDispelStatus.Enable;
            public DispelOrderStatus            DispelOrder       = DispelOrderStatus.Order;
            public EasyRaiseStatus              EasyRaise         = EasyRaiseStatus.Enable;
            public RaiseOrderStatus             RaiseOrder        = RaiseOrderStatus.Order;
        }

        public enum EasyHealStatus
        {
            Disable,
            Enable
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

        public enum OverhealTarget
        {
            Local,
            FirstTank,
            Prevent
        }

        public enum EasyDispelStatus
        {
            Disable,
            Enable
        }

        public enum DispelOrderStatus
        {
            Order,
            Reverse
        }

        public enum EasyRaiseStatus
        {
            Disable,
            Enable
        }

        public enum RaiseOrderStatus
        {
            Order,
            Reverse
        }

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
                    ) != 0)
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

                NotifyTargetChange(IBattleChara.Create((nint)needHealObject), "HealerHelper-EasyHeal-Message");
                return;
            }

            switch (config.OverhealTarget)
            {
                case OverhealTarget.Prevent:
                    isPrevented = true;
                    return;

                case OverhealTarget.Local:
                    targetID = LocalPlayerState.EntityID;
                    NotifyTargetChange(LocalPlayerState.Object, "HealerHelper-EasyHeal-Message");
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
                        NotifyTargetChange(IBattleChara.Create((nint)info.Object), "HealerHelper-EasyHeal-Message");
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

            if (LocalPlayerState.Object.StatusList.Any(s => PresetSheet.DispellableStatuses.ContainsKey(s.StatusID)))
            {
                targetID = LocalPlayerState.EntityID;
                NotifyTargetChange(LocalPlayerState.Object, "HealerHelper-EasyDispel-Message");
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
                            if (PresetSheet.DispellableStatuses.ContainsKey(s.StatusId))
                                return true;
                        }

                        return false;
                    },
                    config.DispelOrder == DispelOrderStatus.Reverse
                );

                if (obj != null)
                {
                    targetID = obj->EntityId;
                    NotifyTargetChange(IBattleChara.Create((nint)obj), "HealerHelper-EasyDispel-Message");
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
            NotifyTargetChange(IBattleChara.Create((nint)obj), "HealerHelper-EasyRaise-Message");
        }

        public static unsafe bool IsHealable(IGameObject? gameObject) =>
            ActionManager.CanUseActionOnTarget(3595, gameObject.ToStruct());
    }

    #endregion

    #region Config

    private class ModuleStorage : ModuleConfiguration
    {
        public AutoPlayCardManager.Storage AutoPlayCardStorage = new();
        public EasyHealManager.Storage     EasyHealStorage     = new();
        public bool                        SendChat;
        public bool                        SendNotification = true;
    }

    #endregion
}
