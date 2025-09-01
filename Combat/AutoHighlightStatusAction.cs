using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using DailyRoutines.Widgets;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using LuminaAction = Lumina.Excel.Sheets.Action;
using Status = Lumina.Excel.Sheets.Status;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoHighlightStatusAction : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoHighlightStatusActionTitle"),
        Description = GetLoc("AutoHighlightStatusActionDescription"),
        Category    = ModuleCategories.Combat,
        Author      = ["HaKu"]
    };

    private static readonly CompSig IsActionHighlightedSig = new("E8 ?? ?? ?? ?? 88 47 41 80 BB C9 00 00 00 01");
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool IsActionHighlightedDelegate(ActionManager* actionManager, ActionType actionType, uint actionId);
    private static Hook<IsActionHighlightedDelegate>? IsActionHighlightedHook;

    private static Config ModuleConfig = null!;

    private static StatusSelectCombo? StatusCombo;
    private static ActionSelectCombo? ActionCombo;
    
    public static float Countdown                = 4.0f;
    public static bool  KeepHighlightAfterExpire = true;

    public static readonly  HashSet<uint>            ActionsToHighlight = [];
    private static          uint                     LastActionID;
    private static readonly Dictionary<ushort, uint> LastStatusTarget = [];
    
    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        if (ModuleConfig.MonitoredStatus.Count == 0)
        {
            ModuleConfig.MonitoredStatus = statusConfigs.ToDictionary(x => x.Key, x => x.Value);
            SaveConfig(ModuleConfig);
        }
        
        StatusCombo ??= new("StatusCombo", PresetSheet.Statuses.Values);
        ActionCombo ??= new("ActionCombo", PresetSheet.PlayerActions.Values);

        IsActionHighlightedHook = IsActionHighlightedSig.GetHook<IsActionHighlightedDelegate>(IsActionHighlightedDetour);
        IsActionHighlightedHook.Enable();

        UseActionManager.RegPreUseActionLocation(OnPreUseActionLocation);
        FrameworkManager.Register(OnUpdate, throttleMS: 500);
    }

    protected override void Uninit()
    {
        UseActionManager.UnregPreUseActionLocation(OnPreUseActionLocation);
        FrameworkManager.Unregister(OnUpdate);
    }

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(100f * GlobalFontScale);
        if (ImGui.SliderFloat($"{GetLoc("AutoHighlightStatusAction-Countdown")}##ReminderThreshold", ref Countdown, 2.0f, 10.0f, "%.1f"))
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
            if (StatusCombo.SelectedStatusID != 0 && ActionCombo.SelectedActionIDs.Count > 0)
            {
                ModuleConfig.MonitoredStatus[StatusCombo.SelectedStatusID] = new StatusConfig
                {
                    BindActions = ActionCombo.SelectedActionIDs.ToList(),
                    Countdown   = Countdown,
                    KeepHighlight       = KeepHighlightAfterExpire,
                };
                ModuleConfig.Save(this);
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
                break;
            }

            ImGui.TableNextColumn();
            if (!LuminaGetter.TryGetRow<Status>(status, out var statusRow) ||
                !DService.Texture.TryGetFromGameIcon(new GameIconLookup(statusRow.Icon), out var texture))
                continue;

            ImGui.SameLine();
            ImGuiOm.TextImage(statusRow.Name.ExtractText(), texture.GetWrapOrEmpty().ImGuiHandle, new Vector2(ImGui.GetTextLineHeight()));
            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsItemClicked())
                StatusCombo.SelectedStatusID = status;

            ImGui.TableNextColumn();
            using (ImRaii.Group())
            {
                foreach (var action in statusConfig.BindActions)
                {
                    if (!LuminaGetter.TryGetRow<LuminaAction>(action, out var actionRow) ||
                        !DService.Texture.TryGetFromGameIcon(new GameIconLookup(actionRow.Icon), out var actionTexture))
                        continue;

                    ImGuiOm.TextImage(actionRow.Name.ExtractText(), actionTexture.GetWrapOrEmpty().ImGuiHandle, new Vector2(ImGui.GetTextLineHeight()));
                    ImGui.SameLine();
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsItemClicked())
                ActionCombo.SelectedActionIDs = statusConfig.BindActions.ToHashSet();

            ImGui.TableNextColumn();
            ImGui.Text($"{statusConfig.Countdown:0.0}");
            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsItemClicked())
                Countdown = statusConfig.Countdown;

            ImGui.TableNextColumn();
            ImGui.Text(statusConfig.KeepHighlight ? GetLoc("Yes") : GetLoc("No"));
            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsItemClicked())
                KeepHighlightAfterExpire = statusConfig.KeepHighlight;
        }
    }

    private static void OnUpdate(IFramework _)
    {
        if (GameState.IsInPVPArea || !DService.Condition[ConditionFlag.InCombat] || Control.GetLocalPlayer() == null)
        {
            // clear record when leaving combat
            ActionsToHighlight.Clear();
            LastStatusTarget.Clear();
            return;
        }

        var actionToHighlight = new Dictionary<uint, (float, float)>();

        // status on local player
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null)
            return;

        foreach (var status in localPlayer->StatusManager.Status)
        {
            if (status.SourceObject != localPlayer->GetGameObjectId())
                continue;
            if (!ModuleConfig.MonitoredStatus.TryGetValue(status.StatusId, out var statusConfig))
                continue;

            foreach (var action in statusConfig.BindActions)
                actionToHighlight[action] = (status.RemainingTime, statusConfig.Countdown);
            LastStatusTarget[status.StatusId] = localPlayer->EntityId;
        }

        // status on current target
        var currentTarget = DService.Targets.Target;
        if (currentTarget is IBattleNpc { IsDead: false } battleNpc)
        {
            foreach (var status in battleNpc.ToBCStruct()->StatusManager.Status)
            {
                if (status.SourceObject != localPlayer->GetGameObjectId())
                    continue;
                if (!ModuleConfig.MonitoredStatus.TryGetValue(status.StatusId, out var statusConfig))
                    continue;

                foreach (var action in statusConfig.BindActions)
                    actionToHighlight[action] = (status.RemainingTime, statusConfig.Countdown);
                LastStatusTarget[status.StatusId] = battleNpc.EntityId;
            }
        }

        // status in cache
        foreach (var status in LastStatusTarget)
        {
            // check entity is still valid
            var lastTarget = CharacterManager.Instance()->LookupBattleCharaByEntityId(status.Value);
            if (lastTarget != null && !lastTarget->IsDead())
            {
                // check if status is still active
                var isActive = false;
                foreach (var validStatus in lastTarget->StatusManager.Status)
                {
                    if (validStatus.StatusId == status.Key)
                    {
                        isActive = true;
                        break;
                    }
                }

                // status active -> continue
                if (isActive)
                    continue;

                // manually add action to highlight
                // when status is no longer active and renew is enabled
                if (!ModuleConfig.MonitoredStatus.TryGetValue(status.Key, out var statusConfig) || !statusConfig.KeepHighlight)
                    continue;

                foreach (var action in statusConfig.BindActions)
                    actionToHighlight[action] = (0f, statusConfig.Countdown);
            }
        }

        // highlight actions
        var manager = ActionManager.Instance();
        ActionsToHighlight.Clear();
        foreach (var (actionId, time) in actionToHighlight)
        {
            var actionChain = FetchComboChain(actionId);

            var cutoff        = time.Item2 * actionChain.Length;
            var notInChain    = actionChain.All(id => !manager->IsActionHighlighted(ActionType.Action, id));
            var notLastAction = actionChain[..^1].All(id => id != LastActionID);

            if (time.Item1 <= cutoff && notInChain && notLastAction)
                ActionsToHighlight.Add(actionChain[0]);
        }
    }

    private static void OnPreUseActionLocation(
        ref bool       isPrevented,
        ref ActionType type,
        ref uint       actionID,
        ref ulong      targetID,
        ref Vector3    location,
        ref uint       extraParam)
    {
        ActionsToHighlight.Remove(actionID);
        LastActionID = actionID;
    }

    private static bool IsActionHighlightedDetour(ActionManager* actionManager, ActionType actionType, uint actionID) => 
        ActionsToHighlight.Contains(actionID) || IsActionHighlightedHook.Original(actionManager, actionType, actionID);

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

    private class Config : ModuleConfiguration
    {
        public Dictionary<uint, StatusConfig> MonitoredStatus = [];
    }

    // default status to monitor
    private static readonly Dictionary<uint, StatusConfig> statusConfigs = new()
    {
        // AST
        [838]  = new StatusConfig { BindActions = [3599], Countdown  = 4.0f, KeepHighlight  = true },
        [843]  = new StatusConfig { BindActions = [3608], Countdown  = 4.0f, KeepHighlight  = true },
        [1881] = new StatusConfig { BindActions = [16554], Countdown = 4.0f, KeepHighlight  = true },
        [1248] = new StatusConfig { BindActions = [8324], Countdown  = 10.0f, KeepHighlight = false },
        // WHM
        [143]  = new StatusConfig { BindActions = [121], Countdown   = 4.0f, KeepHighlight = true },
        [144]  = new StatusConfig { BindActions = [132], Countdown   = 4.0f, KeepHighlight = true },
        [1871] = new StatusConfig { BindActions = [16532], Countdown = 4.0f, KeepHighlight = true },
        // SGE
        [2614] = new StatusConfig { BindActions = [24290], Countdown = 6.0f, KeepHighlight = true },
        [2615] = new StatusConfig { BindActions = [24290], Countdown = 6.0f, KeepHighlight = true },
        [2616] = new StatusConfig { BindActions = [24290], Countdown = 6.0f, KeepHighlight = true },
        // SCH
        [179]  = new StatusConfig { BindActions = [17864], Countdown = 4.0f, KeepHighlight = true },
        [189]  = new StatusConfig { BindActions = [17865], Countdown = 4.0f, KeepHighlight = true },
        [1895] = new StatusConfig { BindActions = [16540], Countdown = 4.0f, KeepHighlight = true },
        // BARD
        [124]  = new StatusConfig { BindActions = [100], Countdown        = 4.0f, KeepHighlight = true },
        [1200] = new StatusConfig { BindActions = [7406, 3560], Countdown = 4.0f, KeepHighlight = true },
        [129]  = new StatusConfig { BindActions = [113], Countdown        = 4.0f, KeepHighlight = true },
        [1201] = new StatusConfig { BindActions = [7407, 3560], Countdown = 4.0f, KeepHighlight = true },
        // SAM
        [1299] = new StatusConfig { BindActions = [7485], Countdown  = 4.0f, KeepHighlight = true },
        [2719] = new StatusConfig { BindActions = [25772], Countdown = 4.0f, KeepHighlight = true },
        // WAR
        [2677] = new StatusConfig { BindActions = [45], Countdown = 4.0f, KeepHighlight = true },
    };

    private class StatusConfig
    {
        public List<uint> BindActions { get; init; } = [];
        public float      Countdown   { get; init; } = 4.0f;
        public bool       KeepHighlight       { get; init; } = true;
    }
}
