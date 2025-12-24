using System.Collections.Generic;
using System.Numerics;
using DailyRoutines.Abstracts;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public class AutoRedirectDashActions : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoRedirectDashActionsTitle"),
        Description = GetLoc("AutoRedirectDashActionsDescription"),
        Category    = ModuleCategories.Action,
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true, CNPremium = true, TCPremium = true };

    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        UseActionManager.RegPreUseActionLocation(OnPreUseAction);
    }

    protected override void ConfigUI()
    {
        var tableSize = (ImGui.GetContentRegionAvail() / 2) with { Y = 0 };
        using var table = ImRaii.Table("ActionEnabled", 2, ImGuiTableFlags.BordersInnerH, tableSize);
        if (!table) return;

        ImGui.TableSetupColumn("选框", ImGuiTableColumnFlags.WidthFixed, ImGui.GetTextLineHeightWithSpacing());
        ImGui.TableSetupColumn("技能", ImGuiTableColumnFlags.WidthStretch);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableNextColumn();

        ImGui.TableNextColumn();
        ImGuiOm.Text(GetLoc("Action"));

        foreach (var actionPair in ModuleConfig.ActionsEnabled)
        {
            if (!LuminaGetter.TryGetRow<Action>(actionPair.Key, out var data)) continue;

            var actionIcon = DService.Texture.GetFromGameIcon(new(data.Icon)).GetWrapOrDefault();
            if (actionIcon == null) continue;

            using var id = ImRaii.PushId($"{actionPair.Key}");
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var isEnabled = actionPair.Value;
            if (ImGui.Checkbox($"###{actionPair.Key}", ref isEnabled))
            {
                ModuleConfig.ActionsEnabled[actionPair.Key] = isEnabled;
                ModuleConfig.Save(this);
            }

            ImGui.TableNextColumn();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (4f * GlobalFontScale));
            ImGui.Image(actionIcon.Handle, new(ImGui.GetTextLineHeight()));
            
            ImGui.SameLine();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() - (2f * GlobalFontScale));
            ImGui.Text($"{data.Name.ExtractText()}");
        }
    }

    public static unsafe void OnPreUseAction(
        ref bool       isPrevented,
        ref ActionType actionType,
        ref uint       actionID,
        ref ulong      targetID,
        ref Vector3    location,
        ref uint       extraParam,
        ref byte       a7)
    {
        if (actionType != ActionType.Action) return;

        var adjustedAction = ActionManager.Instance()->GetAdjustedActionId(actionID);
        if (!ModuleConfig.ActionsEnabled.TryGetValue(adjustedAction, out var isEnabled) || !isEnabled) return;

        var localPlayer = DService.ObjectTable.LocalPlayer;
        if (localPlayer == null) return;

        if (!LuminaGetter.TryGetRow<Action>(adjustedAction, out var data)) return;
        if (data is not { TargetArea: true }) return;

        if (ActionManager.Instance()->GetActionStatus(actionType, adjustedAction) != 0) return;
        if (!DService.Gui.ScreenToWorld(ImGui.GetMousePos(), out var pos)) return;

        pos      = AdjustTargetPosition(localPlayer.Position, pos, data.Range);
        location = pos;
    }

    public static Vector3 AdjustTargetPosition(Vector3 origin, Vector3 target, float maxDistance)
    {
        var originXZ = origin.ToVector2();
        var targetXZ = target.ToVector2();
        var distance = Vector2.DistanceSquared(originXZ, targetXZ);

        if (distance > maxDistance * maxDistance)
        {
            var direction = Vector2.Normalize(targetXZ - originXZ);
            targetXZ = originXZ + (direction * maxDistance);
            return new Vector3(targetXZ.X, target.Y, targetXZ.Y);
        }

        return target;
    }

    protected override void Uninit() => 
        UseActionManager.Unreg(OnPreUseAction);

    private class Config : ModuleConfiguration
    {
        public Dictionary<uint, bool> ActionsEnabled = new()
        {
            // 魔纹步
            [7419] = true,
            // 回退
            [24403] = true,
            // 回退 (PVP)
            [29551] = true,
            // 逆行 (PVP)
            [41507] = true,
        };
    }
}
