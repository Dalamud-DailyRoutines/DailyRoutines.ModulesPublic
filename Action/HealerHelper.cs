using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using DailyRoutines.Managers;
using DailyRoutines.Widgets;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Party;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using Newtonsoft.Json;
using LuminaAction = Lumina.Excel.Sheets.Action;


namespace DailyRoutines.ModulesPublic;

public class HealerHelper : DailyModuleBase
{
    #region Core

    // info
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("HealerHelperTitle"),
        Description = GetLoc("HealerHelperDescription"),
        Category    = ModuleCategories.Action,
        Author      = ["HaKu"]
    };

    // const
    private const uint UnspecificTargetId = 0xE000_0000;

    // cache
    private static readonly Dictionary<ReadOnlySeString, ReadOnlySeString> JobNameMap =
        LuminaGetter.Get<ClassJob>()
                    .ToDictionary(s => s.NameEnglish, s => s.Name);

    // storage
    private static ModuleStorage ModuleConfig = null!;

    // managers
    private static EasyHealManager     EasyHealService;
    private static AutoPlayCardManager AutoPlayCardService;

    // ui
    private static ActionSelectCombo? ActionSelect;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<ModuleStorage>() ?? new ModuleStorage();

        // managers
        EasyHealService     = new EasyHealManager(ModuleConfig.EasyHealStorage);
        AutoPlayCardService = new AutoPlayCardManager(ModuleConfig.AutoPlayCardStorage);

        // fetch remote hotfix
        Task.Run(async () => await RemoteRepoManager.FetchAll());

        // register hooks
        UseActionManager.RegPreUseActionLocation(OnPreUseAction);
        DService.DutyState.DutyRecommenced    += OnDutyRecommenced;
        DService.ClientState.TerritoryChanged += OnZoneChanged;
        DService.Condition.ConditionChange    += OnConditionChanged;
        FrameworkManager.Register(OnUpdate, throttleMS: 5_000);
    }

    protected override void Uninit()
    {
        UseActionManager.UnregPreUseActionLocation(OnPreUseAction);
        DService.DutyState.DutyRecommenced    -= OnDutyRecommenced;
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        DService.Condition.ConditionChange    -= OnConditionChanged;
        FrameworkManager.Unregister(OnUpdate);

        base.Uninit();
    }

    #endregion

    #region UI

    private static int? customCardOrderDragIndex;

    protected override void ConfigUI()
    {
        // auto play card
        AutoPlayCardUI();

        ImGui.NewLine();

        // easy heal
        EasyHealUI();

        ImGui.NewLine();

        // easy dispel
        EasyDispelUI();

        ImGui.NewLine();

        // easy raise
        EasyRaiseUI();

        ImGui.NewLine();

        // notifications
        ImGui.TextColored(LightSkyBlue, GetLoc("Notification"));
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
        var cardConfig = ModuleConfig.AutoPlayCardStorage;

        ImGui.TextColored(LightSkyBlue, GetLoc("HealerHelper-AutoPlayCardTitle"));
        ImGuiOm.HelpMarker(GetLoc("HealerHelper-EasyRedirectDescription", LuminaWrapper.GetActionName(17055)));

        ImGui.Spacing();

        using (ImRaii.PushIndent())
        {
            if (ImGui.RadioButton($"{GetLoc("Disable")}##autocard",
                                  cardConfig.AutoPlayCard == AutoPlayCardManager.AutoPlayCardStatus.Disable))
            {
                cardConfig.AutoPlayCard = AutoPlayCardManager.AutoPlayCardStatus.Disable;
                SaveConfig(ModuleConfig);
            }

            if (ImGui.RadioButton($"{GetLoc("Common")} ({GetLoc("HealerHelper-AutoPlayCard-CommonDescription")})",
                                  cardConfig.AutoPlayCard == AutoPlayCardManager.AutoPlayCardStatus.Default))
            {
                cardConfig.AutoPlayCard = AutoPlayCardManager.AutoPlayCardStatus.Default;
                SaveConfig(ModuleConfig);
            }

            if (ImGui.RadioButton($"{GetLoc("Custom")} ({GetLoc("HealerHelper-AutoPlayCard-CustomDescription")})",
                                  cardConfig.AutoPlayCard == AutoPlayCardManager.AutoPlayCardStatus.Custom))
            {
                cardConfig.AutoPlayCard = AutoPlayCardManager.AutoPlayCardStatus.Custom;
                SaveConfig(ModuleConfig);
            }

            if (cardConfig.AutoPlayCard == AutoPlayCardManager.AutoPlayCardStatus.Custom)
            {
                ImGui.Spacing();
                CustomCardUI();
            }
        }
    }

    private void CustomCardUI()
    {
        var config = ModuleConfig.AutoPlayCardStorage;

        // melee opener
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightYellow, $"{GetLoc("HealerHelper-AutoPlayCard-MeleeOpener")}");

        if (CustomCardOrderUI(config.CustomCardOrder.Melee["opener"]))
        {
            SaveConfig(ModuleConfig);
            AutoPlayCardService.OrderCandidates();
        }

        ImGui.SameLine();
        ScaledDummy(5, 0);
        ImGui.SameLine();
        if (ImGui.Button($"{GetLoc("Reset")}##meleeopener"))
        {
            AutoPlayCardService.InitCustomCardOrder("Melee", "opener");
            SaveConfig(ModuleConfig);
        }

        ImGui.Spacing();

        // melee 2m+
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightYellow, $"{GetLoc("HealerHelper-AutoPlayCard-Melee2Min")}");

        if (CustomCardOrderUI(config.CustomCardOrder.Melee["2m+"]))
        {
            SaveConfig(ModuleConfig);
            AutoPlayCardService.OrderCandidates();
        }

        ImGui.SameLine();
        ScaledDummy(5, 0);
        ImGui.SameLine();
        if (ImGui.Button($"{GetLoc("Reset")}##melee2m"))
        {
            AutoPlayCardService.InitCustomCardOrder("Melee", "2m+");
            SaveConfig(ModuleConfig);
        }

        ImGui.Spacing();

        // range opener
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightYellow, $"{GetLoc("HealerHelper-AutoPlayCard-RangeOpener")}");

        if (CustomCardOrderUI(config.CustomCardOrder.Range["opener"]))
        {
            SaveConfig(ModuleConfig);
            AutoPlayCardService.OrderCandidates();
        }

        ImGui.SameLine();
        ScaledDummy(5, 0);
        ImGui.SameLine();
        if (ImGui.Button($"{GetLoc("Reset")}##rangeopener"))
        {
            AutoPlayCardService.InitCustomCardOrder("Range", "opener");
            SaveConfig(ModuleConfig);
        }

        ImGui.Spacing();

        // range opener
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightYellow, $"{GetLoc("HealerHelper-AutoPlayCard-Range2Min")}");

        if (CustomCardOrderUI(config.CustomCardOrder.Range["2m+"]))
        {
            SaveConfig(ModuleConfig);
            AutoPlayCardService.OrderCandidates();
        }

        ImGui.SameLine();
        ScaledDummy(5, 0);
        ImGui.SameLine();
        if (ImGui.Button($"{GetLoc("Reset")}##range2m"))
        {
            AutoPlayCardService.InitCustomCardOrder("Range", "2m+");
            SaveConfig(ModuleConfig);
        }

        SaveConfig(ModuleConfig);
    }

    private static bool CustomCardOrderUI(string[] cardOrder)
    {
        var modified = false;

        for (var index = 0; index < cardOrder.Length; index++)
        {
            using var id = ImRaii.PushId($"{index}");
            // component
            var jobName  = JobNameMap[cardOrder[index]].ExtractText();
            var textSize = ImGui.CalcTextSize(jobName);
            ImGui.Button(jobName, new(textSize.X + 20f, 0));

            if (index != cardOrder.Length - 1)
                ImGui.SameLine();

            if (ImGui.BeginDragDropSource())
            {
                customCardOrderDragIndex = index;
                ImGui.SetDragDropPayload("##CustomCardOrder", nint.Zero, 0);
                ImGui.EndDragDropSource();
            }

            if (ImGui.BeginDragDropTarget())
            {
                ImGui.AcceptDragDropPayload("##CustomCardOrder");
                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && customCardOrderDragIndex.HasValue)
                {
                    (cardOrder[index], cardOrder[customCardOrderDragIndex.Value]) = (cardOrder[customCardOrderDragIndex.Value], cardOrder[index]);

                    modified = true;
                }

                ImGui.EndDragDropTarget();
            }
        }

        return modified;
    }

    private void EasyHealUI()
    {
        var config = ModuleConfig.EasyHealStorage;

        ImGui.TextColored(LightSkyBlue, GetLoc("HealerHelper-EasyHealTitle"));
        ImGuiOm.HelpMarker(GetLoc("HealerHelper-EasyRedirectDescription", GetLoc("HealerHelper-SingleTargetHeal")));

        ImGui.Spacing();

        using (ImRaii.PushIndent())
        {
            if (ImGui.RadioButton($"{GetLoc("Disable")}##easyheal",
                                  config.EasyHeal == EasyHealManager.EasyHealStatus.Disable))
            {
                config.EasyHeal = EasyHealManager.EasyHealStatus.Disable;
                SaveConfig(ModuleConfig);
            }

            if (ImGui.RadioButton($"{GetLoc("Enable")} ({GetLoc("HealerHelper-EasyHeal-EnableDescription")})",
                                  config.EasyHeal == EasyHealManager.EasyHealStatus.Enable))
            {
                config.EasyHeal = EasyHealManager.EasyHealStatus.Enable;
                SaveConfig(ModuleConfig);
            }

            // heal threshold
            if (config.EasyHeal == EasyHealManager.EasyHealStatus.Enable)
            {
                ImGui.Spacing();

                ActiveHealActionsSelect();

                ImGui.Spacing();

                ImGui.TextColored(LightGreen, GetLoc("HealerHelper-EasyHeal-HealThreshold"));
                ImGuiOm.HelpMarker(GetLoc("HealerHelper-EasyHeal-HealThresholdHelp"));

                ImGui.Spacing();

                if (ImGui.SliderFloat("##HealThreshold", ref config.NeedHealThreshold, 0.0f, 1.0f, "%.2f"))
                    SaveConfig(ModuleConfig);

                // all time heal warning
                if (config.NeedHealThreshold > 0.92f)
                {
                    ImGui.Spacing();
                    ImGui.TextColored(Orange, GetLoc("HealerHelper-EasyHeal-OverhealWarning"));
                }

                ImGui.Spacing();

                // target when overheal
                ImGui.TextColored(LightPink, GetLoc("HealerHelper-EasyHeal-OverhealTargetDescription"));

                ImGui.Spacing();

                if (ImGui.RadioButton($"{GetLoc("HealerHelper-EasyHeal-OverhealTarget-Prevent")}##overhealtarget",
                                      config.OverhealTarget == EasyHealManager.OverhealTarget.Prevent))
                {
                    config.OverhealTarget = EasyHealManager.OverhealTarget.Prevent;
                    SaveConfig(ModuleConfig);
                }

                ImGui.SameLine();
                ScaledDummy(5, 0);
                ImGui.SameLine();
                if (ImGui.RadioButton($"{GetLoc("HealerHelper-EasyHeal-OverhealTarget-Local")}##overhealtarget",
                                      config.OverhealTarget == EasyHealManager.OverhealTarget.Local))
                {
                    config.OverhealTarget = EasyHealManager.OverhealTarget.Local;
                    SaveConfig(ModuleConfig);
                }

                ImGui.SameLine();
                ScaledDummy(5, 0);
                ImGui.SameLine();
                if (ImGui.RadioButton($"{GetLoc("HealerHelper-EasyHeal-OverhealTarget-FirstTank")}##overhealtarget",
                                      config.OverhealTarget == EasyHealManager.OverhealTarget.FirstTank))
                {
                    config.OverhealTarget = EasyHealManager.OverhealTarget.FirstTank;
                    SaveConfig(ModuleConfig);
                }
            }
        }
    }

    private void ActiveHealActionsSelect()
    {
        ImGui.TextColored(YellowGreen, $"{GetLoc("HealerHelper-EasyHeal-ActiveHealAction")}");
        ImGui.Spacing();

        if (ActionSelect.DrawCheckbox())
        {
            ModuleConfig.EasyHealStorage.ActiveHealActions = ActionSelect.SelectedActionIDs;
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

        ImGui.TextColored(LightSkyBlue, GetLoc("HealerHelper-EasyDispelTitle"));
        ImGuiOm.HelpMarker(GetLoc("HealerHelper-EasyRedirectDescription", LuminaWrapper.GetActionName(7568)));

        ImGui.Spacing();

        using (ImRaii.PushIndent())
        {
            if (ImGui.RadioButton($"{GetLoc("Disable")}##easydispel",
                                  config.EasyDispel == EasyHealManager.EasyDispelStatus.Disable))
            {
                config.EasyDispel = EasyHealManager.EasyDispelStatus.Disable;
                SaveConfig(ModuleConfig);
            }

            using (ImRaii.Group())
            {
                if (ImGui.RadioButton($"{GetLoc("Enable")} [{GetLoc("InOrder")}]##easydispel",
                                      config is { EasyDispel: EasyHealManager.EasyDispelStatus.Enable, DispelOrder: EasyHealManager.DispelOrderStatus.Order }))
                {
                    config.EasyDispel  = EasyHealManager.EasyDispelStatus.Enable;
                    config.DispelOrder = EasyHealManager.DispelOrderStatus.Order;
                    SaveConfig(ModuleConfig);
                }

                if (ImGui.RadioButton($"{GetLoc("Enable")} [{GetLoc("InReverseOrder")}]##easydispel",
                                      config is { EasyDispel: EasyHealManager.EasyDispelStatus.Enable, DispelOrder: EasyHealManager.DispelOrderStatus.Reverse }))
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

        ImGui.TextColored(LightSkyBlue, GetLoc("HealerHelper-EasyRaiseTitle"));

        ImGui.Spacing();

        using (ImRaii.PushIndent())
        {
            if (ImGui.RadioButton($"{GetLoc("Disable")}##easyraise",
                                  config.EasyRaise == EasyHealManager.EasyRaiseStatus.Disable))
            {
                config.EasyRaise = EasyHealManager.EasyRaiseStatus.Disable;
                SaveConfig(ModuleConfig);
            }

            using (ImRaii.Group())
            {
                if (ImGui.RadioButton($"{GetLoc("Enable")} [{GetLoc("InOrder")}]##easyraise",
                                      config is { EasyRaise: EasyHealManager.EasyRaiseStatus.Enable, RaiseOrder: EasyHealManager.RaiseOrderStatus.Order }))
                {
                    config.EasyRaise  = EasyHealManager.EasyRaiseStatus.Enable;
                    config.RaiseOrder = EasyHealManager.RaiseOrderStatus.Order;
                    SaveConfig(ModuleConfig);
                }

                if (ImGui.RadioButton($"{GetLoc("Enable")} [{GetLoc("InReverseOrder")}]##easyraise",
                                      config is { EasyRaise: EasyHealManager.EasyRaiseStatus.Enable, RaiseOrder: EasyHealManager.RaiseOrderStatus.Reverse }))
                {
                    config.EasyRaise  = EasyHealManager.EasyRaiseStatus.Enable;
                    config.RaiseOrder = EasyHealManager.RaiseOrderStatus.Reverse;
                    SaveConfig(ModuleConfig);
                }
            }

            ImGuiOm.TooltipHover(GetLoc("HealerHelper-OrderHelp"), 20f * GlobalFontScale);
        }
    }

    #endregion

    #region Hooks

    // hook before play card and target heal
    private static void OnPreUseAction(
        ref bool  isPrevented, ref ActionType type,     ref uint actionId,
        ref ulong targetId,    ref Vector3    location, ref uint extraParam
    )
    {
        if (type != ActionType.Action || GameState.IsInPVPArea || DService.PartyList.Length < 2)
            return;

        // job check
        var isHealer = LocalPlayerState.ClassJobData.Role == 4;

        // healer related
        if (isHealer)
        {
            var isAST = LocalPlayerState.ClassJob == 33;

            // auto play card
            var cardConfig = ModuleConfig.AutoPlayCardStorage;
            if (isAST && AutoPlayCardManager.PlayCardActions.Contains(actionId) && cardConfig.AutoPlayCard != AutoPlayCardManager.AutoPlayCardStatus.Disable)
                AutoPlayCardService.OnPrePlayCard(ref targetId, ref actionId);

            // easy heal
            var healConfig = ModuleConfig.EasyHealStorage;
            if (healConfig.EasyHeal == EasyHealManager.EasyHealStatus.Enable && healConfig.ActiveHealActions.Contains(actionId))
                EasyHealService.OnPreHeal(ref targetId, ref actionId, ref isPrevented);

            // easy dispel
            if (healConfig.EasyDispel == EasyHealManager.EasyDispelStatus.Enable && actionId is 7568)
                EasyHealService.OnPreDispel(ref targetId, ref isPrevented);
        }

        // can raise
        var canRaise = isHealer || LocalPlayerState.ClassJob is 27 or 35;
        if (canRaise)
        {
            // easy raise
            var healConfig = ModuleConfig.EasyHealStorage;
            if (healConfig.EasyRaise == EasyHealManager.EasyRaiseStatus.Enable && EasyHealManager.RaiseActions.Contains(actionId))
                EasyHealService.OnPreRaise(ref targetId, ref actionId, ref isPrevented);
        }
    }

    private static void OnZoneChanged(ushort _)
    {
        AutoPlayCardService.CurrentDutySection = AutoPlayCardManager.DutySection.Enter;
        AutoPlayCardService.NeedReorder        = true;
    }

    private static void OnDutyRecommenced(object? sender, ushort e)
    {
        AutoPlayCardService.CurrentDutySection = AutoPlayCardManager.DutySection.Enter;
        AutoPlayCardService.NeedReorder        = true;
    }

    private static void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag is ConditionFlag.InCombat && AutoPlayCardService.CurrentDutySection == AutoPlayCardManager.DutySection.Enter)
        {
            AutoPlayCardService.CurrentDutySection = AutoPlayCardManager.DutySection.Start;
            AutoPlayCardService.StartTime          = DateTime.UtcNow;
        }
    }

    private static void OnUpdate(IFramework _)
    {
        // party member changed?
        try
        {
            var inPvEParty = DService.PartyList.Length > 1 && !GameState.IsInPVPArea;
            if (!inPvEParty)
                return;

            // need to update candidates?
            var ids = DService.PartyList.Select(m => m.ObjectId).ToHashSet();
            if (!ids.SetEquals(AutoPlayCardService.PartyMemberIdsCache) || AutoPlayCardService.NeedReorder)
            {
                // party member changed, update candidates
                AutoPlayCardService.OrderCandidates();
                AutoPlayCardService.PartyMemberIdsCache = ids;
                AutoPlayCardService.NeedReorder         = false;
            }
        }
        catch (Exception)
        {
            // ignored
        }
    }

    #endregion

    #region Utils

    private static IPartyMember? FetchMember(uint id)
        => DService.PartyList.FirstOrDefault(m => m.ObjectId == id);

    private static unsafe uint? FetchMemberIndex(uint id)
        => (uint)AgentHUD.Instance()->PartyMembers.ToArray()
                                                  .Select((m, i) => (m, i))
                                                  .FirstOrDefault(t => t.m.EntityId == id).i;

    #endregion

    #region RemoteCache

    private static class RemoteRepoManager
    {
        // param
        public static bool IsLoading = true;

        // const
        private const string Uri = "https://assets.sumemo.dev";

        public static async Task FetchPlayCardOrder()
        {
            if (AutoPlayCardService.DefaultCardOrderLoaded)
                return;

            try
            {
                var json = await HttpClientHelper.Get().GetStringAsync($"{Uri}/card-order");
                var resp = JsonConvert.DeserializeObject<AutoPlayCardManager.PlayCardOrder>(json);
                if (resp is null)
                    Error($"[HealerHelper] 远程发卡顺序文件解析失败: {json}");
                else
                {
                    AutoPlayCardService.InitDefaultCardOrder(resp);
                    // init custom if empty
                    if (!AutoPlayCardService.CustomCardOrderLoaded)
                        AutoPlayCardService.InitCustomCardOrder();
                }
            }
            catch (Exception ex) { Error($"[HealerHelper] 远程发卡顺序文件获取失败: {ex}"); }
        }

        public static async Task FetchHealActions()
        {
            if (EasyHealService.TargetHealActionsLoaded)
                return;

            try
            {
                var json = await HttpClientHelper.Get().GetStringAsync($"{Uri}/heal-action");
                var resp = JsonConvert.DeserializeObject<Dictionary<string, List<EasyHealManager.HealAction>>>(json);
                if (resp is null)
                    Error($"[HealerHelper] 远程治疗技能文件解析失败: {json}");
                else
                {
                    EasyHealService.InitTargetHealActions(resp.SelectMany(kv => kv.Value).ToDictionary(act => act.Id, act => act));

                    // when active is empty, set with default on heal actions
                    if (!EasyHealService.ActiveHealActionsLoaded)
                        EasyHealService.InitActiveHealActions();
                }
            }
            catch (Exception ex) { Error($"[HealerHelper] 远程治疗技能文件获取失败: {ex}"); }
        }

        public static async Task FetchAll()
        {
            try
            {
                var tasks = new[] { FetchPlayCardOrder(), FetchHealActions() };
                await Task.WhenAll(tasks);
            }
            catch (Exception ex) { Error($"[HealerHelper] 远程资源获取失败: {ex}"); } finally
            {
                // action select combo
                ActionSelect ??= new("##ActionSelect", LuminaGetter.Get<LuminaAction>().Where(x => EasyHealService.TargetHealActions.ContainsKey(x.RowId)));
                if (ModuleConfig.EasyHealStorage.ActiveHealActions.Count == 0)
                    EasyHealService.InitActiveHealActions();
                ActionSelect.SelectedActionIDs = ModuleConfig.EasyHealStorage.ActiveHealActions;
            }
        }
    }

    #endregion

    #region AutoPlayCard

    private class AutoPlayCardManager(AutoPlayCardManager.Storage config)
    {
        // const
        public static readonly HashSet<uint> PlayCardActions = [37023, 37026];

        // cache
        public HashSet<uint> PartyMemberIdsCache = []; // check party member changed or not

        // card order load status
        public bool DefaultCardOrderLoaded => config.DefaultCardOrder.Melee.Count > 0 && config.DefaultCardOrder.Range.Count > 0;
        public bool CustomCardOrderLoaded  => config.CustomCardOrder.Melee.Count > 0 && config.CustomCardOrder.Range.Count > 0;

        private readonly List<(uint id, double priority)> meleeCandidateOrder = [];
        private readonly List<(uint id, double priority)> rangeCandidateOrder = [];

        public DutySection CurrentDutySection;
        public DateTime    StartTime;
        public bool        IsOpener => (DateTime.UtcNow - StartTime).TotalSeconds > 90;
        public bool        NeedReorder;

        // config
        public class Storage
        {
            public          AutoPlayCardStatus AutoPlayCard     = AutoPlayCardStatus.Default;
            public          PlayCardOrder      DefaultCardOrder = new();
            public readonly PlayCardOrder      CustomCardOrder  = new();
        }

        #region Structs

        public enum AutoPlayCardStatus
        {
            Disable, // disable auto play card
            Default, // select target based on predefined order when no target selected
            Custom   // defined by user
        }

        // predefined card priority, arranged based on the guidance in The Balance
        // https://www.thebalanceffxiv.com/
        // load from su-cache:card-order
        public class PlayCardOrder
        {
            [JsonProperty("melee")]
            public Dictionary<string, string[]> Melee { get; private set; } = new();

            [JsonProperty("range")]
            public Dictionary<string, string[]> Range { get; private set; } = new();
        }

        public enum DutySection
        {
            Enter,
            Start
        }

        #endregion

        #region Funcs

        public void InitDefaultCardOrder(PlayCardOrder order)
        {
            config.DefaultCardOrder = order;
            OrderCandidates();
        }

        public void InitCustomCardOrder(string role = "All", string section = "All")
        {
            // melee opener
            if (role is "Melee" or "All")
            {
                if (section is "opener" or "All")
                    config.CustomCardOrder.Melee["opener"] = config.DefaultCardOrder.Melee["opener"].ToArray();
                if (section is "2m+" or "All")
                    config.CustomCardOrder.Melee["2m+"] = config.DefaultCardOrder.Melee["2m+"].ToArray();
            }

            // range opener
            if (role is "Range" or "All")
            {
                if (section is "opener" or "All")
                    config.CustomCardOrder.Range["opener"] = config.DefaultCardOrder.Range["opener"].ToArray();
                if (section is "2m+" or "All")
                    config.CustomCardOrder.Range["2m+"] = config.DefaultCardOrder.Range["2m+"].ToArray();
            }

            // reset order
            OrderCandidates();
        }

        public void OrderCandidates()
        {
            // reset candidates before select new candidates
            meleeCandidateOrder.Clear();
            rangeCandidateOrder.Clear();

            // find card candidates
            var partyList = DService.PartyList; // role [1 tank, 2 melee, 3 range, 4 healer]
            var isAST     = LocalPlayerState.ClassJob == 33;
            if (GameState.IsInPVPArea || partyList.Length < 2 || !isAST || config.AutoPlayCard == AutoPlayCardStatus.Disable)
                return;

            // is opener or 2m+?
            var sectionLabel = IsOpener ? "opener" : "2m+";

            // activate config (custom or default)
            var activateOrder = config.AutoPlayCard switch
            {
                AutoPlayCardStatus.Custom => config.CustomCardOrder,
                AutoPlayCardStatus.Default => config.DefaultCardOrder
            };

            // set candidate priority based on predefined order
            var meleeOrder = activateOrder.Melee[sectionLabel];
            for (var idx = 0; idx < meleeOrder.Length; idx++)
            {
                var member = partyList.FirstOrDefault(m => m.ClassJob.Value.NameEnglish == meleeOrder[idx]);
                if (member is not null && meleeCandidateOrder.All(m => m.id != member.ObjectId))
                    meleeCandidateOrder.Add((member.ObjectId, 5 - (idx * 0.1)));
            }

            var rangeOrder = activateOrder.Range[sectionLabel];
            for (var idx = 0; idx < rangeOrder.Length; idx++)
            {
                var member = partyList.FirstOrDefault(m => m.ClassJob.Value.NameEnglish == rangeOrder[idx]);
                if (member is not null && rangeCandidateOrder.All(m => m.id != member.ObjectId))
                    rangeCandidateOrder.Add((member.ObjectId, 5 - (idx * 0.1)));
            }

            // fallback: select the first dps in party list
            if (meleeCandidateOrder.Count is 0)
            {
                var firstRange = partyList.FirstOrDefault(m => m.ClassJob.Value.Role is 3);
                if (firstRange is not null)
                    meleeCandidateOrder.Add((firstRange.ObjectId, 1));
            }

            if (rangeCandidateOrder.Count is 0)
            {
                var firstMelee = partyList.FirstOrDefault(m => m.ClassJob.Value.Role is 2);
                if (firstMelee is not null)
                    rangeCandidateOrder.Add((firstMelee.ObjectId, 1));
            }

            // sort candidates by priority
            meleeCandidateOrder.Sort((a, b) => b.priority.CompareTo(a.priority));
            rangeCandidateOrder.Sort((a, b) => b.priority.CompareTo(a.priority));
        }

        private uint FetchCandidateId(string role)
        {
            var candidates = role switch
            {
                "Melee" => meleeCandidateOrder,
                "Range" => rangeCandidateOrder
            };

            var fallbackId       = LocalPlayerState.EntityID;
            var fallbackPriority = 0.0;

            foreach (var member in candidates)
            {
                var candidate = DService.PartyList.FirstOrDefault(m => m.ObjectId == member.id);
                if (candidate is null)
                    continue;

                // member skip conditions: out of range, dead, or weakened
                var maxDistance    = ActionManager.GetActionRange(37023);
                var memberDead     = candidate.GameObject.IsDead || candidate.CurrentHP <= 0;
                var memberDistance = Vector3.DistanceSquared(candidate.Position, DService.ObjectTable.LocalPlayer.Position);
                if (memberDead || memberDistance > maxDistance * maxDistance)
                    continue;

                // weakness: use as fallback
                if (candidate.Statuses.Any(x => x.StatusId is 43 or 44))
                {
                    fallbackId       = member.id;
                    fallbackPriority = member.priority;
                    continue;
                }

                // candidate vs fallback
                if (member.priority >= fallbackPriority - 2)
                    return member.id;
            }

            return fallbackId;
        }

        public void OnPrePlayCard(ref ulong targetId, ref uint actionId)
        {
            var partyMemberIds = DService.PartyList.Select(m => m.ObjectId).ToHashSet();
            if (!partyMemberIds.Contains((uint)targetId))
            {
                targetId = actionId switch
                {
                    37023 => FetchCandidateId("Melee"),
                    37026 => FetchCandidateId("Range")
                };

                var member = FetchMember((uint)targetId);
                if (member is not null)
                {
                    var name         = member.Name.ExtractText();
                    var classJobIcon = member.ClassJob.ValueNullable.ToBitmapFontIcon();
                    var classJobName = member.ClassJob.Value.Name.ExtractText();

                    var locKey = actionId switch
                    {
                        37023 => "Melee",
                        37026 => "Range"
                    };

                    if (ModuleConfig.SendChat)
                        Chat(GetSLoc($"HealerHelper-AutoPlayCard-Message-{locKey}", name, classJobIcon, classJobName));
                    if (ModuleConfig.SendNotification)
                        NotificationInfo(GetLoc($"HealerHelper-AutoPlayCard-Message-{locKey}", name, string.Empty, classJobName));
                }
            }
        }

        #endregion
    }

    #endregion

    #region EasyHeal

    private class EasyHealManager(EasyHealManager.Storage config)
    {
        // const
        public static readonly HashSet<uint> RaiseActions = [125, 173, 3603, 24287, 7670, 7523, 64556];

        // heal action status
        public bool TargetHealActionsLoaded => config.TargetHealActions.Count > 0;
        public bool ActiveHealActionsLoaded => config.ActiveHealActions.Count > 0;

        // alias
        public Dictionary<uint, HealAction> TargetHealActions => config.TargetHealActions;

        // config
        public class Storage
        {
            // easy heal
            public EasyHealStatus EasyHeal          = EasyHealStatus.Enable;
            public float          NeedHealThreshold = 0.92f;
            public OverhealTarget OverhealTarget    = OverhealTarget.Local;

            // heal actions
            public Dictionary<uint, HealAction> TargetHealActions = [];
            public HashSet<uint>                ActiveHealActions = [];

            // easy dispel
            public EasyDispelStatus  EasyDispel  = EasyDispelStatus.Enable;
            public DispelOrderStatus DispelOrder = DispelOrderStatus.Order;

            // easy raise
            public EasyRaiseStatus  EasyRaise  = EasyRaiseStatus.Enable;
            public RaiseOrderStatus RaiseOrder = RaiseOrderStatus.Order;
        }

        #region Structs

        public enum EasyHealStatus
        {
            Disable, // disable easy heal
            Enable   // select target with the lowest HP ratio within range when no target selected
        }

        public class HealAction
        {
            [JsonProperty("id")]
            public uint Id { get; private set; }

            [JsonProperty("name")]
            public string Name { get; private set; }

            [JsonProperty("on")]
            public bool On { get; private set; }
        }


        public enum OverhealTarget
        {
            Local,     // local player
            FirstTank, // first tank in party list
            Prevent    // prevent overheal
        }

        public enum EasyDispelStatus
        {
            Disable, // disable easy dispel
            Enable   // select target with dispellable status within range when no target selected
        }

        public enum DispelOrderStatus
        {
            Order,  // local -> party list (0 -> 7)
            Reverse // local -> party list (7 -> 0)
        }

        public enum EasyRaiseStatus
        {
            Disable, // disable easy raise
            Enable   // select target dead within range when no target selected
        }

        public enum RaiseOrderStatus
        {
            Order,  // local -> party list (0 -> 7)
            Reverse // local -> party list (7 -> 0)
        }

        #endregion

        #region Funcs

        public void InitTargetHealActions(Dictionary<uint, HealAction> actions)
            => config.TargetHealActions = actions;

        public void InitActiveHealActions()
            => config.ActiveHealActions = config.TargetHealActions.Where(act => act.Value.On).Select(act => act.Key).ToHashSet();

        private uint TargetNeedHeal(uint actionId)
        {
            var partyList  = DService.PartyList;
            var lowRatio   = 2f;
            var needHealId = UnspecificTargetId;

            foreach (var member in partyList)
            {
                if (member.ObjectId == 0)
                    continue;

                var maxDistance = ActionManager.GetActionRange(actionId);
                var withinRange = Vector3.DistanceSquared(member.Position, DService.ObjectTable.LocalPlayer.Position) <= maxDistance * maxDistance;
                var memberDead  = member.GameObject.IsDead || member.CurrentHP <= 0;
                if (memberDead || !withinRange)
                    continue;

                var ratio = member.CurrentHP / (float)member.MaxHP;
                if (ratio < lowRatio && ratio <= config.NeedHealThreshold)
                {
                    lowRatio   = ratio;
                    needHealId = member.ObjectId;
                }
            }

            return needHealId;
        }

        private static uint TargetNeedDispel(bool reverse = false)
        {
            var partyList = DService.PartyList;

            // first dispel local player
            var localStatus = DService.ObjectTable.LocalPlayer.StatusList;
            foreach (var status in localStatus)
            {
                if (PresetSheet.DispellableStatuses.ContainsKey(status.StatusId))
                    return LocalPlayerState.EntityID;
            }

            // dispel in order (or reverse order)
            var sortedPartyList = reverse
                                      ? partyList.OrderByDescending(member => FetchMemberIndex(member.ObjectId) ?? 0).ToList()
                                      : partyList.OrderBy(member => FetchMemberIndex(member.ObjectId) ?? 0).ToList();
            foreach (var member in sortedPartyList)
            {
                if (member.ObjectId == 0)
                    continue;

                var maxDistance = ActionManager.GetActionRange(7568);
                var withinRange = Vector3.DistanceSquared(member.Position, DService.ObjectTable.LocalPlayer.Position) <= maxDistance * maxDistance;
                var memberDead  = member.GameObject.IsDead || member.CurrentHP <= 0;
                if (memberDead || !withinRange)
                    continue;

                foreach (var status in member.Statuses)
                {
                    if (PresetSheet.DispellableStatuses.ContainsKey(status.StatusId))
                        return member.ObjectId;
                }
            }

            return UnspecificTargetId;
        }

        private static uint TargetNeedRaise(uint actionId, bool reverse = false)
        {
            var partyList = DService.PartyList;

            // raise in order (or reverse order)
            var sortedPartyList = reverse
                                      ? partyList.OrderByDescending(member => FetchMemberIndex(member.ObjectId) ?? 0).ToList()
                                      : partyList.OrderBy(member => FetchMemberIndex(member.ObjectId) ?? 0).ToList();
            foreach (var member in sortedPartyList)
            {
                if (member.ObjectId == 0)
                    continue;

                var maxDistance  = ActionManager.GetActionRange(actionId);
                var withinRange  = Vector3.DistanceSquared(member.Position, DService.ObjectTable.LocalPlayer.Position) <= maxDistance * maxDistance;
                var memberRaised = member.Statuses.Any(x => x.StatusId is 148);
                var memberDead   = member.GameObject.IsDead || member.CurrentHP <= 0;
                if (memberDead && !memberRaised && withinRange)
                    return member.ObjectId;
            }

            return UnspecificTargetId;
        }

        public static unsafe bool IsHealable(IGameObject? gameObject)
        {
            var battleChara = CharacterManager.Instance()->LookupBattleCharaByEntityId(gameObject.EntityId);
            return battleChara is not null && ActionManager.CanUseActionOnTarget(3595, (GameObject*)battleChara);
        }

        public void OnPreHeal(ref ulong targetId, ref uint actionId, ref bool isPrevented)
        {
            var currentTarget = DService.ObjectTable.SearchById(targetId);
            if (targetId == UnspecificTargetId || !IsHealable(currentTarget))
            {
                // find the target with the lowest HP ratio within range and satisfy the threshold
                targetId = TargetNeedHeal(actionId);
                if (targetId == UnspecificTargetId)
                {
                    switch (config.OverhealTarget)
                    {
                        case OverhealTarget.Prevent:
                            isPrevented = true;
                            return;

                        case OverhealTarget.Local:
                            targetId = LocalPlayerState.EntityID;
                            break;

                        case OverhealTarget.FirstTank:
                            var partyList       = DService.PartyList;
                            var sortedPartyList = partyList.OrderBy(member => FetchMemberIndex(member.ObjectId) ?? 0).ToList();
                            var firstTank       = sortedPartyList.FirstOrDefault(m => m.ClassJob.Value.Role == 1);
                            var maxDistance     = ActionManager.GetActionRange(actionId);
                            targetId = firstTank is not null &&
                                       Vector3.DistanceSquared(firstTank.Position, DService.ObjectTable.LocalPlayer.Position) <= maxDistance * maxDistance
                                           ? firstTank.ObjectId
                                           : LocalPlayerState.EntityID;
                            break;

                        default:
                            targetId = LocalPlayerState.EntityID;
                            break;
                    }
                }

                var member = FetchMember((uint)targetId);
                if (member is not null)
                {
                    var name         = member.Name.ExtractText();
                    var classJobIcon = member.ClassJob.ValueNullable.ToBitmapFontIcon();
                    var classJobName = member.ClassJob.Value.Name.ExtractText();

                    if (ModuleConfig.SendChat)
                        Chat(GetSLoc("HealerHelper-EasyHeal-Message", name, classJobIcon, classJobName));
                    if (ModuleConfig.SendNotification)
                        NotificationInfo(GetLoc("HealerHelper-EasyHeal-Message", name, string.Empty, classJobName));
                }
            }
        }

        public void OnPreDispel(ref ulong targetId, ref bool isPrevented)
        {
            var currentTarget = DService.ObjectTable.SearchById(targetId);
            if (currentTarget is IBattleNpc || targetId == UnspecificTargetId)
            {
                // find target with dispellable status within range
                targetId = TargetNeedDispel(config.DispelOrder is DispelOrderStatus.Reverse);
                if (targetId == UnspecificTargetId)
                {
                    isPrevented = true;
                    return;
                }

                // dispel target
                var member = FetchMember((uint)targetId);
                if (member is not null)
                {
                    var name         = member.Name.ExtractText();
                    var classJobIcon = member.ClassJob.ValueNullable.ToBitmapFontIcon();
                    var classJobName = member.ClassJob.Value.Name.ExtractText();

                    if (ModuleConfig.SendChat)
                        Chat(GetSLoc("HealerHelper-EasyDispel-Message", name, classJobIcon, classJobName));
                    if (ModuleConfig.SendNotification)
                        NotificationInfo(GetLoc("HealerHelper-EasyDispel-Message", name, string.Empty, classJobName));
                }
            }
        }

        public void OnPreRaise(ref ulong targetId, ref uint actionId, ref bool isPrevented)
        {
            var currentTarget = DService.ObjectTable.SearchById(targetId);
            if (currentTarget is IBattleNpc || targetId == UnspecificTargetId)
            {
                // find target with dead status within range
                targetId = TargetNeedRaise(actionId, config.RaiseOrder is RaiseOrderStatus.Reverse);
                if (targetId == UnspecificTargetId)
                {
                    isPrevented = true;
                    return;
                }

                // raise target
                var member = FetchMember((uint)targetId);
                if (member is not null)
                {
                    var name         = member.Name.ExtractText();
                    var classJobIcon = member.ClassJob.ValueNullable.ToBitmapFontIcon();
                    var classJobName = member.ClassJob.Value.Name.ExtractText();

                    if (ModuleConfig.SendChat)
                        Chat(GetSLoc("HealerHelper-EasyRaise-Message", name, classJobIcon, classJobName));
                    if (ModuleConfig.SendNotification)
                        NotificationInfo(GetLoc("HealerHelper-EasyRaise-Message", name, string.Empty, classJobName));
                }
            }
        }

        #endregion
    }

    #endregion

    #region Config

    private class ModuleStorage : ModuleConfiguration
    {
        // auto play card
        public AutoPlayCardManager.Storage AutoPlayCardStorage = new();

        // easy heal
        public EasyHealManager.Storage EasyHealStorage = new();

        // notification
        public bool SendChat;
        public bool SendNotification = true;
    }

    #endregion
}
