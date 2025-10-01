using System;
using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Action = Lumina.Excel.Sheets.Action;
using Mount = Lumina.Excel.Sheets.Mount;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoUseMountAction : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoUseMountActionTitle"),
        Description = GetLoc("AutoUseMountActionDescription"),
        Category    = ModuleCategories.General,
        Author      = ["逆光", "Bill"]
    };

    private static Config                 ModuleConfig = null!;
    private static LuminaSearcher<Mount>? MountSearcher;
    
    private static string MountSearchInput = string.Empty;
    
    private static uint SelectedActionID;
    private static uint SelectedMountID;
    
    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new Config();

        MountSearcher ??= new(LuminaGetter.Get<Mount>()
                                          .Where(x => x.MountAction.RowId > 0)
                                          .Where(x => x.Icon              > 0)
                                          .Where(x => !string.IsNullOrEmpty(x.Singular.ExtractText()))
                                          .GroupBy(x => x.Singular.ExtractText())
                                          .Select(x => x.First()),
                              [x => x.Singular.ExtractText()],
                              x => x.Singular.ExtractText());
        
        DService.Condition.ConditionChange += OnConditionChanged;
        if (DService.Condition[ConditionFlag.Mounted])
            OnConditionChanged(ConditionFlag.Mounted, true);
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
                ImGui.SetNextItemWidth(250f * GlobalFontScale);
                using (var combo = ImRaii.Combo($"{LuminaWrapper.GetAddonText(4964)}##MountSelectCombo",
                                                SelectedMountID > 0 && LuminaGetter.TryGetRow(SelectedMountID, out Mount selectedMount)
                                                    ? $"{selectedMount.Singular.ExtractText()}"
                                                    : string.Empty,
                                                ImGuiComboFlags.HeightLarge))
                {
                    if (combo)
                    {
                        ImGui.SetNextItemWidth(-1f);
                        if (ImGui.InputTextWithHint("###MountSearchInput", GetLoc("PleaseSearch"), ref MountSearchInput, 128))
                            MountSearcher?.Search(MountSearchInput);

                        if (MountSearcher != null)
                        {
                            foreach (var mount in MountSearcher.SearchResult)
                            {
                                if (!ImageHelper.TryGetGameIcon(mount.Icon, out var textureWrap)) continue;

                                if (ImGuiOm.SelectableImageWithText(textureWrap.Handle,
                                                                    new(ImGui.GetTextLineHeightWithSpacing()),
                                                                    $"{mount.Singular.ExtractText()}",
                                                                    mount.RowId == SelectedMountID))
                                    SelectedMountID = mount.RowId;
                            }
                        }
                    }
                }

                if (SelectedMountID > 0                                              &&
                    LuminaGetter.TryGetRow(SelectedMountID, out Mount mountSelected) &&
                    mountSelected.MountAction.ValueNullable is { Action: { Count: > 0 } actions })
                {
                    ImGui.SetNextItemWidth(250f * GlobalFontScale);
                    using var combo = ImRaii.Combo($"{GetLoc("Action")}###ActionSelectCombo",
                                                   LuminaWrapper.GetActionName(SelectedActionID),
                                                   ImGuiComboFlags.None);
                    if (combo)
                    {
                        foreach (var action in actions)
                        {
                            if (action.RowId == 0) continue;
                            if (!ImageHelper.TryGetGameIcon(action.Value.Icon, out var textureWrap)) continue;

                            if (ImGuiOm.SelectableImageWithText(textureWrap.Handle, new(ImGui.GetTextLineHeightWithSpacing()), $"{action.Value.Name}",
                                                                action.RowId == SelectedActionID))
                                SelectedActionID = action.RowId;
                        }
                    }
                }

                ImGui.Spacing();
                using (ImRaii.Disabled(SelectedMountID == 0 || SelectedActionID == 0))
                {
                    if (ImGui.Button(GetLoc("Add")))
                    {
                        var newAction = new MountAction(SelectedMountID, SelectedActionID);
                        if (ModuleConfig.MountActions.TryAdd(newAction.MountID, newAction))
                            ModuleConfig.Save(this);

                        ImGui.CloseCurrentPopup();
                    }
                }
            }
        }

        // 显示已配置的动作列表
        foreach (var kv in ModuleConfig.MountActions)
        {
            var action = kv.Value;
            ImGui.TableNextRow();

            // 坐骑ID和特性
            ImGui.TableNextColumn();
            if (LuminaGetter.TryGetRow<Mount>(action.MountID, out var mountRow) && ImageHelper.TryGetGameIcon(mountRow.Icon, out var mountIcon))
                ImGuiOm.TextImage($"{mountRow.Singular.ExtractText()}", mountIcon.Handle, new(ImGui.GetTextLineHeightWithSpacing()));

            // 动作ID
            ImGui.TableNextColumn();
            if (LuminaGetter.TryGetRow<Action>(action.ActionID, out var actionRow) && ImageHelper.TryGetGameIcon(actionRow.Icon, out var actionIcon))
                ImGuiOm.TextImage($"{actionRow.Name.ExtractText()}", actionIcon.Handle, new(ImGui.GetTextLineHeightWithSpacing()));

            // 删除按钮
            ImGui.TableNextColumn();
            if (ImGuiOm.ButtonIcon($"{action.MountID}_Delete", FontAwesomeIcon.TrashAlt))
            {
                ModuleConfig.MountActions.Remove(action.MountID);
                ModuleConfig.Save(this);
            }
        }
    }
    
    private static void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.Mounted) return;

        FrameworkManager.Unregister(OnUpdate);
        
        if (!value) return;
        
        FrameworkManager.Register(OnUpdate, throttleMS: 1500);
    }
    
    private static void OnUpdate(IFramework framework)
    {
        if (DService.ObjectTable.LocalPlayer is not { } localPlayer) return;
        if (!DService.Condition[ConditionFlag.Mounted]) return;

        var currentMountID = localPlayer.CurrentMount?.RowId ?? 0;
        if (currentMountID == 0) return;

        if (ModuleConfig.MountActions.TryGetValue(currentMountID, out var action) &&
            ActionManager.Instance()->GetActionStatus(ActionType.Action, action.ActionID) == 0)
            UseActionManager.UseAction(ActionType.Action, action.ActionID);
    }

    protected override void Uninit()
    {
        DService.Condition.ConditionChange -= OnConditionChanged;
        OnConditionChanged(ConditionFlag.Mounted, false);
    }

    private class Config : ModuleConfiguration
    {
        public Dictionary<uint, MountAction> MountActions { get; set; } = new();
    }

    private class MountAction : IEquatable<MountAction>
    {
        public uint MountID  { get; set; }
        public uint ActionID { get; set; }

        public MountAction() { }

        public MountAction(uint mountID, uint actionID)
        {
            MountID  = mountID;
            ActionID = actionID;
        }

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
}
