using System.Collections.Generic;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoPreventGuardInterrupt : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("AutoPreventGuardInterruptTitle"),
        Description = GetLoc("AutoPreventGuardInterruptDescription"),
        Category = ModuleCategories.Action,
        Author = ["XSZYYS"]
    };

    private static Config ModuleConfig = null!;
    private static int newWhitelistIDInput;
    private const uint guardStatusID = 3054;
    private const uint hideStatusID = 1316;

    private class Config : ModuleConfiguration
    {
        public bool Enabled = true;
        public bool SendNotification = true;
        public float InterruptThreshold = 0.5f;
        public HashSet<uint> CustomWhitelist = new();
    }
    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        SaveConfig(ModuleConfig);
        UseActionManager.RegPreUseAction(OnPreUseAction);
    }

    protected override void Uninit()
    {
        UseActionManager.Unreg(OnPreUseAction);
        SaveConfig(ModuleConfig);
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("Enable"), ref ModuleConfig.Enabled))
            SaveConfig(ModuleConfig);
        ImGuiOm.HelpMarker(GetLoc("AutoPreventGuardInterrupt-EnableHelp"));

        ImGui.SameLine();
        if (ImGui.Checkbox(GetLoc("SendNotification"), ref ModuleConfig.SendNotification))
            SaveConfig(ModuleConfig);

        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        if (ImGui.SliderFloat(GetLoc("AutoPreventGuardInterrupt-InterruptThreshold"), ref ModuleConfig.InterruptThreshold, 0.0f, 4.0f, "%.1f s"))
            SaveConfig(ModuleConfig);
        ImGuiOm.HelpMarker(GetLoc("AutoPreventGuardInterrupt-InterruptThresholdHelp"));
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        using var node = ImRaii.TreeNode($"{GetLoc("Whitelist")} ({ModuleConfig.CustomWhitelist.Count})###WhitelistNode");
        if (node)
        {
            ImGuiOm.HelpMarker(GetLoc("AutoPreventGuardInterrupt-WhitelistHelp"));
            
            ImGui.SetNextItemWidth(150f * GlobalFontScale);
            ImGui.InputInt("##newWhitelistId", ref newWhitelistIDInput);


            ImGui.SameLine();
            if (ImGui.Button(GetLoc("Add")))
            {
                if (newWhitelistIDInput > 0 && ModuleConfig.CustomWhitelist.Add((uint)newWhitelistIDInput))
                {
                    SaveConfig(ModuleConfig);
                    newWhitelistIDInput = 0;
                }
            }

            // 显示当前的白名单列表
            ImGui.Spacing();
            var tableSize = new Vector2(ImGui.GetContentRegionAvail().X - ImGui.GetTextLineHeightWithSpacing(), 0);
            using var table = ImRaii.Table("###WhitelistTable", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg, tableSize);
            if (!table) return;
            ImGui.TableSetupColumn(GetLoc("ID"), ImGuiTableColumnFlags.WidthFixed, 50 * GlobalFontScale);
            ImGui.TableSetupColumn(GetLoc("Action"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(GetLoc("Operation"), ImGuiTableColumnFlags.WidthFixed, 80 * GlobalFontScale);
            ImGui.TableHeadersRow();
            
            uint idToRemove = 0;
            foreach (var id in ModuleConfig.CustomWhitelist)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.Text(id.ToString());

                ImGui.TableNextColumn();
                var actionData = LuminaGetter.GetRow<Action>(id);
                var actionName = actionData?.Name.ExtractText() ?? GetLoc("UnknownAction");
                ImGui.Text(actionName);
                
                ImGui.TableNextColumn();
                using var idScope = ImRaii.PushId((int)id);
                if (ImGui.SmallButton(GetLoc("Remove")))
                    idToRemove = id;
            }

            if (idToRemove != 0)
            {
                ModuleConfig.CustomWhitelist.Remove(idToRemove);
                SaveConfig(ModuleConfig);
            }
        }
    }

    private static void OnPreUseAction(
        ref bool isPrevented,
        ref ActionType actionType,
        ref uint actionID,
        ref ulong targetID,
        ref uint extraParam,
        ref ActionManager.UseActionMode queueState,
        ref uint comboRouteID)
    {
        if (!ModuleConfig.Enabled) return;

        if (actionType != ActionType.Action) return;

        if (!HasBlockingStatus()) return;

        var adjustedActionID = ActionManager.Instance()->GetAdjustedActionId(actionID);

        if (ModuleConfig.CustomWhitelist.Contains(adjustedActionID)) return;

        isPrevented = true;

        if (ModuleConfig.SendNotification && Throttler.Throttle("AutoPreventGuardInterrupt-Notification", 1000))
        {
            var actionData = LuminaGetter.GetRow<Action>(adjustedActionID);
            var actionName = actionData?.Name.ExtractText() ?? GetLoc("UnknownAction");
            NotificationInfo(GetLoc("AutoPreventGuardInterrupt-PreventedNotification", actionName));
        }
    }
    
    private static bool HasBlockingStatus()
    {
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return false;

        var statusManager = &localPlayer->StatusManager;
        if (statusManager == null) return false;

        var guardStatusIndex = statusManager->GetStatusIndex(guardStatusID);
        if (guardStatusIndex != -1)
        {
            var guardStatus = statusManager->Status[guardStatusIndex];
            if (guardStatus.RemainingTime > ModuleConfig.InterruptThreshold) return true;
        }
        var hideStatusIndex = statusManager->GetStatusIndex(hideStatusID);
        if (hideStatusIndex != -1)
        {
            var hideStatus = statusManager->Status[hideStatusIndex];
            if (hideStatus.RemainingTime > ModuleConfig.InterruptThreshold) return true;
        }
        
        return false;
    }
    

}

