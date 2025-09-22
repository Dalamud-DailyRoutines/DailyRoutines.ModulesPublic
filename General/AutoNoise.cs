using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using Lumina.Excel.Sheets.Experimental;
using System;
using System.Collections.Generic;
using System.Linq;
using Action = Lumina.Excel.Sheets.Action;
using Mount = Lumina.Excel.Sheets.Mount;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoNoise : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("AutoNoiseTitle"),
        Description = GetLoc("AutoNoiseDescription"),
        Category = ModuleCategories.General,
        Author = ["逆光", "Bill"]
    };

    private static Config ModuleConfig = null!;
    private static LuminaSearcher<Mount>? MountSearcher;
    private static Mount? SelectedMount;
    private static string MountSearchInput = string.Empty;
    private static uint SelectedActionID;
    private static List<(Action act, uint ActionID, string Name)> AvailableActions = new();

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new Config();

        // 初始化坐骑搜索器
        MountSearcher ??= new(LuminaGetter.Get<Mount>()
                .Where(x => x.MountAction.RowId > 0)
                .Where(x => x.Icon > 0)
                .Where(x => !string.IsNullOrEmpty(x.Singular.ExtractText()))
                .GroupBy(x => x.Singular.ExtractText())
                .Select(x => x.First()),
            [x => x.RowId.ToString()],
            x => x.RowId.ToString());

        FrameworkManager.Register(OnUpdate, throttleMS: 1500);
    }

    protected override void ConfigUI()
    {
        using var table = ImRaii.Table("MountActionsTable", 3);
        if (!table) return;

        // 设置列
        ImGui.TableSetupColumn("坐骑", ImGuiTableColumnFlags.None, 30);
        ImGui.TableSetupColumn("动作", ImGuiTableColumnFlags.None, ImGui.CalcTextSize("四个汉字").X);
        ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.None, 10);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableNextColumn();
        if (ImGuiOm.SelectableIconCentered("AddNewAction", FontAwesomeIcon.Plus))
            ImGui.OpenPopup("AddNewActionPopup");

        using (var popup = ImRaii.Popup("AddNewActionPopup"))
        {
            if (popup)
            {
                using (ImRaii.Group())
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(LightSkyBlue, $"{GetLoc("Mount")}:");

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(250f * GlobalFontScale);
                    using (var combo = ImRaii.Combo("###MountSelectCombo",
                                                  SelectedMount == null ? "" :
                                                  $"{SelectedMount.Value.Singular.ExtractText()}",
                                                  ImGuiComboFlags.HeightLarge))
                    {
                        if (combo)
                        {
                            ImGui.SetNextItemWidth(-1f);
                            ImGui.InputTextWithHint("###MountSearchInput", GetLoc("PleaseSearch"),
                                                  ref MountSearchInput, 100);
                            if (ImGui.IsItemDeactivatedAfterEdit())
                                MountSearcher?.Search(MountSearchInput);

                            if (MountSearcher != null)
                            {
                                foreach (var mount in MountSearcher.SearchResult)
                                {
                                    var icon = ImageHelper.GetGameIcon(mount.Icon).Handle;
                                    if (ImGuiOm.SelectableImageWithText(icon,
                                        new(ImGui.GetTextLineHeightWithSpacing()),
                                        $"{mount.Singular.ExtractText()}",
                                        mount.Equals(SelectedMount)))
                                    {
                                        SelectedMount = mount;
                                        UpdateAvailableActions(mount);
                                    }
                                }
                            }
                        }
                    }

                    if (SelectedMount.HasValue && AvailableActions.Count > 0)
                    {
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextColored(LightSkyBlue, $"{GetLoc("Action")}:");

                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(250f * GlobalFontScale);

                        using var combo = ImRaii.Combo("###ActionSelectCombo",
                                                      GetActionName(SelectedActionID),
                                                      ImGuiComboFlags.None);
                        if (combo)
                        {
                            foreach (var (act, actionId, name) in AvailableActions)
                            {
                                var icon = ImageHelper.GetGameIcon(act.Icon).Handle;
                                if (ImGuiOm.SelectableImageWithText(icon,
                                        new(ImGui.GetTextLineHeightWithSpacing()),
                                        $"{name}",
                                        actionId == SelectedActionID))
                                    SelectedActionID = actionId;
                            }
                        }
                    }

                    ImGui.Spacing();
                    using (ImRaii.Disabled(SelectedMount == null || SelectedActionID == 0))
                    {
                        if (ImGui.Button(GetLoc("Add")))
                        {
                            var newAction = new MountAction(SelectedMount!.Value.RowId, SelectedActionID);
                            if (!ModuleConfig.MountActions.Contains(newAction))
                            {
                                ModuleConfig.MountActions.Add(newAction);
                                ModuleConfig.Save(this);
                            }
                            ImGui.CloseCurrentPopup();
                        }
                    }
                }
            }
        }

        // 显示已配置的动作列表
        foreach (var action in ModuleConfig.MountActions)
        {
            ImGui.TableNextRow();

            // 坐骑ID和特性
            ImGui.TableNextColumn();
            if (LuminaGetter.TryGetRow<Mount>(action.MountID, out var mount))
            {
                var icon = ImageHelper.GetGameIcon(mount.Icon);
                ImGuiOm.TextImage($"{mount.Singular.ExtractText()}",
                                 icon.Handle, ScaledVector2(24f));
            }

            // 动作ID
            ImGui.TableNextColumn();
            if (LuminaGetter.TryGetRow<Action>(action.ActionID, out var act))
            {
                var icon = ImageHelper.GetGameIcon(act.Icon);
                ImGuiOm.TextImage($"{act.Name.ExtractText()}",
                                                     icon.Handle, ScaledVector2(24f));
            }
            // 删除按钮
            ImGui.TableNextColumn();
            if (ImGuiOm.ButtonIcon($"{action.MountID}_Delete", FontAwesomeIcon.TrashAlt))
            {
                ModuleConfig.MountActions.Remove(action);
                ModuleConfig.Save(this);
            }
        }
    }
    
    private static string GetActionName(uint actionID)
    {
        if (actionID == 0)
            return string.Empty;
        if (LuminaGetter.TryGetRow<Action>(actionID, out var action))
            return action.Name.ExtractText();
        return $"{actionID}";
    }

    private void UpdateAvailableActions(Mount mount)
    {
        AvailableActions.Clear();
        SelectedActionID = 0;

        if (!LuminaGetter.TryGetRow<Lumina.Excel.Sheets.MountAction>(mount.MountAction.RowId, out var mountAction)) return;

        for (var i = 0; i < 6; i++)
        {
            var action = mountAction.Action[i];
            if (action.RowId > 0 && LuminaGetter.TryGetRow<Action>(action.RowId, out var act) && act.Range == 0)
                AvailableActions.Add((act, action.RowId, act.Name.ExtractText()));
        }
    }

    private static void OnUpdate(IFramework framework)
    {
        if (DService.ObjectTable.LocalPlayer is not { } localPlayer) return;
        if (!DService.Condition[ConditionFlag.Mounted]) return;

        var currentMountID = localPlayer.CurrentMount?.RowId;
        if (currentMountID == null) return;

        var action = ModuleConfig.MountActions.FirstOrDefault(x => x.MountID == currentMountID);
        if (action != null &&
            ActionManager.Instance()->GetActionStatus(ActionType.Action, action.ActionID) == 0)

            UseActionManager.UseAction(ActionType.Action, action.ActionID);
    }

    protected override void Uninit() => FrameworkManager.Unregister(OnUpdate);

    private class Config : ModuleConfiguration
    {
        public List<MountAction> MountActions { get; set; } = new();
    }

    private class MountAction : IEquatable<MountAction>
        {
            public uint MountID { get; set; }
            public uint ActionID { get; set; }

            public MountAction() { }

            public MountAction(uint mountID, uint actionID)
            {
                MountID = mountID;
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
