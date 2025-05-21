using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Web;
using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Party;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
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
    private static readonly Dictionary<ReadOnlySeString, ReadOnlySeString> JobNameMap;

    static HealerHelper()
    {
        JobNameMap = LuminaGetter.Get<ClassJob>()
                                 .ToDictionary(s => s.NameEnglish, s => s.Name);
    }

    // storage
    private static ModuleStorage? ModuleConfig;

    // managers
    public static EasyHealManager     EasyHealService;
    public static AutoPlayCardManager AutoPlayCardService;
    public static FFLogsManager       FFLogsService;

    public override void Init()
    {
        ModuleConfig = LoadConfig<ModuleStorage>() ?? new ModuleStorage();

        // fetch remote hotfix
        Task.Run(async () =>
        {
            await RemoteRepoManager.FetchPlayCardOrder();
            await RemoteRepoManager.FetchHealActions();
            await RemoteRepoManager.FetchTerritoryMap();
        });
        SaveConfig(ModuleConfig);

        // managers
        EasyHealService     = new EasyHealManager(ModuleConfig.EasyHealStorage);
        AutoPlayCardService = new AutoPlayCardManager(ModuleConfig.AutoPlayCardStorage);
        FFLogsService       = new FFLogsManager(ModuleConfig.FFLogsStorage);

        // register hooks
        UseActionManager.RegPreUseActionLocation(OnPreUseAction);
        DService.ClientState.TerritoryChanged += OnZoneChanged;
        DService.DutyState.DutyRecommenced    += OnDutyRecommenced;
        DService.Condition.ConditionChange    += OnConditionChanged;
        FrameworkManager.Register(OnUpdate, throttleMS: 5_000);
    }

    public override void Uninit()
    {
        UseActionManager.UnregPreUseActionLocation(OnPreUseAction);
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        DService.DutyState.DutyRecommenced    -= OnDutyRecommenced;
        DService.Condition.ConditionChange    -= OnConditionChanged;
        FrameworkManager.Unregister(OnUpdate);

        base.Uninit();
    }

    #endregion

    #region UI

    private static int?   CustomCardOrderDragIndex;
    private static string ActionSearchInput = string.Empty;

    public override void ConfigUI()
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
        var logsConfig = ModuleConfig.FFLogsStorage;

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

            if (ImGui.RadioButton($"{GetLoc("Advance")} ({GetLoc("HealerHelper-AutoPlayCard-AdvanceDescription")})",
                                  cardConfig.AutoPlayCard == AutoPlayCardManager.AutoPlayCardStatus.Advance))
            {
                cardConfig.AutoPlayCard = AutoPlayCardManager.AutoPlayCardStatus.Advance;
                SaveConfig(ModuleConfig);
            }

            // Api Key [v1] for fetching FFLogs records (auto play card advance mode)
            if (cardConfig.AutoPlayCard == AutoPlayCardManager.AutoPlayCardStatus.Advance)
            {
                ImGui.Spacing();

                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(LightYellow, $"{GetLoc("HealerHelper-DuringTestDescription")}");

                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(LightGoldenrod, "FFLogs V1 API Key");

                ImGui.Spacing();

                if (ImGui.InputText("##FFLogsAPIKey", ref logsConfig.AuthKey, 32))
                    SaveConfig(ModuleConfig);

                ImGui.SameLine();
                if (ImGui.Button(GetLoc("Save")))
                {
                    if (string.IsNullOrWhiteSpace(logsConfig.AuthKey) || logsConfig.AuthKey.Length != 32)
                        logsConfig.KeyValid = false;
                    else
                        DService.Framework.RunOnTick(async () => await FFLogsService.IsKeyValid());
                    SaveConfig(ModuleConfig);
                }

                // key status (valid or invalid)
                ImGui.Spacing();

                ImGui.AlignTextToFramePadding();
                ImGui.Text(GetLoc("HealerHelper-LogsApi-Status"));

                ImGui.SameLine();
                if (logsConfig.KeyValid)
                    ImGui.TextColored(LightGreen, GetLoc("Connected"));
                else
                    ImGui.TextColored(LightPink, GetLoc("Disconnected"));
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
                CustomCardOrderDragIndex = index;
                ImGui.SetDragDropPayload("##CustomCardOrder", nint.Zero, 0);
                ImGui.EndDragDropSource();
            }

            if (ImGui.BeginDragDropTarget())
            {
                ImGui.AcceptDragDropPayload("##CustomCardOrder");
                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && CustomCardOrderDragIndex.HasValue)
                {
                    (cardOrder[index], cardOrder[CustomCardOrderDragIndex.Value]) = (cardOrder[CustomCardOrderDragIndex.Value], cardOrder[index]);

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
        var config = ModuleConfig.EasyHealStorage;

        ImGui.TextColored(YellowGreen, $"{GetLoc("HealerHelper-EasyHeal-ActiveHealAction")}");
        ImGui.Spacing();

        var actionList = config.TargetHealActions
                               .ToDictionary(act => act.Key, act => LuminaGetter.GetRow<LuminaAction>(act.Key)!.Value);
        MultiSelectCombo(actionList,
                         ref config.ActiveHealActions,
                         ref ActionSearchInput,
                         [
                             new(GetLoc("Action"), ImGuiTableColumnFlags.WidthStretch, 20),
                             new(GetLoc("Job"), ImGuiTableColumnFlags.WidthStretch, 10)
                         ],
                         [
                             x => () =>
                             {
                                 if (!DService.Texture.TryGetFromGameIcon((uint)x.Icon, out var actionIcon))
                                     return;
                                 using var id = ImRaii.PushId($"{x.RowId}");

                                 // icon - action name
                                 ImGui.TableSetColumnIndex(1);
                                 if (ImGuiOm.SelectableImageWithText(
                                         actionIcon.GetWrapOrEmpty().ImGuiHandle,
                                         new(ImGui.GetTextLineHeightWithSpacing()),
                                         x.Name.ExtractText(),
                                         config.ActiveHealActions.Contains(x.RowId),
                                         ImGuiSelectableFlags.DontClosePopups))
                                 {
                                     if (!config.ActiveHealActions.Remove(x.RowId))
                                         config.ActiveHealActions.Add(x.RowId);
                                 }

                                 // show action description
                                 // var desc = LuminaGetter.GetRow<ActionTransient>(x.RowId).Value.Description;

                                 // job
                                 ImGui.TableSetColumnIndex(2);
                                 ImGui.Text(x.ClassJobCategory.Value.Name.ExtractText());
                             }
                         ],
                         [x => x.Name.ExtractText() + x.ClassJobCategory.Value.Name.ExtractText()]
        );
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
        ref ulong targetId,    ref Vector3    location, ref uint extraParam)
    {
        if (type != ActionType.Action || GameState.IsInPVPArea || DService.PartyList.Length < 2)
            return;

        // job check
        var isHealer = GameState.ClassJobData.Role == 4;

        // healer related
        if (isHealer)
        {
            var isAST = GameState.ClassJob == 33;

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
                EasyHealService.OnPreDispel(ref targetId, ref actionId, ref isPrevented);
        }

        // can raise
        var canRaise = isHealer || GameState.ClassJob is 27 or 35;
        if (canRaise)
        {
            // easy raise
            var healConfig = ModuleConfig.EasyHealStorage;
            if (healConfig.EasyRaise == EasyHealManager.EasyRaiseStatus.Enable && EasyHealManager.RaiseActions.Contains(actionId))
                EasyHealService.OnPreRaise(ref targetId, ref actionId, ref isPrevented);
        }
    }

    private void OnZoneChanged(ushort zone)
        => FFLogsService.ClearBestRecords();

    private static void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag is ConditionFlag.InCombat)
        {
            AutoPlayCardService.IsOpener    = true;
            AutoPlayCardService.NeedReorder = false;
        }
        else
        {
            AutoPlayCardService.IsOpener    = false;
            AutoPlayCardService.NeedReorder = true;
        }
    }

    private static void OnDutyRecommenced(object? sender, ushort e)
        => AutoPlayCardService.OrderCandidates();

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

    public static class RemoteRepoManager
    {
        // const
        private const string Uri = "https://dr-cache.sumemo.dev";

        public static async Task FetchPlayCardOrder()
        {
            try
            {
                var json = await HttpClientHelper.Get().GetStringAsync($"{Uri}/card-order");
                var resp = JsonConvert.DeserializeObject<AutoPlayCardManager.PlayCardOrder>(json);
                if (resp == null)
                    Error($"[HealerHelper] Deserialize Default Play Card Order Failed: {json}");
                else
                {
                    ModuleConfig.AutoPlayCardStorage.DefaultCardOrder = resp;
                    // init custom if empty
                    if (ModuleConfig.AutoPlayCardStorage.CustomCardOrder.Melee.Count is 0)
                        AutoPlayCardService.InitCustomCardOrder();
                }
            }
            catch (Exception ex) { Error($"[HealerHelper] Fetch Default Play Card Order Failed: {ex}"); }
        }

        public static async Task FetchHealActions()
        {
            try
            {
                var json = await HttpClientHelper.Get().GetStringAsync($"{Uri}/heal-actions");
                var resp = JsonConvert.DeserializeObject<Dictionary<string, List<EasyHealManager.HealAction>>>(json);
                if (resp == null)
                    Error($"[HealerHelper] Deserialize Default Heal Actions Failed: {json}");
                else
                {
                    ModuleConfig.EasyHealStorage.TargetHealActions = resp.SelectMany(kv => kv.Value).ToDictionary(act => act.Id, act => act);

                    // when active is empty, set with default on heal actions
                    if (ModuleConfig.EasyHealStorage.ActiveHealActions.Count is 0)
                        EasyHealService.InitActiveHealActions();
                }
            }
            catch (Exception ex) { Error($"[HealerHelper] Fetch Default Heal Actions Failed: {ex}"); }
        }

        public static async Task FetchTerritoryMap()
        {
            try
            {
                var json = await HttpClientHelper.Get().GetStringAsync($"{Uri}/territory");
                var resp = JsonConvert.DeserializeObject<List<FFLogsManager.TerritoryMap>>(json);
                if (resp == null)
                    Error($"[HealerHelper] Deserialize Territory Map Failed: {json}");
                else
                {
                    ModuleConfig.FFLogsStorage.TerritoryMaps = resp;
                    FFLogsService.InitTerritoryDict();
                }
            }
            catch (Exception ex) { Error($"[HealerHelper] Fetch Territory Map Failed: {ex}"); }
        }
    }

    #endregion

    #region AutoPlayCard

    public class AutoPlayCardManager(AutoPlayCardManager.Storage config)
    {
        // const
        public static readonly HashSet<uint> PlayCardActions = [37023, 37026];

        // cache
        public HashSet<uint> PartyMemberIdsCache = []; // check party member changed or not

        private readonly List<(uint id, double priority)> meleeCandidateOrder = [];
        private readonly List<(uint id, double priority)> rangeCandidateOrder = [];

        public bool IsOpener;
        public bool NeedReorder;

        // config
        public class Storage
        {
            public AutoPlayCardStatus AutoPlayCard     = AutoPlayCardStatus.Default;
            public PlayCardOrder      DefaultCardOrder = new();
            public PlayCardOrder      CustomCardOrder  = new();
        }

        #region Structs

        public enum AutoPlayCardStatus
        {
            Disable, // disable auto play card
            Default, // select target based on predefined order when no target selected
            Advance, // select target based on FFLogs rDPS records when no target selected
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

        #endregion

        #region Funcs

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
            var isAST     = GameState.ClassJob == 33;
            if (GameState.IsInPVPArea || partyList.Length < 2 || !isAST || config.AutoPlayCard == AutoPlayCardStatus.Disable)
                return;

            // advance fallback when no valid zone id or invalid key
            var advance = ModuleConfig is { AutoPlayCardStorage.AutoPlayCard: AutoPlayCardStatus.Advance, FFLogsStorage.KeyValid: false };
            if (!FFLogsService.TerritoryDict.ContainsKey(GameState.TerritoryType) && advance)
            {
                if (FFLogsService.FirstTimeFallback)
                {
                    Chat(GetLoc("HealerHelper-AutoPlayCard-AdvanceFallback"));
                    FFLogsService.FirstTimeFallback = false;
                }

                config.AutoPlayCard = AutoPlayCardStatus.Default;
            }

            // is opener or 2m+?
            var sectionLabel = IsOpener ? "opener" : "2m+";

            // activate config (custom or default)
            var activateOrder = config.AutoPlayCard switch
            {
                AutoPlayCardStatus.Custom => config.CustomCardOrder,
                _ => config.DefaultCardOrder
            };

            // set candidate priority based on predefined order
            var meleeOrder = activateOrder.Melee[sectionLabel];
            for (var idx = 0; idx < meleeOrder.Length; idx++)
            {
                var member = partyList.FirstOrDefault(m => m.ClassJob.Value.NameEnglish == meleeOrder[idx]);
                if (member is not null && meleeCandidateOrder.All(m => m.id != member.ObjectId))
                    meleeCandidateOrder.Add((member.ObjectId, 2 - (idx * 0.1)));
            }

            var rangeOrder = activateOrder.Range[sectionLabel];
            for (var idx = 0; idx < rangeOrder.Length; idx++)
            {
                var member = partyList.FirstOrDefault(m => m.ClassJob.Value.NameEnglish == rangeOrder[idx]);
                if (member is not null && rangeCandidateOrder.All(m => m.id != member.ObjectId))
                    rangeCandidateOrder.Add((member.ObjectId, 2 - (idx * 0.1)));
            }

            // adjust candidate priority based on FFLogs records (auto play card advance mode)
            if (config.AutoPlayCard == AutoPlayCardStatus.Advance)
            {
                foreach (var member in partyList)
                {
                    var bestRecord = FFLogsService.FetchBestRecord((ushort)GameState.TerritoryType, member).GetAwaiter().GetResult();
                    if (bestRecord is null)
                        continue;

                    // scale priority based on sigmoid percentile
                    var scale = 1 / (1 + Math.Exp(-(bestRecord.Percentile - 50) / 8.33));

                    switch (member.ClassJob.Value.Role)
                    {
                        // update priority
                        case 1 or 2:
                        {
                            var idx = meleeCandidateOrder.FindIndex(m => m.id == member.ObjectId);
                            if (idx != -1)
                            {
                                var priority = meleeCandidateOrder[idx].priority * scale;
                                meleeCandidateOrder[idx] = (member.ObjectId, priority);
                            }

                            break;
                        }
                        case 3:
                        {
                            var idx = rangeCandidateOrder.FindIndex(m => m.id == member.ObjectId);
                            if (idx != -1)
                            {
                                var priority = rangeCandidateOrder[idx].priority * scale;
                                rangeCandidateOrder[idx] = (member.ObjectId, priority);
                            }

                            break;
                        }
                    }
                }
            }

            // fallback: select the first dps in party list
            if (meleeCandidateOrder.Count is 0)
            {
                var firstRange = partyList.FirstOrDefault(m => m.ClassJob.Value.Role is 1 or 3);
                if (firstRange is not null)
                    meleeCandidateOrder.Add((firstRange.ObjectId, -5));
            }

            if (rangeCandidateOrder.Count is 0)
            {
                var firstMelee = partyList.FirstOrDefault(m => m.ClassJob.Value.Role is 2);
                if (firstMelee is not null)
                    rangeCandidateOrder.Add((firstMelee.ObjectId, -5));
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
                "Range" => rangeCandidateOrder,
                _ => throw new ArgumentOutOfRangeException(nameof(role))
            };

            var needResort = false;
            for (var i = 0; i < candidates.Count; i++)
            {
                var member    = candidates[i];
                var candidate = DService.PartyList.FirstOrDefault(m => m.ObjectId == member.id);
                if (candidate is null)
                    continue;

                // skip dead member in this round (refresh on duty recommenced)
                if (candidate.CurrentHP <= 0)
                {
                    switch (role)
                    {
                        case "Melee":
                            meleeCandidateOrder[i] = (candidate.ObjectId, -2);
                            break;
                        case "Range":
                            rangeCandidateOrder[i] = (candidate.ObjectId, -2);
                            break;
                    }

                    needResort = true;
                    continue;
                }

                // skip member out of range for this action
                var maxDistance = ActionManager.GetActionRange(37023);
                var memberDead  = candidate.GameObject.IsDead || candidate.CurrentHP <= 0;
                if (memberDead ||
                    Vector3.DistanceSquared(candidate.Position, DService.ClientState.LocalPlayer.Position) > maxDistance * maxDistance)
                    continue;

                return member.id;
            }

            if (needResort)
                candidates.Sort((a, b) => b.priority.CompareTo(a.priority));

            return GameState.EntityID;
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
                if (member != null)
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

            // mark opener end
            if (actionId is 37026 && IsOpener)
            {
                IsOpener    = false;
                NeedReorder = true;
            }
        }

        #endregion
    }

    #endregion

    #region EasyHeal

    public class EasyHealManager(EasyHealManager.Storage config)
    {
        // const
        public static readonly HashSet<uint> RaiseActions = [125, 173, 3603, 24287, 7670, 7523];

        // config
        public class Storage
        {
            // easy heal
            public EasyHealStatus EasyHeal          = EasyHealStatus.Enable;
            public float          NeedHealThreshold = 0.92f;
            public OverhealTarget OverhealTarget    = OverhealTarget.Local;

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

        public void InitActiveHealActions()
            => config.ActiveHealActions = config.TargetHealActions.Where(act => act.Value.On).Select(act => act.Key).ToHashSet();

        private uint TargetNeedHeal(uint actionId)
        {
            var partyList  = DService.PartyList;
            var lowRatio   = 2f;
            var needHealId = UnspecificTargetId;

            foreach (var member in partyList)
            {
                var maxDistance = ActionManager.GetActionRange(actionId);
                var memberDead  = member.GameObject.IsDead || member.CurrentHP <= 0;
                if (memberDead ||
                    Vector3.DistanceSquared(member.Position, DService.ClientState.LocalPlayer.Position) > maxDistance * maxDistance)
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
            var localStatus = DService.ClientState.LocalPlayer.StatusList;
            foreach (var status in localStatus)
            {
                if (PresetSheet.DispellableStatuses.ContainsKey(status.StatusId))
                    return GameState.EntityID;
            }

            // dispel in order (or reverse order)
            var sortedPartyList = reverse
                                      ? partyList.OrderByDescending(member => FetchMemberIndex(member.ObjectId) ?? 0).ToList()
                                      : partyList.OrderBy(member => FetchMemberIndex(member.ObjectId) ?? 0).ToList();
            foreach (var member in sortedPartyList)
            {
                var maxDistance = ActionManager.GetActionRange(7568);
                var memberDead  = member.GameObject.IsDead || member.CurrentHP <= 0;
                if (memberDead ||
                    Vector3.DistanceSquared(member.Position, DService.ClientState.LocalPlayer.Position) > maxDistance * maxDistance)
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
                var maxDistance = ActionManager.GetActionRange(actionId);
                var memberDead  = member.GameObject.IsDead || member.CurrentHP <= 0;
                if (memberDead &&
                    Vector3.DistanceSquared(member.Position, DService.ClientState.LocalPlayer.Position) <= maxDistance * maxDistance)
                    return member.ObjectId;
            }

            return UnspecificTargetId;
        }

        public void OnPreHeal(ref ulong targetId, ref uint actionId, ref bool isPrevented)
        {
            var currentTarget = DService.ObjectTable.SearchById(targetId);
            if (currentTarget is IBattleNpc || targetId == UnspecificTargetId)
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
                            targetId = GameState.EntityID;
                            break;

                        case OverhealTarget.FirstTank:
                            var partyList       = DService.PartyList;
                            var sortedPartyList = partyList.OrderBy(member => FetchMemberIndex(member.ObjectId) ?? 0).ToList();
                            var firstTank       = sortedPartyList.FirstOrDefault(m => m.ClassJob.Value.Role == 1);
                            var maxDistance     = ActionManager.GetActionRange(actionId);
                            targetId = firstTank is not null &&
                                       Vector3.DistanceSquared(firstTank.Position, DService.ClientState.LocalPlayer.Position) <= maxDistance * maxDistance
                                           ? firstTank.ObjectId
                                           : GameState.EntityID;
                            break;

                        default:
                            targetId = GameState.EntityID;
                            break;
                    }
                }

                var member = FetchMember((uint)targetId);
                if (member != null)
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

        public void OnPreDispel(ref ulong targetId, ref uint actionId, ref bool isPrevented)
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
                if (member != null)
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
                if (member != null)
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

    #region FFLogs

    public class FFLogsManager(FFLogsManager.Storage config)
    {
        #region Params

        // const
        private const string Uri = "https://www.fflogs.com/v1";

        // cache
        public readonly Dictionary<uint, LogsRecord> MemberBestRecords = new();
        public          Dictionary<uint, uint>       TerritoryDict     = new();
        public          bool                         FirstTimeFallback = true;


        // config
        public class Storage
        {
            public string             AuthKey = string.Empty;
            public bool               KeyValid;
            public List<TerritoryMap> TerritoryMaps = new();
        }

        #endregion

        #region Structs

        // Dalamud-FFLogs zone match map (ultimates and current savage)
        // TerritoryType - FFLogs Zone ID
        // load from su-cache:territory
        public class TerritoryMap
        {
            [JsonProperty("id")]
            public uint Id { get; private set; }

            [JsonProperty("name")]
            public string Name { get; private set; }

            [JsonProperty("logs_zone")]
            public uint LogsZone { get; private set; }
        }

        public class LogsRecord
        {
            // job english name
            [JsonProperty("spec")]
            public string JobName { get; private set; }

            // record difficulty
            [JsonProperty("difficulty")]
            public int Difficulty { get; private set; }

            // DPS
            [JsonProperty("total")]
            public double DPS { get; private set; }

            // percentile
            [JsonProperty("percentile")]
            public double Percentile { get; private set; }
        }

        #endregion

        #region Funcs

        public async Task IsKeyValid()
        {
            try
            {
                var uri      = $"{Uri}/classes?api_key={config.AuthKey}";
                var response = await HttpClientHelper.Get().GetStringAsync(uri);
                config.KeyValid   = !string.IsNullOrWhiteSpace(response);
                FirstTimeFallback = true;
            }
            catch (Exception) { config.KeyValid = false; }
        }

        private static string FetchRegion()
        {
            return GameState.CurrentDataCenter switch
            {
                1 => "JP",
                2 => "NA",
                3 => "EU",
                4 => "OC",
                5 => "CN",
                _ => string.Empty
            };
        }

        public async Task<LogsRecord?> FetchBestRecord(ushort zone, IPartyMember member)
        {
            // find in cache
            if (MemberBestRecords.TryGetValue(member.ObjectId, out var bestRecord))
                return bestRecord;

            // get character info
            var charaName  = member.Name;
            var serverSlug = member.World.Value.Name.ExtractText();
            var job        = member.ClassJob.Value.NameEnglish.ExtractText();

            // fetch record
            try
            {
                var uri   = $"{Uri}/parses/character/{charaName}/{serverSlug}/{FetchRegion()}";
                var query = HttpUtility.ParseQueryString(string.Empty);
                query["api_key"]   = config.AuthKey;
                query["encounter"] = config.TerritoryMaps[zone].ToString();
                query["metric"]    = "ndps";

                // contains all ultimates and current savage in current version
                var response = await HttpClientHelper.Get().GetStringAsync($"{uri}?{query}");
                var records  = JsonConvert.DeserializeObject<LogsRecord[]>(response);
                if (records == null || records.Length == 0)
                    return null;

                // find best record
                bestRecord = records.Where(r => r.JobName == job)
                                    .OrderByDescending(r => r.Difficulty)
                                    .ThenByDescending(r => r.DPS)
                                    .FirstOrDefault();
                MemberBestRecords[member.ObjectId] = bestRecord;
                return bestRecord;
            }
            catch (Exception) { return null; }
        }

        public void ClearBestRecords()
            => MemberBestRecords.Clear();

        public void InitTerritoryDict()
            => TerritoryDict = config.TerritoryMaps.ToDictionary(x => x.Id, x => x.LogsZone);

        #endregion
    }

    #endregion

    #region Config

    private class ModuleStorage : ModuleConfiguration
    {
        // auto play card
        public readonly AutoPlayCardManager.Storage AutoPlayCardStorage = new();

        // FFLogs
        public readonly FFLogsManager.Storage FFLogsStorage = new();

        // easy heal
        public readonly EasyHealManager.Storage EasyHealStorage = new();

        // notification
        public bool SendChat;
        public bool SendNotification = true;
    }

    #endregion
}
