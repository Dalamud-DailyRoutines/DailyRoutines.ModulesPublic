using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using DailyRoutines.Widgets;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Interface;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
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
    private delegate bool IsActionHighlightedDelegate(ActionManager* actionManager, ActionType actionType, uint actionID);

    private static Hook<IsActionHighlightedDelegate>? IsActionHighlightedHook;

    private static Config ModuleConfig = null!;

    private static StatusSelectCombo? StatusCombo;
    private static ActionSelectCombo? ActionCombo;

    private static readonly HashSet<uint> ActionsToHighlight = [];
    private static          uint          LastActionID;

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ??
                       new()
                       {
                           StatusToMonitor = StatusToAction.ToDictionary(x => x.Key, x => x.Value)
                       };

        StatusCombo ??= new("StatusCombo", PresetSheet.Statuses.Values);
        ActionCombo ??= new("ActionCombo", PresetSheet.PlayerActions.Values);

        IsActionHighlightedHook = IsActionHighlightedSig.GetHook<IsActionHighlightedDelegate>(IsActionHighlightedDetour);
        IsActionHighlightedHook.Enable();

        UseActionManager.RegPreUseActionLocation(OnPreUseActionLocation);
        FrameworkManager.Register(OnUpdate, throttleMS: 200);
    }

    public override void Uninit()
    {
        UseActionManager.UnregPreUseActionLocation(OnPreUseActionLocation);
        FrameworkManager.Unregister(OnUpdate);

        base.Uninit();
    }

    public override void ConfigUI()
    {
        ImGui.SetNextItemWidth(100f * GlobalFontScale);
        if (ImGui.SliderFloat($"{GetLoc("Threshold")} (s)##ReminderThreshold", ref ModuleConfig.Threshold, 2.0f, 10.0f, "%.1f"))
            SaveConfig(ModuleConfig);
        ImGuiOm.HelpMarker(GetLoc("AutoHighlightStatusAction-ThresholdHelp"));
        ImGui.NewLine();

        ImGui.SetNextItemWidth(300f * GlobalFontScale);
        using (ImRaii.PushId("Status"))
            StatusCombo.DrawRadio();
        ImGui.TextDisabled("↓");
        ImGui.SetNextItemWidth(300f * GlobalFontScale);
        using (ImRaii.PushId("Action"))
            ActionCombo.DrawCheckbox();

        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, GetLoc("Add")))
        {
            if (StatusCombo.SelectedStatusID != 0 && ActionCombo.SelectedActionIDs.Count > 0)
            {
                ModuleConfig.StatusToMonitor[StatusCombo.SelectedStatusID] = ActionCombo.SelectedActionIDs.ToList();
                ModuleConfig.Save(this);
            }
        }

        ImGui.Spacing();

        foreach (var (status, actions) in ModuleConfig.StatusToMonitor)
        {
            using var id = ImRaii.PushId($"{status}");

            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.TrashAlt, GetLoc("Delete")))
            {
                ModuleConfig.StatusToMonitor.Remove(status);
                ModuleConfig.Save(this);
            }

            if (!LuminaGetter.TryGetRow<Status>(status, out var statusRow) ||
                !DService.Texture.TryGetFromGameIcon(new(statusRow.Icon), out var texture))
                continue;

            ImGui.SameLine();
            ImGuiOm.TextImage(statusRow.Name.ExtractText(), texture.GetWrapOrEmpty().ImGuiHandle, new(ImGui.GetTextLineHeight()));
            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsItemClicked())
                StatusCombo.SelectedStatusID = status;

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
                ActionCombo.SelectedActionIDs = actions.ToHashSet();
        }
    }

    private static void OnUpdate(IFramework _)
    {
        if (GameState.IsInPVPArea || !DService.Condition[ConditionFlag.InCombat] || Control.GetLocalPlayer() == null)
            return;

        var actionToHighlight = new Dictionary<uint, float>();

        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null)
            return;

        foreach (var status in localPlayer->StatusManager.Status)
        {
            if (!ModuleConfig.StatusToMonitor.TryGetValue(status.StatusId, out var actions))
                continue;
            actionToHighlight.AddRange(actions.ToDictionary(x => x, _ => status.RemainingTime));
        }

        var currentTarget = DService.Targets.Target;
        if (currentTarget is IBattleNpc { IsDead: false } battleNpc)
        {
            foreach (var status in battleNpc.ToBCStruct()->StatusManager.Status)
            {
                if (!ModuleConfig.StatusToMonitor.TryGetValue(status.StatusId, out var actions))
                    continue;
                actionToHighlight.AddRange(actions.ToDictionary(x => x, _ => status.RemainingTime));
            }
        }

        var manager = ActionManager.Instance();
        ActionsToHighlight.Clear();
        foreach (var (actionID, time) in actionToHighlight)
        {
            var actionChain = FetchComboChain(actionID);

            var cutoff        = ModuleConfig.Threshold * actionChain.Length;
            var notInChain    = actionChain.All(id => !manager->IsActionHighlighted(ActionType.Action, id));
            var notLastAction = actionChain[..^1].All(id => id != LastActionID);

            if (time <= cutoff && notInChain && notLastAction)
                ActionsToHighlight.Add(actionChain[0]);
        }
    }

    private static void OnPreUseActionLocation(
        ref bool isPrevented, ref ActionType type, ref uint actionID, ref ulong targetID, ref Vector3 location, ref uint extraParam)
    {
        ActionsToHighlight.Remove(actionID);
        LastActionID = actionID;
    }

    private static bool IsActionHighlightedDetour(ActionManager* actionManager, ActionType actionType, uint actionID)
        => ActionsToHighlight.Contains(actionID) || IsActionHighlightedHook!.Original(actionManager, actionType, actionID);

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
        public float Threshold = 3.0f;

        public Dictionary<uint, List<uint>> StatusToMonitor = [];
    }

    // Status - Actions
    private static Dictionary<uint, List<uint>> StatusToAction = new()
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
