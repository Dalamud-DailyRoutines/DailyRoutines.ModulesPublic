using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using Action = Lumina.Excel.Sheets.Action;
using Mount = Lumina.Excel.Sheets.Mount;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoUseMountAction : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoUseMountActionTitle"),
        Description = Lang.Get("AutoUseMountActionDescription"),
        Category    = ModuleCategory.General,
        Author      = ["逆光", "Bill"]
    };

    private Config config = null!;

    private string mountSearchInput = string.Empty;

    private uint selectedActionID;
    private uint selectedMountID;

    protected override void Init()
    {
        config =   Config.Load(this) ?? new();
        TaskHelper   ??= new();

        DService.Instance().Condition.ConditionChange += OnConditionChanged;
        UseActionManager.Instance().RegPostUseActionLocation(OnPostUseAction);

        if (DService.Instance().Condition[ConditionFlag.Mounted])
            OnConditionChanged(ConditionFlag.Mounted, true);
    }

    protected override void Uninit()
    {
        DService.Instance().Condition.ConditionChange -= OnConditionChanged;
        UseActionManager.Instance().Unreg(OnPostUseAction);

        TaskHelper?.Abort();
    }

    protected override void ConfigUI()
    {
        using var table = ImRaii.Table("MountActionsTable", 3);
        if (!table) return;

        // 设置列
        ImGui.TableSetupColumn("坐骑", ImGuiTableColumnFlags.None, 200);
        ImGui.TableSetupColumn("动作", ImGuiTableColumnFlags.None, 200);
        ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.None, 80);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);

        ImGui.TableNextColumn();
        if (ImGuiOm.SelectableIconCentered("AddNewAction", FontAwesomeIcon.Plus))
            ImGui.OpenPopup("AddNewActionPopup");

        using (var popup = ImRaii.Popup("AddNewActionPopup"))
        {
            if (popup)
            {
                ImGui.SetNextItemWidth(250f * GlobalUIScale);

                using (var combo = ImRaii.Combo
                       (
                           $"{LuminaWrapper.GetAddonText(4964)}##MountSelectCombo",
                           selectedMountID > 0 && LuminaGetter.TryGetRow(selectedMountID, out Mount selectedMount)
                               ? $"{selectedMount.Singular.ToString()}"
                               : string.Empty,
                           ImGuiComboFlags.HeightLarge
                       ))
                {
                    if (combo)
                    {
                        ImGui.SetNextItemWidth(-1f);
                        if (ImGui.InputTextWithHint("###MountSearchInput", Lang.Get("PleaseSearch"), ref mountSearchInput, 128))
                            MountSearcher?.Search(mountSearchInput);

                        if (MountSearcher != null)
                        {
                            foreach (var mount in MountSearcher.SearchResult)
                            {
                                if (!ImageHelper.TryGetGameIcon(mount.Icon, out var textureWrap)) continue;

                                if (ImGuiOm.SelectableImageWithText
                                    (
                                        textureWrap.Handle,
                                        new(ImGui.GetTextLineHeightWithSpacing()),
                                        $"{mount.Singular.ToString()}",
                                        mount.RowId == selectedMountID
                                    ))
                                    selectedMountID = mount.RowId;
                            }
                        }
                    }
                }

                if (selectedMountID > 0                                              &&
                    LuminaGetter.TryGetRow(selectedMountID, out Mount mountSelected) &&
                    mountSelected.MountAction.ValueNullable is { Action: { Count: > 0 } actions })
                {
                    ImGui.SetNextItemWidth(250f * GlobalUIScale);
                    using var combo = ImRaii.Combo
                    (
                        $"{Lang.Get("Action")}###ActionSelectCombo",
                        LuminaWrapper.GetActionName(selectedActionID),
                        ImGuiComboFlags.None
                    );

                    if (combo)
                    {
                        foreach (var action in actions)
                        {
                            if (action.RowId == 0) continue;
                            if (!ImageHelper.TryGetGameIcon(action.Value.Icon, out var textureWrap)) continue;

                            if (ImGuiOm.SelectableImageWithText
                                (
                                    textureWrap.Handle,
                                    new(ImGui.GetTextLineHeightWithSpacing()),
                                    $"{action.Value.Name}",
                                    action.RowId == selectedActionID
                                ))
                                selectedActionID = action.RowId;
                        }
                    }
                }

                ImGui.Spacing();

                using (ImRaii.Disabled(selectedMountID == 0 || selectedActionID == 0))
                {
                    if (ImGui.Button(Lang.Get("Add")))
                    {
                        var newAction = new MountAction(selectedMountID, selectedActionID);
                        if (config.MountActions.TryAdd(newAction.MountID, newAction))
                            config.Save(this);

                        ImGui.CloseCurrentPopup();
                    }
                }
            }
        }

        // 显示已配置的动作列表
        foreach (var kv in config.MountActions)
        {
            var action = kv.Value;
            ImGui.TableNextRow();

            // 坐骑ID和特性
            ImGui.TableNextColumn();
            if (LuminaGetter.TryGetRow<Mount>(action.MountID, out var mountRow) && ImageHelper.TryGetGameIcon(mountRow.Icon, out var mountIcon))
                ImGuiOm.TextImage($"{mountRow.Singular.ToString()}", mountIcon.Handle, new(ImGui.GetTextLineHeightWithSpacing()));

            // 动作ID
            ImGui.TableNextColumn();
            if (LuminaGetter.TryGetRow<Action>(action.ActionID, out var actionRow) && ImageHelper.TryGetGameIcon(actionRow.Icon, out var actionIcon))
                ImGuiOm.TextImage($"{actionRow.Name.ToString()}", actionIcon.Handle, new(ImGui.GetTextLineHeightWithSpacing()));

            // 删除按钮
            ImGui.TableNextColumn();

            if (ImGuiOm.ButtonIcon($"{action.MountID}_Delete", FontAwesomeIcon.TrashAlt))
            {
                config.MountActions.Remove(action.MountID);
                config.Save(this);
            }
        }
    }

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (InvalidConditions.Contains(flag))
        {
            if (value)
                TaskHelper.Abort();
            else
            {
                if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer ||
                    !config.MountActions.ContainsKey(localPlayer.CurrentMount?.RowId ?? 0)) return;
                TaskHelper.Enqueue(UseAction);
            }
        }

        if (flag == ConditionFlag.Mounted)
        {
            if (value)
            {
                if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer ||
                    !config.MountActions.ContainsKey(localPlayer.CurrentMount?.RowId ?? 0)) return;
                TaskHelper.Enqueue(UseAction);
            }
            else
                TaskHelper.Abort();
        }
    }

    private void OnPostUseAction(bool result, ActionType actionType, uint actionID, ulong targetID, Vector3 location, uint extraParam, byte a7)
    {
        if (actionType != ActionType.Action) return;
        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return;

        var mountID = localPlayer.CurrentMount?.RowId ?? 0;
        if (!config.MountActions.TryGetValue(mountID, out var action) || action.ActionID != actionID) return;

        TaskHelper.Enqueue(UseAction);
    }

    private bool UseAction()
    {
        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return true;

        var mountID = localPlayer.CurrentMount?.RowId ?? 0;
        if (!config.MountActions.TryGetValue(mountID, out var action)) return true;

        if (ActionManager.Instance()->GetActionStatus(ActionType.Action, action.ActionID) != 0) return false;

        ActionManager.Instance()->UseAction(ActionType.Action, action.ActionID);
        return true;
    }

    private class Config : ModuleConfig
    {
        public Dictionary<uint, MountAction> MountActions { get; set; } = new();
    }

    private class MountAction : IEquatable<MountAction>
    {
        public MountAction() { }

        public MountAction(uint mountID, uint actionID)
        {
            MountID  = mountID;
            ActionID = actionID;
        }

        public uint MountID  { get; set; }
        public uint ActionID { get; set; }

        public bool Equals(MountAction? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return MountID == other.MountID;
        }

        public override bool Equals(object? obj) =>
            obj is MountAction other && Equals(other);

        public override int GetHashCode() =>
            (int)MountID;
    }
    
    #region 常量

    private static readonly FrozenSet<ConditionFlag> InvalidConditions =
    [
        ConditionFlag.InFlight,
        ConditionFlag.Diving
    ];

    private static readonly LuminaSearcher<Mount> MountSearcher = new
    (
        LuminaGetter.Get<Mount>()
                    .Where(x => x.MountAction.RowId > 0)
                    .Where(x => x.Icon              > 0)
                    .Where(x => !string.IsNullOrEmpty(x.Singular.ToString()))
                    .GroupBy(x => x.Singular.ToString())
                    .Select(x => x.First()),
        [x => x.Singular.ToString()]
    );

    #endregion
}
