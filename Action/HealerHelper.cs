using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Web;
using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Party;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using Newtonsoft.Json;
using LuminaAction = Lumina.Excel.Sheets.Action;
using Status = Lumina.Excel.Sheets.Status;

namespace DailyRoutines.ModulesPublic;

public class HealerHelper : DailyModuleBase
{
    #region Core

    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("HealerHelperTitle"),
        Description = GetLoc("HealerHelperDescription"),
        Category    = ModuleCategories.Action,
        Author      = ["HaKu"]
    };

    private static Config? ModuleConfig;

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new Config();

        // task helper for select candidates
        TaskHelper ??= new TaskHelper { TimeLimitMS = 20_000 };

        // fetch remote resource
        DService.Framework.RunOnTick(async () => await FetchRemoteVersion());

        // build dispellable status dict
        DispellableStatus = LuminaGetter.Get<Status>()
                                        .Where(s => s is { CanDispel: true, Name.IsEmpty: false })
                                        .ToDictionary(s => s.RowId, s => s.Name.ExtractText().ToLowerInvariant());

        // build job names mapping
        JobNameMap = LuminaGetter.Get<ClassJob>()
                                 .ToDictionary(s => s.NameEnglish, s => s.Name);

        // life cycle hooks
        UseActionManager.Register(OnPreUseAction);
        DService.ClientState.TerritoryChanged += OnZoneChanged;
        DService.DutyState.DutyRecommenced    += OnDutyRecommenced;
        DService.Condition.ConditionChange    += OnConditionChanged;
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "_PartyList", OnPartyListPostDraw);
    }

    public override void Uninit()
    {
        UseActionManager.Unregister(OnPreUseAction);
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        DService.DutyState.DutyRecommenced    -= OnDutyRecommenced;
        DService.Condition.ConditionChange    -= OnConditionChanged;
        DService.AddonLifecycle.UnregisterListener(AddonEvent.PostDraw, OnPartyListPostDraw);

        base.Uninit();
    }

    #endregion

    #region UI

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
        ImGui.TextColored(LightSkyBlue, GetLoc("HealerHelper-AutoPlayCardTitle"));
        ImGuiOm.HelpMarker(GetLoc("HealerHelper-EasyRedirectDescription", LuminaWrapper.GetActionName(17055)));

        ImGui.Spacing();

        using (ImRaii.PushIndent())
        {
            if (ImGui.RadioButton($"{GetLoc("Disable")}##autocard",
                                  ModuleConfig.AutoPlayCard == AutoPlayCardStatus.Disable))
            {
                ModuleConfig.AutoPlayCard = AutoPlayCardStatus.Disable;
                SaveConfig(ModuleConfig);
            }

            if (ImGui.RadioButton($"{GetLoc("Common")} ({GetLoc("HealerHelper-AutoPlayCard-CommonDescription")})",
                                  ModuleConfig.AutoPlayCard == AutoPlayCardStatus.Default))
            {
                ModuleConfig.AutoPlayCard = AutoPlayCardStatus.Default;
                SaveConfig(ModuleConfig);
            }

            if (ImGui.RadioButton($"{GetLoc("Advance")} ({GetLoc("HealerHelper-AutoPlayCard-AdvanceDescription")})",
                                  ModuleConfig.AutoPlayCard == AutoPlayCardStatus.Advance))
            {
                ModuleConfig.AutoPlayCard = AutoPlayCardStatus.Advance;
                SaveConfig(ModuleConfig);
            }

            // Api Key [v1] for fetching FFLogs records (auto play card advance mode)
            if (ModuleConfig.AutoPlayCard == AutoPlayCardStatus.Advance)
            {
                ImGui.Spacing();

                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(LightYellow, $"{GetLoc("HealerHelper-DuringTestDescription")}");

                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(LightGoldenrod, "FFLogs V1 API Key");

                ImGui.Spacing();

                if (ImGui.InputText("##FFLogsAPIKey", ref ModuleConfig.FFLogsAPIKey, 32))
                    SaveConfig(ModuleConfig);

                ImGui.SameLine();
                if (ImGui.Button(GetLoc("Save")))
                {
                    if (string.IsNullOrWhiteSpace(ModuleConfig.FFLogsAPIKey) || ModuleConfig.FFLogsAPIKey.Length != 32)
                    {
                        ModuleConfig.KeyValid = false;
                        SaveConfig(ModuleConfig);
                    }
                    else
                        DService.Framework.RunOnTick(async () => await CheckKeyStatus());
                }

                // key status (valid or invalid)
                ImGui.Spacing();

                ImGui.AlignTextToFramePadding();
                ImGui.Text(GetLoc("HealerHelper-LogsApi-Status"));

                ImGui.SameLine();
                if (ModuleConfig.KeyValid)
                    ImGui.TextColored(LightGreen, GetLoc("Connected"));
                else
                    ImGui.TextColored(LightPink, GetLoc("Disconnected"));
            }

            if (ImGui.RadioButton($"{GetLoc("Custom")} ({GetLoc("HealerHelper-AutoPlayCard-CustomDescription")})",
                                  ModuleConfig.AutoPlayCard == AutoPlayCardStatus.Custom))
            {
                ModuleConfig.AutoPlayCard = AutoPlayCardStatus.Custom;
                SaveConfig(ModuleConfig);
            }

            if (ModuleConfig.AutoPlayCard == AutoPlayCardStatus.Custom)
            {
                ImGui.Spacing();
                CustomCardUI();
            }
        }
    }

    private void CustomCardUI()
    {
        // melee opener
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightYellow, $"{GetLoc("HealerHelper-AutoPlayCard-MeleeOpener")}");

        if (CustomCardOrderUI(ModuleConfig.CustomCardOrder.Melee["opener"]))
        {
            SaveConfig(ModuleConfig);
            OrderCandidates();
        }

        ImGui.SameLine();
        ImGui.Dummy(new Vector2(5, 0));
        ImGui.SameLine();
        if (ImGui.Button($"{GetLoc("Reset")}##meleeopener"))
            ResetCustomCardOrder("Melee", "opener");

        ImGui.Spacing();

        // melee 2m+
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightYellow, $"{GetLoc("HealerHelper-AutoPlayCard-Melee2Min")}");

        if (CustomCardOrderUI(ModuleConfig.CustomCardOrder.Melee["2m+"]))
        {
            SaveConfig(ModuleConfig);
            OrderCandidates();
        }

        ImGui.SameLine();
        ImGui.Dummy(new Vector2(5, 0));
        ImGui.SameLine();
        if (ImGui.Button($"{GetLoc("Reset")}##melee2m"))
            ResetCustomCardOrder("Melee", "2m+");

        ImGui.Spacing();

        // range opener
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightYellow, $"{GetLoc("HealerHelper-AutoPlayCard-RangeOpener")}");

        if (CustomCardOrderUI(ModuleConfig.CustomCardOrder.Range["opener"]))
        {
            SaveConfig(ModuleConfig);
            OrderCandidates();
        }

        ImGui.SameLine();
        ImGui.Dummy(new Vector2(5, 0));
        ImGui.SameLine();
        if (ImGui.Button($"{GetLoc("Reset")}##rangeopener"))
            ResetCustomCardOrder("Range", "opener");

        ImGui.Spacing();

        // range opener
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightYellow, $"{GetLoc("HealerHelper-AutoPlayCard-Range2Min")}");

        if (CustomCardOrderUI(ModuleConfig.CustomCardOrder.Range["2m+"]))
        {
            SaveConfig(ModuleConfig);
            OrderCandidates();
        }

        ImGui.SameLine();
        ImGui.Dummy(new Vector2(5, 0));
        ImGui.SameLine();
        if (ImGui.Button($"{GetLoc("Reset")}##range2m"))
            ResetCustomCardOrder("Range", "2m+");

        SaveConfig(ModuleConfig);
    }

    private        int?                                           dargIdx;
    private static Dictionary<ReadOnlySeString, ReadOnlySeString> JobNameMap = new();

    private bool CustomCardOrderUI(string[] cardOrder)
    {
        var modified = false;

        for (var idx = 0; idx < cardOrder.Length; idx++)
        {
            ImGui.PushID(idx);

            // component
            var jobName  = JobNameMap[cardOrder[idx]].ExtractText();
            var textSize = ImGui.CalcTextSize(jobName);
            if (ImGui.Button(jobName, new Vector2(textSize.X + 20, 0))) { }

            if (idx != cardOrder.Length - 1)
                ImGui.SameLine();

            if (ImGui.BeginDragDropSource())
            {
                dargIdx = idx;
                ImGui.SetDragDropPayload("##CustomCardOrder", IntPtr.Zero, 0);
                ImGui.EndDragDropSource();
            }

            if (ImGui.BeginDragDropTarget())
            {
                ImGui.AcceptDragDropPayload("##CustomCardOrder");
                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && dargIdx.HasValue)
                {
                    (cardOrder[idx], cardOrder[dargIdx.Value]) = (cardOrder[dargIdx.Value], cardOrder[idx]);
                    modified                                   = true;
                }

                ImGui.EndDragDropTarget();
            }

            ImGui.PopID();
        }

        return modified;
    }

    private void EasyHealUI()
    {
        ImGui.TextColored(LightSkyBlue, GetLoc("HealerHelper-EasyHealTitle"));
        ImGuiOm.HelpMarker(GetLoc("HealerHelper-EasyRedirectDescription", GetLoc("HealerHelper-SingleTargetHeal")));

        ImGui.Spacing();

        using (ImRaii.PushIndent())
        {
            if (ImGui.RadioButton($"{GetLoc("Disable")}##easyheal",
                                  ModuleConfig.EasyHeal == EasyHealStatus.Disable))
            {
                ModuleConfig.EasyHeal = EasyHealStatus.Disable;
                SaveConfig(ModuleConfig);
            }

            if (ImGui.RadioButton($"{GetLoc("Enable")} ({GetLoc("HealerHelper-EasyHeal-EnableDescription")})",
                                  ModuleConfig.EasyHeal == EasyHealStatus.Enable))
            {
                ModuleConfig.EasyHeal = EasyHealStatus.Enable;
                SaveConfig(ModuleConfig);
            }

            // heal threshold
            if (ModuleConfig.EasyHeal == EasyHealStatus.Enable)
            {
                ImGui.Spacing();

                ActiveHealActionsSelect();

                ImGui.Spacing();

                ImGui.TextColored(LightGreen, GetLoc("HealerHelper-EasyHeal-HealThreshold"));
                ImGuiOm.HelpMarker(GetLoc("HealerHelper-EasyHeal-HealThresholdHelp"));

                ImGui.Spacing();

                if (ImGui.SliderFloat("##HealThreshold", ref ModuleConfig.NeedHealThreshold, 0.0f, 1.0f, "%.2f"))
                    SaveConfig(ModuleConfig);

                // all time heal warning
                if (ModuleConfig.NeedHealThreshold > 0.92f)
                {
                    ImGui.Spacing();
                    ImGui.TextColored(Orange, GetLoc("HealerHelper-EasyHeal-OverhealWarning"));
                }

                ImGui.Spacing();

                // target when overheal
                ImGui.TextColored(LightPink, GetLoc("HealerHelper-EasyHeal-OverhealTargetDescription"));

                ImGui.Spacing();

                if (ImGui.RadioButton($"{GetLoc("HealerHelper-EasyHeal-OverhealTarget-Prevent")}##overhealtarget",
                                      ModuleConfig.OverhealTarget == OverhealTarget.Prevent))
                {
                    ModuleConfig.OverhealTarget = OverhealTarget.Prevent;
                    SaveConfig(ModuleConfig);
                }

                ImGui.SameLine();
                ImGui.Dummy(new Vector2(5, 0));
                ImGui.SameLine();
                if (ImGui.RadioButton($"{GetLoc("HealerHelper-EasyHeal-OverhealTarget-Local")}##overhealtarget",
                                      ModuleConfig.OverhealTarget == OverhealTarget.Local))
                {
                    ModuleConfig.OverhealTarget = OverhealTarget.Local;
                    SaveConfig(ModuleConfig);
                }

                ImGui.SameLine();
                ImGui.Dummy(new Vector2(5, 0));
                ImGui.SameLine();
                if (ImGui.RadioButton($"{GetLoc("HealerHelper-EasyHeal-OverhealTarget-FirstTank")}##overhealtarget",
                                      ModuleConfig.OverhealTarget == OverhealTarget.FirstTank))
                {
                    ModuleConfig.OverhealTarget = OverhealTarget.FirstTank;
                    SaveConfig(ModuleConfig);
                }
            }
        }
    }

    private static string ActionSearchInput = string.Empty;

    private static void ActiveHealActionsSelect()
    {
        ImGui.TextColored(YellowGreen, $"{GetLoc("HealerHelper-EasyHeal-ActiveHealAction")}");
        ImGui.Spacing();

        var actionList = ModuleConfig.TargetHealActions
                                     .ToDictionary(act => act.Key, act => LuminaGetter.GetRow<LuminaAction>(act.Key)!.Value);
        MultiSelectCombo(actionList,
                         ref ModuleConfig.ActiveHealActions,
                         ref ActionSearchInput,
                         [
                             new(GetLoc("Action"), ImGuiTableColumnFlags.WidthStretch, 20),
                             new(GetLoc("Job"), ImGuiTableColumnFlags.WidthStretch, 10)
                         ],
                         [
                             x => () =>
                             {
                                 if (!DService.Texture.TryGetFromGameIcon((uint)x.Icon, out var actionIcon)) return;
                                 using var id = ImRaii.PushId($"{x.RowId}");

                                 // icon - action name
                                 ImGui.TableSetColumnIndex(1);
                                 if (ImGuiOm.SelectableImageWithText(
                                                                     actionIcon.GetWrapOrEmpty().ImGuiHandle,
                                                                     new(ImGui.GetTextLineHeightWithSpacing()),
                                                                     x.Name.ExtractText(),
                                                                     ModuleConfig.ActiveHealActions.Contains(x.RowId),
                                                                     ImGuiSelectableFlags.DontClosePopups))
                                 {
                                     if (!ModuleConfig.ActiveHealActions.Remove(x.RowId))
                                         ModuleConfig.ActiveHealActions.Add(x.RowId);
                                 }

                                 // TODO: show action description
                                 // var desc = LuminaGetter.GetRow<ActionTransient>(x.RowId).Value.Description;

                                 // job
                                 ImGui.TableSetColumnIndex(2);
                                 ImGui.Text(x.ClassJobCategory.Value.Name.ExtractText());
                             }
                         ],
                         [x => x.Name.ExtractText() + x.ClassJobCategory.Value.Name.ExtractText()]
                        );
        ImGui.SameLine();
        ImGui.Dummy(new Vector2(5, 0));
        ImGui.SameLine();
        if (ImGui.Button($"{GetLoc("Reset")}##activehealactions"))
            ResetActiveHealActions();
    }

    private void EasyDispelUI()
    {
        ImGui.TextColored(LightSkyBlue, GetLoc("HealerHelper-EasyDispelTitle"));
        ImGuiOm.HelpMarker(GetLoc("HealerHelper-EasyRedirectDescription", LuminaWrapper.GetActionName(7568)));

        ImGui.Spacing();

        using (ImRaii.PushIndent())
        {
            if (ImGui.RadioButton($"{GetLoc("Disable")}##easydispel",
                                  ModuleConfig.EasyDispel == EasyDispelStatus.Disable))
            {
                ModuleConfig.EasyDispel = EasyDispelStatus.Disable;
                SaveConfig(ModuleConfig);
            }

            using (ImRaii.Group())
            {
                if (ImGui.RadioButton($"{GetLoc("Enable")} [{GetLoc("InOrder")}]##easydispel",
                                      ModuleConfig is { EasyDispel: EasyDispelStatus.Enable, DispelOrder: DispelOrderStatus.Order }))
                {
                    ModuleConfig.EasyDispel  = EasyDispelStatus.Enable;
                    ModuleConfig.DispelOrder = DispelOrderStatus.Order;
                    SaveConfig(ModuleConfig);
                }

                if (ImGui.RadioButton($"{GetLoc("Enable")} [{GetLoc("InReverseOrder")}]##easydispel",
                                      ModuleConfig is { EasyDispel: EasyDispelStatus.Enable, DispelOrder: DispelOrderStatus.Reverse }))
                {
                    ModuleConfig.EasyDispel  = EasyDispelStatus.Enable;
                    ModuleConfig.DispelOrder = DispelOrderStatus.Reverse;
                    SaveConfig(ModuleConfig);
                }
            }

            ImGuiOm.TooltipHover(GetLoc("HealerHelper-OrderHelp"), 20f * GlobalFontScale);
        }
    }

    private void EasyRaiseUI()
    {
        ImGui.TextColored(LightSkyBlue, GetLoc("HealerHelper-EasyRaiseTitle"));

        ImGui.Spacing();

        using (ImRaii.PushIndent())
        {
            if (ImGui.RadioButton($"{GetLoc("Disable")}##easyraise",
                                  ModuleConfig.EasyRaise == EasyRaiseStatus.Disable))
            {
                ModuleConfig.EasyRaise = EasyRaiseStatus.Disable;
                SaveConfig(ModuleConfig);
            }

            using (ImRaii.Group())
            {
                if (ImGui.RadioButton($"{GetLoc("Enable")} [{GetLoc("InOrder")}]##easyraise",
                                      ModuleConfig is { EasyRaise: EasyRaiseStatus.Enable, RaiseOrder: RaiseOrderStatus.Order }))
                {
                    ModuleConfig.EasyRaise  = EasyRaiseStatus.Enable;
                    ModuleConfig.RaiseOrder = RaiseOrderStatus.Order;
                    SaveConfig(ModuleConfig);
                }

                if (ImGui.RadioButton($"{GetLoc("Enable")} [{GetLoc("InReverseOrder")}]##easyraise",
                                      ModuleConfig is { EasyRaise: EasyRaiseStatus.Enable, RaiseOrder: RaiseOrderStatus.Reverse }))
                {
                    ModuleConfig.EasyRaise  = EasyRaiseStatus.Enable;
                    ModuleConfig.RaiseOrder = RaiseOrderStatus.Reverse;
                    SaveConfig(ModuleConfig);
                }
            }

            ImGuiOm.TooltipHover(GetLoc("HealerHelper-OrderHelp"), 20f * GlobalFontScale);
        }
    }

    #endregion

    #region RemoteCache

    // cache related client setting
    private const string SuCache = "https://dr-cache.sumemo.dev";

    private async Task FetchRemoteVersion()
    {
        // fetch remote data
        try
        {
            var json = await HttpClientHelper.Get().GetStringAsync($"{SuCache}/version");
            var resp = JsonConvert.DeserializeAnonymousType(json, new { version = "" });
            if (resp == null || string.IsNullOrWhiteSpace(resp.version))
                Error($"[HealerHelper] Deserialize Remote Version Failed: {json}");
            else
            {
                var remoteCacheVersion = resp.version;
                if (new Version(ModuleConfig.LocalCacheVersion) < new Version(remoteCacheVersion))
                {
                    // update config
                    ModuleConfig.LocalCacheVersion = resp.version;
                    SaveConfig(ModuleConfig);

                    // update out-date cache
                    await FetchDefaultCardOrder();
                    await FetchDefaultHealActions();
                    await FetchTerritoryMap();
                }
            }
        }
        catch (Exception ex)
        {
            Error($"[HealerHelper] Fetch Remote Version Failed: {ex}");
        }

        // build cache
        UpdateTerritoryMaps();
    }

    private async Task FetchDefaultCardOrder()
    {
        try
        {
            var json = await HttpClientHelper.Get().GetStringAsync($"{SuCache}/card-order?v={ModuleConfig.LocalCacheVersion}");
            var resp = JsonConvert.DeserializeObject<PlayCardOrder>(json);
            if (resp == null)
                Error($"[HealerHelper] Deserialize Default Play Card Order Failed: {json}");
            else
            {
                ModuleConfig.DefaultCardOrder = resp;
                // init custom if empty
                if (ModuleConfig.CustomCardOrder.Melee.Count is 0)
                    ResetCustomCardOrder();
                SaveConfig(ModuleConfig);
            }
        }
        catch (Exception ex)
        {
            Error($"[HealerHelper] Fetch Default Play Card Order Failed: {ex}");
        }
    }

    private async Task FetchDefaultHealActions()
    {
        try
        {
            var json = await HttpClientHelper.Get().GetStringAsync($"{SuCache}/heal-actions?v={ModuleConfig.LocalCacheVersion}");
            var resp = JsonConvert.DeserializeObject<Dictionary<string, List<HealAction>>>(json);
            if (resp == null)
                Error($"[HealerHelper] Deserialize Default Heal Actions Failed: {json}");
            else
            {
                ModuleConfig.TargetHealActions = resp.SelectMany(kv => kv.Value).ToDictionary(act => act.Id, act => act);

                // when active is empty, set with default on heal actions
                if (ModuleConfig.ActiveHealActions.Count is 0)
                    ResetActiveHealActions();

                SaveConfig(ModuleConfig);
            }
        }
        catch (Exception ex)
        {
            Error($"[HealerHelper] Fetch Default Heal Actions Failed: {ex}");
        }
    }

    private async Task FetchTerritoryMap()
    {
        try
        {
            var json = await HttpClientHelper.Get().GetStringAsync($"{SuCache}/territory?v={ModuleConfig.LocalCacheVersion}");
            var resp = JsonConvert.DeserializeObject<List<TerritoryMap>>(json);
            if (resp == null)
                Error($"[HealerHelper] Deserialize Territory Map Failed: {json}");
            else
            {
                ModuleConfig.TerritoryMaps = resp;
                SaveConfig(ModuleConfig);
            }
        }
        catch (Exception ex)
        {
            Error($"[HealerHelper] Fetch Territory Map Failed: {ex}");
        }
    }

    #endregion

    #region Hooks

    // hook before play card & target heal
    private void OnPreUseAction(
        ref bool  isPrevented, ref ActionType type,     ref uint actionID,
        ref ulong targetID,    ref Vector3    location, ref uint extraParam)
    {
        if (type != ActionType.Action || DService.ClientState.IsPvP || DService.PartyList.Length < 2) return;

        // precheck
        var localPlayer = DService.ClientState.LocalPlayer;
        var isHealer    = localPlayer.ClassJob.Value.Role is 4;

        // healer related
        if (isHealer)
        {
            var isAST = localPlayer.ClassJob.RowId is 33;

            // auto play card
            if (isAST && PlayCardActions.Contains(actionID) && ModuleConfig.AutoPlayCard != AutoPlayCardStatus.Disable)
                OnPrePlayCard(ref targetID, ref actionID);

            // easy heal
            if (ModuleConfig.EasyHeal == EasyHealStatus.Enable && ModuleConfig.ActiveHealActions.Contains(actionID))
                OnPreHeal(ref targetID, ref actionID, ref isPrevented);

            // easy dispel
            if (ModuleConfig.EasyDispel == EasyDispelStatus.Enable && actionID is 7568)
                OnPreDispel(ref targetID, ref actionID, ref isPrevented);
        }

        // can raise
        var canRaise = isHealer || localPlayer.ClassJob.RowId is 27 or 35;
        if (canRaise)
        {
            // easy raise
            if (ModuleConfig.EasyRaise == EasyRaiseStatus.Enable && RaiseActions.Contains(actionID))
                OnPreRaise(ref targetID, ref actionID, ref isPrevented);
        }
    }

    private static void OnZoneChanged(ushort zone)
        => MemberBestRecords.Clear();

    private static void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag is ConditionFlag.InCombat)
        {
            IsOpener    = true;
            NeedReorder = false;
        }
        else
        {
            IsOpener    = false;
            NeedReorder = true;
        }
    }

    private static void OnDutyRecommenced(object? sender, ushort e)
        => OrderCandidates();

    private static IPartyMember? FetchMember(uint id)
        => DService.PartyList.FirstOrDefault(m => m.ObjectId == id);

    private static unsafe uint? FetchMemberIndex(uint id)
        => (uint)AgentModule.Instance()->GetAgentHUD()->PartyMembers.ToArray()
                                                                    .Select((m, i) => (m, i))
                                                                    .FirstOrDefault(t => t.m.EntityId == id).i;

    private static bool NotifyErrorOnce = true;

    private static void OnPartyListPostDraw(AddonEvent type, AddonArgs args)
    {
        // party member changed?
        try
        {
            var inPvEParty = DService.PartyList.Length is not 0 && DService.ClientState.IsPvP is false;
            if (!inPvEParty)
                return;

            // need to update candidates?
            var ids = DService.PartyList.Select(m => m.ObjectId).ToHashSet();
            if (!ids.SetEquals(PartyMemberIdsCache) || NeedReorder)
            {
                // party member changed, update candidates
                OrderCandidates();
                PartyMemberIdsCache = ids;
                NeedReorder         = false;
            }
        }
        catch (Exception)
        {
            if (NotifyErrorOnce)
            {
                Chat(GetLoc("HealerHelper-Error"));
                NotifyErrorOnce = false;
            }
        }
    }

    #endregion

    #region AutoPlayCard

    private const           uint          UnspecificTargetId = 0xE000_0000;
    private static readonly HashSet<uint> PlayCardActions    = [37023, 37026];

    private static HashSet<uint> PartyMemberIdsCache = []; // check party member changed or not

    private static readonly List<(uint id, double priority)> MeleeCandidateOrder = [];
    private static readonly List<(uint id, double priority)> RangeCandidateOrder = [];

    private static bool IsOpener;
    private static bool NeedReorder;

    private static Dictionary<uint, uint> TerritoryMaps = new();

    private static void UpdateTerritoryMaps()
        => TerritoryMaps = ModuleConfig.TerritoryMaps.ToDictionary(x => x.Id, x => x.LogsZone);

    private static void ResetCustomCardOrder(string role = "All", string section = "All")
    {
        // melee opener
        if (role is "Melee" or "All")
        {
            if (section is "opener" or "All")
                ModuleConfig.CustomCardOrder.Melee["opener"] = ModuleConfig.DefaultCardOrder.Melee["opener"];
            if (section is "2m+" or "All")
                ModuleConfig.CustomCardOrder.Melee["2m+"] = ModuleConfig.DefaultCardOrder.Melee["2m+"];
        }

        // range opener
        if (role is "Range" or "All")
        {
            if (section is "opener" or "All")
                ModuleConfig.CustomCardOrder.Range["opener"] = ModuleConfig.DefaultCardOrder.Range["opener"];
            if (section is "2m+" or "All")
                ModuleConfig.CustomCardOrder.Range["2m+"] = ModuleConfig.DefaultCardOrder.Range["2m+"];
        }

        // reset order
        OrderCandidates();
    }

    private static void OrderCandidates()
    {
        // reset candidates before select new candidates
        MeleeCandidateOrder.Clear();
        RangeCandidateOrder.Clear();

        // find card candidates
        var partyList = DService.PartyList; // role [1 tank, 2 melee, 3 range, 4 healer]
        var isAST     = DService.ClientState.LocalPlayer.ClassJob.RowId is 33;
        if (partyList.Length is 0 || isAST is false || ModuleConfig.AutoPlayCard == AutoPlayCardStatus.Disable || DService.ClientState.IsPvP)
            return;

        // advance fallback when no valid zone id or invalid key
        if (!TerritoryMaps.ContainsKey(DService.ClientState.TerritoryType) && ModuleConfig is { AutoPlayCard: AutoPlayCardStatus.Advance, KeyValid: false })
        {
            if (FirstTimeFallback)
            {
                Chat(GetLoc("HealerHelper-AutoPlayCard-AdvanceFallback"));
                FirstTimeFallback = false;
            }

            ModuleConfig.AutoPlayCard = AutoPlayCardStatus.Default;
        }

        // is opener or 2m+?
        var sectionLabel = IsOpener ? "opener" : "2m+";

        // activate config (custom or default)
        var activateOrder = ModuleConfig.AutoPlayCard switch
        {
            AutoPlayCardStatus.Custom => ModuleConfig.CustomCardOrder,
            _                         => ModuleConfig.DefaultCardOrder
        };

        // set candidate priority based on predefined order
        var meleeOrder = activateOrder.Melee[sectionLabel];
        for (var idx = 0; idx < meleeOrder.Length; idx++)
        {
            var member = partyList.FirstOrDefault(m => m.ClassJob.Value.NameEnglish == meleeOrder[idx]);
            if (member is not null && MeleeCandidateOrder.All(m => m.id != member.ObjectId))
                MeleeCandidateOrder.Add((member.ObjectId, 2 - (idx * 0.1)));
        }

        var rangeOrder = activateOrder.Range[sectionLabel];
        for (var idx = 0; idx < rangeOrder.Length; idx++)
        {
            var member = partyList.FirstOrDefault(m => m.ClassJob.Value.NameEnglish == rangeOrder[idx]);
            if (member is not null && RangeCandidateOrder.All(m => m.id != member.ObjectId))
                RangeCandidateOrder.Add((member.ObjectId, 2 - (idx * 0.1)));
        }

        // adjust candidate priority based on FFLogs records (auto play card advance mode)
        if (ModuleConfig.AutoPlayCard == AutoPlayCardStatus.Advance)
        {
            foreach (var member in partyList)
            {
                var bestRecord = FetchBestLogsRecord(DService.ClientState.TerritoryType, member).GetAwaiter().GetResult();
                if (bestRecord is null) continue;

                // scale priority based on sigmoid percentile
                var scale = 1 / (1 + Math.Exp(-(bestRecord.Percentile - 50) / 8.33));

                // update priority
                if (member.ClassJob.Value.Role is 1 or 2)
                {
                    var idx = MeleeCandidateOrder.FindIndex(m => m.id == member.ObjectId);
                    if (idx != -1)
                    {
                        var priority = MeleeCandidateOrder[idx].priority * scale;
                        MeleeCandidateOrder[idx] = (member.ObjectId, priority);
                    }
                }
                else if (member.ClassJob.Value.Role is 3)
                {
                    var idx = RangeCandidateOrder.FindIndex(m => m.id == member.ObjectId);
                    if (idx != -1)
                    {
                        var priority = RangeCandidateOrder[idx].priority * scale;
                        RangeCandidateOrder[idx] = (member.ObjectId, priority);
                    }
                }
            }
        }

        // fallback: select the first dps in party list
        if (MeleeCandidateOrder.Count is 0)
        {
            var firstRange = partyList.FirstOrDefault(m => m.ClassJob.Value.Role is 1 or 3);
            if (firstRange is not null)
                MeleeCandidateOrder.Add((firstRange.ObjectId, -5));
        }

        if (RangeCandidateOrder.Count is 0)
        {
            var firstMelee = partyList.FirstOrDefault(m => m.ClassJob.Value.Role is 2);
            if (firstMelee is not null)
                RangeCandidateOrder.Add((firstMelee.ObjectId, -5));
        }

        // sort candidates by priority
        MeleeCandidateOrder.Sort((a, b) => b.priority.CompareTo(a.priority));
        RangeCandidateOrder.Sort((a, b) => b.priority.CompareTo(a.priority));
    }

    private static uint FetchCandidateId(string role)
    {
        var candidates = role switch
        {
            "Melee" => MeleeCandidateOrder,
            "Range" => RangeCandidateOrder,
            _       => throw new ArgumentOutOfRangeException(nameof(role))
        };

        var needResort = false;
        for (var i = 0; i < candidates.Count; i++)
        {
            var member    = candidates[i];
            var candidate = DService.PartyList.FirstOrDefault(m => m.ObjectId == member.id);
            if (candidate is null) continue;

            // skip dead member in this round (refresh on duty recommenced)
            if (candidate.CurrentHP <= 0)
            {
                switch (role)
                {
                    case "Melee":
                        MeleeCandidateOrder[i] = (candidate.ObjectId, -2);
                        break;
                    case "Range":
                        RangeCandidateOrder[i] = (candidate.ObjectId, -2);
                        break;
                }

                needResort = true;
                continue;
            }

            // skip member out of range for this action
            var maxDistance = ActionManager.GetActionRange(37023);
            var memberDead  = candidate.GameObject.IsDead || candidate.CurrentHP <= 0;
            if (memberDead || Vector3.Distance(candidate.Position, DService.ClientState.LocalPlayer.Position) > maxDistance)
                continue;

            return member.id;
        }

        if (needResort)
            candidates.Sort((a, b) => b.priority.CompareTo(a.priority));

        return DService.ClientState.LocalPlayer.EntityId;
    }

    private static void OnPrePlayCard(ref ulong targetID, ref uint actionID)
    {
        var partyMemberIds = DService.PartyList.Select(m => m.ObjectId).ToHashSet();
        if (!partyMemberIds.Contains((uint)targetID))
        {
            targetID = actionID switch
            {
                37023 => FetchCandidateId("Melee"),
                37026 => FetchCandidateId("Range")
            };

            var member = FetchMember((uint)targetID);
            if (member != null)
            {
                var name         = member.Name.ExtractText();
                var classJobIcon = member.ClassJob.ValueNullable.ToBitmapFontIcon();
                var classJobName = member.ClassJob.Value.Name.ExtractText();

                var locKey = actionID switch
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
        if (actionID is 37026 && IsOpener)
        {
            IsOpener    = false;
            NeedReorder = true;
        }
    }

    #endregion

    #region EasyHeal

    private static void ResetActiveHealActions()
        => ModuleConfig.ActiveHealActions = ModuleConfig.TargetHealActions.Where(act => act.Value.On).Select(act => act.Key).ToHashSet();

    private static uint TargetNeedHeal(uint actionID)
    {
        var partyList  = DService.PartyList;
        var lowRatio   = 2f;
        var needHealId = UnspecificTargetId;
        foreach (var member in partyList)
        {
            var maxDistance = ActionManager.GetActionRange(actionID);
            var memberDead  = member.GameObject.IsDead || member.CurrentHP <= 0;
            if (memberDead || Vector3.Distance(member.Position, DService.ClientState.LocalPlayer.Position) > maxDistance)
                continue;

            var ratio = member.CurrentHP / (float)member.MaxHP;
            if (ratio < lowRatio && ratio <= ModuleConfig.NeedHealThreshold)
            {
                lowRatio   = ratio;
                needHealId = member.ObjectId;
            }
        }

        return needHealId;
    }

    private static uint TargetNeedDispel(bool reverse = false)
    {
        var partyList    = DService.PartyList;
        var needDispelId = UnspecificTargetId;

        // first dispel local player
        var localPlayer       = DService.ClientState.LocalPlayer;
        var localPlayerStatus = localPlayer.StatusList;
        foreach (var status in localPlayerStatus)
            if (DispellableStatus.ContainsKey(status.StatusId))
                return localPlayer.EntityId;

        // dispel in order (or reverse order)
        var sortedPartyList = reverse
                                  ? partyList.OrderByDescending(member => FetchMemberIndex(member.ObjectId) ?? 0).ToList()
                                  : partyList.OrderBy(member => FetchMemberIndex(member.ObjectId)           ?? 0).ToList();
        foreach (var member in sortedPartyList)
        {
            var maxDistance = ActionManager.GetActionRange(7568);
            var memberDead  = member.GameObject.IsDead || member.CurrentHP <= 0;
            if (memberDead || Vector3.Distance(member.Position, DService.ClientState.LocalPlayer.Position) > maxDistance)
                continue;

            foreach (var status in member.Statuses)
                if (DispellableStatus.ContainsKey(status.StatusId))
                    return member.ObjectId;
        }

        return needDispelId;
    }

    private static uint TargetNeedRaise(uint actionID, bool reverse = false)
    {
        var partyList = DService.PartyList;

        // raise in order (or reverse order)
        var sortedPartyList = reverse
                                  ? partyList.OrderByDescending(member => FetchMemberIndex(member.ObjectId) ?? 0).ToList()
                                  : partyList.OrderBy(member => FetchMemberIndex(member.ObjectId)           ?? 0).ToList();
        foreach (var member in sortedPartyList)
        {
            var maxDistance = ActionManager.GetActionRange(actionID);
            var memberDead  = member.GameObject.IsDead || member.CurrentHP <= 0;
            if (memberDead && Vector3.Distance(member.Position, DService.ClientState.LocalPlayer.Position) <= maxDistance)
                return member.ObjectId;
        }

        return UnspecificTargetId;
    }

    private static void OnPreHeal(ref ulong targetID, ref uint actionID, ref bool isPrevented)
    {
        var currentTarget = DService.ObjectTable.SearchById(targetID);
        if (currentTarget is IBattleNpc || targetID == UnspecificTargetId)
        {
            // find target with the lowest HP ratio within range and satisfy threshold
            targetID = TargetNeedHeal(actionID);
            if (targetID == UnspecificTargetId)
            {
                switch (ModuleConfig.OverhealTarget)
                {
                    case OverhealTarget.Prevent:
                        isPrevented = true;
                        return;

                    case OverhealTarget.Local:
                        targetID = DService.ClientState.LocalPlayer.EntityId;
                        break;

                    case OverhealTarget.FirstTank:
                        var partyList       = DService.PartyList;
                        var sortedPartyList = partyList.OrderBy(member => FetchMemberIndex(member.ObjectId) ?? 0).ToList();
                        var firstTank       = sortedPartyList.FirstOrDefault(m => m.ClassJob.Value.Role == 1);
                        targetID = firstTank?.ObjectId ?? DService.ClientState.LocalPlayer.EntityId;
                        break;

                    default:
                        targetID = DService.ClientState.LocalPlayer.EntityId;
                        break;
                }
            }

            var member = FetchMember((uint)targetID);
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

    private static void OnPreDispel(ref ulong targetID, ref uint actionID, ref bool isPrevented)
    {
        var currentTarget = DService.ObjectTable.SearchById(targetID);
        if (currentTarget is IBattleNpc || targetID == UnspecificTargetId)
        {
            // find target with dispellable status within range
            targetID = TargetNeedDispel(ModuleConfig.DispelOrder is DispelOrderStatus.Reverse);
            if (targetID == UnspecificTargetId)
            {
                isPrevented = true;
                return;
            }

            // dispel target
            var member = FetchMember((uint)targetID);
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

    private static void OnPreRaise(ref ulong targetID, ref uint actionID, ref bool isPrevented)
    {
        var currentTarget = DService.ObjectTable.SearchById(targetID);
        if (currentTarget is IBattleNpc || targetID == UnspecificTargetId)
        {
            // find target with dead status within range
            targetID = TargetNeedRaise(actionID, ModuleConfig.RaiseOrder is RaiseOrderStatus.Reverse);
            if (targetID == UnspecificTargetId)
            {
                isPrevented = true;
                return;
            }

            // raise target
            var member = FetchMember((uint)targetID);
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

    #region FFLogs

    // FFLogs related (auto play card advance mode)
    private const           string                       FFLogsUri         = "https://www.fflogs.com/v1";
    private static readonly Dictionary<uint, LogsRecord> MemberBestRecords = new();

    // warning log
    private static bool FirstTimeFallback = true;

    private async Task CheckKeyStatus()
    {
        try
        {
            var uri      = $"{FFLogsUri}/classes?api_key={ModuleConfig.FFLogsAPIKey}";
            var response = await HttpClientHelper.Get().GetStringAsync(uri);
            ModuleConfig.KeyValid = !string.IsNullOrWhiteSpace(response);
            FirstTimeFallback     = true; // only notify once per exec time
            SaveConfig(ModuleConfig);
        }
        catch (Exception)
        {
            ModuleConfig.KeyValid = false;
            SaveConfig(ModuleConfig);
        }
    }

    private static string GetRegion()
    {
        return DService.ClientState.LocalPlayer.CurrentWorld.Value.DataCenter.Value.Region switch
        {
            1 => "JP",
            2 => "NA",
            3 => "EU",
            4 => "OC",
            5 => "CN",
            _ => string.Empty
        };
    }

    private static async Task<LogsRecord?> FetchBestLogsRecord(ushort zone, IPartyMember member)
    {
        // find in cache
        if (MemberBestRecords.TryGetValue(member.ObjectId, out var bestRecord))
            return bestRecord;

        // get character info
        var charaName  = member.Name;
        var serverSlug = member.World.Value.Name.ExtractText();
        var region     = GetRegion();
        var job        = member.ClassJob.Value.NameEnglish.ExtractText();

        // fetch record
        try
        {
            var uri   = $"{FFLogsUri}/parses/character/{charaName}/{serverSlug}/{region}";
            var query = HttpUtility.ParseQueryString(string.Empty);
            query["api_key"]   = ModuleConfig.FFLogsAPIKey;
            query["metric"]    = "ndps";
            query["encounter"] = TerritoryMaps[zone].ToString();

            // contains all ultimates and current savage in current patch
            var response = await HttpClientHelper.Get().GetStringAsync($"{uri}?{query}");
            var records  = JsonConvert.DeserializeObject<LogsRecord[]>(response);
            if (records == null || records.Length == 0) return null;

            // find best record
            bestRecord = records.Where(r => r.JobName == job)
                                .OrderByDescending(r => r.Difficulty)
                                .ThenByDescending(r => r.DPS)
                                .FirstOrDefault();
            MemberBestRecords[member.ObjectId] = bestRecord;
            return bestRecord;
        }
        catch (Exception)
        {
            return null;
        }
    }

    #endregion


    #region Config

    private class Config : ModuleConfiguration
    {
        // auto play card
        public          AutoPlayCardStatus AutoPlayCard     = AutoPlayCardStatus.Default;
        public          PlayCardOrder      DefaultCardOrder = new();
        public readonly PlayCardOrder      CustomCardOrder  = new();

        // FFLogs API Key v1 for fetching records (auto play card advance mode)
        public string             FFLogsAPIKey = string.Empty;
        public bool               KeyValid;
        public List<TerritoryMap> TerritoryMaps = new();

        // easy heal
        public EasyHealStatus               EasyHeal          = EasyHealStatus.Enable;
        public float                        NeedHealThreshold = 0.92f;
        public OverhealTarget               OverhealTarget    = OverhealTarget.Local;
        public Dictionary<uint, HealAction> TargetHealActions = [];
        public HashSet<uint>                ActiveHealActions = [];

        // easy dispel
        public EasyDispelStatus  EasyDispel  = EasyDispelStatus.Enable;
        public DispelOrderStatus DispelOrder = DispelOrderStatus.Order;

        // easy raise
        public EasyRaiseStatus  EasyRaise  = EasyRaiseStatus.Enable;
        public RaiseOrderStatus RaiseOrder = RaiseOrderStatus.Order;

        // notification
        public bool SendChat;
        public bool SendNotification = true;

        // remote cache version
        public string LocalCacheVersion = "0.0.0";
    }

    #endregion

    #region AutoPlayCardStructs

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

    private class LogsRecord
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

    private enum AutoPlayCardStatus
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

    #region EasyHealStructs

    // list of target healing actions
    // load from su-cache:heal-actions
    public class HealAction
    {
        [JsonProperty("id")]
        public uint Id { get; private set; }

        [JsonProperty("name")]
        public string Name { get; private set; }

        [JsonProperty("on")]
        public bool On { get; private set; }
    }

    // list of dispellable status
    public static Dictionary<uint, string> DispellableStatus = new();

    // enums
    private enum EasyHealStatus
    {
        Disable, // disable easy heal
        Enable   // select target with the lowest HP ratio within range when no target selected
    }

    private enum OverhealTarget
    {
        Local,     // local player
        FirstTank, // first tank in party list
        Prevent    // prevent overheal
    }

    private enum EasyDispelStatus
    {
        Disable, // disable easy dispel
        Enable   // select target with dispellable status within range when no target selected
    }

    private enum DispelOrderStatus
    {
        Order,  // local -> party list (0 -> 7)
        Reverse // local -> party list (7 -> 0)
    }

    private readonly HashSet<uint> RaiseActions = [125, 173, 3603, 24287, 7670, 7523];

    private enum EasyRaiseStatus
    {
        Disable, // disable easy raise
        Enable   // select target dead within range when no target selected
    }

    private enum RaiseOrderStatus
    {
        Order,  // local -> party list (0 -> 7)
        Reverse // local -> party list (7 -> 0)
    }

    #endregion
}
