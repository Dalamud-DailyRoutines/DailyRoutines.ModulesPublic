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

    private static readonly CompSig isActionHighlightedSig = new("E8 ?? ?? ?? ?? 88 47 41 80 BB C9 00 00 00 01");

    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool IsActionHighlightedDelegate(ActionManager* actionManager, ActionType actionType, uint actionId);

    private static Hook<IsActionHighlightedDelegate>? isActionHighlightedHook;

    private static Config moduleConfig = null!;

    private static StatusSelectCombo? statusCombo;
    private static ActionSelectCombo? actionCombo;

    public static readonly  HashSet<uint>            ActionsToHighlight = [];
    private static          uint                     lastActionId;
    private static readonly Dictionary<ushort, uint> lastStatusTarget = new();

    protected override void Init()
    {
        moduleConfig = LoadConfig<Config>() ??
                       new()
                       {
                           StatusToMonitor = statusToAction.ToDictionary(x => x.Key, x => x.Value)
                       };

        statusCombo ??= new("StatusCombo", PresetSheet.Statuses.Values);
        actionCombo ??= new("ActionCombo", PresetSheet.PlayerActions.Values);

        isActionHighlightedHook = isActionHighlightedSig.GetHook<IsActionHighlightedDelegate>(IsActionHighlightedDetour);
        isActionHighlightedHook.Enable();

        UseActionManager.RegPreUseActionLocation(OnPreUseActionLocation);
        FrameworkManager.Register(OnUpdate, throttleMS: 200);
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
        if (ImGui.SliderFloat($"{GetLoc("Threshold")} (s)##ReminderThreshold", ref moduleConfig.Threshold, 2.0f, 10.0f, "%.1f"))
            SaveConfig(moduleConfig);
        ImGuiOm.HelpMarker(GetLoc("AutoHighlightStatusAction-ThresholdHelp"));
        ImGui.NewLine();

        ImGui.SetNextItemWidth(300f * GlobalFontScale);
        using (ImRaii.PushId("Status"))
            statusCombo.DrawRadio();
        ImGui.TextDisabled("↓");
        ImGui.SetNextItemWidth(300f * GlobalFontScale);
        using (ImRaii.PushId("Action"))
            actionCombo.DrawCheckbox();

        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, GetLoc("Add")))
        {
            if (statusCombo.SelectedStatusID != 0 && actionCombo.SelectedActionIDs.Count > 0)
            {
                moduleConfig.StatusToMonitor[statusCombo.SelectedStatusID] = actionCombo.SelectedActionIDs.ToList();
                moduleConfig.Save(this);
            }
        }

        ImGui.Spacing();

        foreach (var (status, actions) in moduleConfig.StatusToMonitor)
        {
            using var id = ImRaii.PushId($"{status}");

            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.TrashAlt, GetLoc("Delete")))
            {
                moduleConfig.StatusToMonitor.Remove(status);
                moduleConfig.Save(this);
            }

            if (!LuminaGetter.TryGetRow<Status>(status, out var statusRow) ||
                !DService.Texture.TryGetFromGameIcon(new(statusRow.Icon), out var texture))
                continue;

            ImGui.SameLine();
            ImGuiOm.TextImage(statusRow.Name.ExtractText(), texture.GetWrapOrEmpty().ImGuiHandle, new(ImGui.GetTextLineHeight()));
            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsItemClicked())
                statusCombo.SelectedStatusID = status;

            ImGui.SameLine();
            ImGui.TextDisabled("→");

            ImGui.SameLine();
            using (ImRaii.Group())
            {
                foreach (var action in actions)
                {
                    if (!LuminaGetter.TryGetRow<LuminaAction>(action, out var actionRow) ||
                        !DService.Texture.TryGetFromGameIcon(new(actionRow.Icon), out var actionTexture))
                        continue;

                    ImGuiOm.TextImage(actionRow.Name.ExtractText(), actionTexture.GetWrapOrEmpty().ImGuiHandle, new(ImGui.GetTextLineHeight()));
                    ImGui.SameLine();
                }
            }

            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsItemClicked())
                actionCombo.SelectedActionIDs = actions.ToHashSet();
        }
    }

    private static void OnUpdate(IFramework _)
    {
        if (GameState.IsInPVPArea || !DService.Condition[ConditionFlag.InCombat] || Control.GetLocalPlayer() == null)
        {
            // clear record when leaving combat
            ActionsToHighlight.Clear();
            lastStatusTarget.Clear();
            return;
        }

        var actionToHighlight = new Dictionary<uint, float>();

        // status on local player
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null)
            return;

        foreach (var status in localPlayer->StatusManager.Status)
        {
            if (!moduleConfig.StatusToMonitor.TryGetValue(status.StatusId, out var actions))
                continue;

            foreach (var action in actions)
                actionToHighlight[action] = status.RemainingTime;
            lastStatusTarget[status.StatusId] = localPlayer->EntityId;
        }

        // status on current target
        var currentTarget = DService.Targets.Target;
        if (currentTarget is IBattleNpc { IsDead: false } battleNpc)
        {
            foreach (var status in battleNpc.ToBCStruct()->StatusManager.Status)
            {
                if (!moduleConfig.StatusToMonitor.TryGetValue(status.StatusId, out var actions))
                    continue;

                foreach (var action in actions)
                    actionToHighlight[action] = status.RemainingTime;
                lastStatusTarget[status.StatusId] = battleNpc.EntityId;
            }
        }

        // status in cache
        foreach (var status in lastStatusTarget)
        {
            // check entity is still valid
            var lastTarget = CharacterManager.Instance()->LookupBattleCharaByEntityId(status.Value);
            if (lastTarget != null && lastTarget->IsDead() == false)
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
                if (!moduleConfig.StatusToMonitor.TryGetValue(status.Key, out var actions))
                    continue;

                foreach (var action in actions)
                    actionToHighlight[action] = 0f;
            }
        }

        // highlight actions
        var manager = ActionManager.Instance();
        ActionsToHighlight.Clear();
        foreach (var (actionId, time) in actionToHighlight)
        {
            var actionChain = FetchComboChain(actionId);

            var cutoff        = moduleConfig.Threshold * actionChain.Length;
            var notInChain    = actionChain.All(id => !manager->IsActionHighlighted(ActionType.Action, id));
            var notLastAction = actionChain[..^1].All(id => id != lastActionId);

            if (time <= cutoff && notInChain && notLastAction)
                ActionsToHighlight.Add(actionChain[0]);
        }
    }

    private static void OnPreUseActionLocation(
        ref bool isPrevented, ref ActionType type, ref uint actionId, ref ulong targetId, ref Vector3 location, ref uint extraParam)
    {
        ActionsToHighlight.Remove(actionId);
        lastActionId = actionId;
    }

    private static bool IsActionHighlightedDetour(ActionManager* actionManager, ActionType actionType, uint actionId)
        => ActionsToHighlight.Contains(actionId) || isActionHighlightedHook!.Original(actionManager, actionType, actionId);

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

        public Dictionary<uint, List<uint>> StatusToMonitor = [];
    }

    // Status - Actions
    private static readonly Dictionary<uint, List<uint>> statusToAction = new()
    {
        [1881] = [16554],
        [1871] = [16532],
        [2616] = [24314],
        [3897] = [37032],
        [1895] = [16540],
        [1200] = [7406, 3560],
        [1201] = [7407, 3560],
        [1299] = [7485],
        [2719] = [25772],
        [2677] = [45]
    };
}
