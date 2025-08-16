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

    public static readonly  HashSet<uint>            ActionsToHighlight = [];
    private static          uint                     LastActionID;
    private static readonly Dictionary<ushort, uint> LastStatusTarget = [];

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ??
                       new()
                       {
                           StatusToMonitor = statusConfigs.ToDictionary(x => x.Key, x => x.Value)
                       };

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

        base.Uninit();
    }

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(100f * GlobalFontScale);
        if (ImGui.SliderFloat($"{GetLoc("AutoHighlightStatusAction-Countdown")} (s)##ReminderThreshold", ref ModuleConfig.Threshold, 2.0f, 10.0f, "%.1f"))
            SaveConfig(ModuleConfig);
        ImGui.SameLine();
        ImGuiOm.HelpMarker(GetLoc("AutoHighlightStatusAction-ThresholdHelp"));

        ImGui.SameLine();
        ScaledDummy(5, 0);
        ImGui.SameLine();

        if (ImGui.Checkbox($"{GetLoc("AutoHighlightStatusAction-Renew")}##Renew", ref ModuleConfig.Renew))
            SaveConfig(ModuleConfig);

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
                ModuleConfig.StatusToMonitor[StatusCombo.SelectedStatusID] = new StatusConfig
                {
                    BindActions = ActionCombo.SelectedActionIDs.ToList(),
                    Countdown   = ModuleConfig.Threshold,
                    Renew       = ModuleConfig.Renew,
                };
                ModuleConfig.Save(this);
            }
        }

        ImGui.NewLine();

        using var table = ImRaii.Table("PlayersInList", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY, new Vector2(0, 180f * GlobalFontScale));
        if (!table)
            return;

        var availableWidth = ImGui.GetContentRegionAvail().X;
        ImGui.TableSetupColumn("##Delete", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn(GetLoc("Status"), ImGuiTableColumnFlags.WidthFixed, availableWidth * 0.15f);
        ImGui.TableSetupColumn(GetLoc("Action"), ImGuiTableColumnFlags.WidthFixed, availableWidth * 0.4f);
        ImGui.TableSetupColumn(GetLoc("AutoHighlightStatusAction-Countdown"), ImGuiTableColumnFlags.WidthFixed, availableWidth * 0.2f);
        ImGui.TableSetupColumn(GetLoc("AutoHighlightStatusAction-Renew"), ImGuiTableColumnFlags.WidthFixed, availableWidth * 0.2f);

        ImGui.TableHeadersRow();

        foreach (var (status, statusConfig) in ModuleConfig.StatusToMonitor)
        {
            using var id = ImRaii.PushId($"{status}");

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.TrashAlt, GetLoc("Delete")))
            {
                ModuleConfig.StatusToMonitor.Remove(status);
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

            ImGui.TableNextColumn();
            ImGui.TextDisabled($"{statusConfig.Countdown:0.0}s");

            ImGui.TableNextColumn();
            ImGui.TextDisabled(statusConfig.Renew ? GetLoc("Yes") : GetLoc("No"));

            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsItemClicked())
                ActionCombo.SelectedActionIDs = statusConfig.BindActions.ToHashSet();
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
            if (!ModuleConfig.StatusToMonitor.TryGetValue(status.StatusId, out var statusConfig))
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
                if (!ModuleConfig.StatusToMonitor.TryGetValue(status.StatusId, out var statusConfig))
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
                if (!ModuleConfig.StatusToMonitor.TryGetValue(status.Key, out var statusConfig) || !statusConfig.Renew)
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
        ref bool isPrevented, ref ActionType type, ref uint actionId, ref ulong targetId, ref Vector3 location, ref uint extraParam)
    {
        ActionsToHighlight.Remove(actionId);
        LastActionID = actionId;
    }

    private static bool IsActionHighlightedDetour(ActionManager* actionManager, ActionType actionType, uint actionId)
        => ActionsToHighlight.Contains(actionId) || IsActionHighlightedHook!.Original(actionManager, actionType, actionId);

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
        public float Threshold = 4.0f;
        public bool  Renew     = true;

        public Dictionary<uint, StatusConfig> StatusToMonitor = [];
    }

    // default status to monitor
    private static readonly Dictionary<uint, StatusConfig> statusConfigs = new()
    {
        // AST
        [838]  = new StatusConfig { BindActions = [3599], Countdown  = 4.0f, Renew  = true },
        [843]  = new StatusConfig { BindActions = [3608], Countdown  = 4.0f, Renew  = true },
        [1881] = new StatusConfig { BindActions = [16554], Countdown = 4.0f, Renew  = true },
        [1248] = new StatusConfig { BindActions = [8324], Countdown  = 10.0f, Renew = false },
        // WHM
        [143]  = new StatusConfig { BindActions = [121], Countdown   = 4.0f, Renew = true },
        [144]  = new StatusConfig { BindActions = [132], Countdown   = 4.0f, Renew = true },
        [1871] = new StatusConfig { BindActions = [16532], Countdown = 4.0f, Renew = true },
        // SGE
        [2614] = new StatusConfig { BindActions = [24290], Countdown = 6.0f, Renew = true },
        [2615] = new StatusConfig { BindActions = [24290], Countdown = 6.0f, Renew = true },
        [2616] = new StatusConfig { BindActions = [24290], Countdown = 6.0f, Renew = true },
        // SCH
        [179]  = new StatusConfig { BindActions = [17864], Countdown = 4.0f, Renew = true },
        [189]  = new StatusConfig { BindActions = [17865], Countdown = 4.0f, Renew = true },
        [1895] = new StatusConfig { BindActions = [16540], Countdown = 4.0f, Renew = true },
        // BARD
        [124]  = new StatusConfig { BindActions = [100], Countdown        = 4.0f, Renew = true },
        [1200] = new StatusConfig { BindActions = [7406, 3560], Countdown = 4.0f, Renew = true },
        [129]  = new StatusConfig { BindActions = [113], Countdown        = 4.0f, Renew = true },
        [1201] = new StatusConfig { BindActions = [7407, 3560], Countdown = 4.0f, Renew = true },
        // SAM
        [1299] = new StatusConfig { BindActions = [7485], Countdown  = 4.0f, Renew = true },
        [2719] = new StatusConfig { BindActions = [25772], Countdown = 4.0f, Renew = true },
        // WAR
        [2677] = new StatusConfig { BindActions = [45], Countdown = 4.0f, Renew = true },
    };

    private class StatusConfig
    {
        public List<uint> BindActions { get; init; } = [];
        public float      Countdown   { get; init; } = 4.0f;
        public bool       Renew       { get; init; } = true;
    }
}
