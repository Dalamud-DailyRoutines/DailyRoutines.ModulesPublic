using System;
using System.Collections.Generic;
using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using Action = Lumina.Excel.Sheets.Action;

namespace DailyRoutines.ModulesPublic;

public unsafe class CustomActionCastRecastTime : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("CustomActionCastRecastTimeTitle"),
        Description = GetLoc("CustomActionCastRecastTimeDescription"),
        Category    = ModuleCategories.Action
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private delegate int GetAdjustedCastTimeDelegate
    (
        ActionType                  actionType,
        uint                        actionID,
        bool                        applyProc,
        ActionManager.CastTimeProc* outCastTimeProc
    );
    private static Hook<GetAdjustedCastTimeDelegate>? GetAdjustedCastTimeHook;

    private delegate int GetAdjustedRecastTimeDelegate
    (
        ActionType actionType,
        uint       actionID,
        bool       applyClassMechanics
    );
    private static Hook<GetAdjustedRecastTimeDelegate>? GetAdjustedRecastTimeHook;

    private static readonly CompSig                            CastInfoUpdateTotalSig = new("48 89 5C 24 ?? 57 48 83 EC ?? 48 8B F9 0F 29 74 24 ?? 0F B6 49");
    private delegate        uint                               CastInfoUpdateTotalDelegate(nint data, uint spellActionID, float process, float processTotal);
    private static          Hook<CastInfoUpdateTotalDelegate>? CastInfoUpdateTotalHook;

    private static readonly CompSig CastTimeCurrentSig = new("F3 44 0F 2C C0 BA ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? F3 44 0F 10 1D");
    private static          float*  CastTimeCurrent;

    private static Config ModuleConfig = null!;

    private static readonly ActionSelectCombo CastActionCombo = new("CastActionSelect");
    private static readonly JobSelectCombo    CastJobCombo    = new("CastJobSelect");
    
    private static readonly ActionSelectCombo RecastActionCombo = new("RecastActionSelect");
    private static readonly JobSelectCombo    RecastJobCombo    = new("RecastJobSelect");

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        GetAdjustedCastTimeHook ??=
            DService.Instance().Hook.HookFromMemberFunction
            (
                typeof(ActionManager.MemberFunctionPointers),
                "GetAdjustedCastTime",
                (GetAdjustedCastTimeDelegate)GetAdjustedCastTimeDetour
            );
        GetAdjustedCastTimeHook.Enable();
        
        GetAdjustedRecastTimeHook ??=
            DService.Instance().Hook.HookFromMemberFunction
            (
                typeof(ActionManager.MemberFunctionPointers),
                "GetAdjustedRecastTime",
                (GetAdjustedRecastTimeDelegate)GetAdjustedRecastTimeDetour
            );
        GetAdjustedRecastTimeHook.Enable();

        CastTimeCurrent = CastTimeCurrentSig.GetStatic<float>(0x12);

        CastInfoUpdateTotalHook ??= CastInfoUpdateTotalSig.GetHook<CastInfoUpdateTotalDelegate>(CastInfoUpdateTotalDetour);
        CastInfoUpdateTotalHook.Enable();
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("CustomActionCastRecastTime-CustomCastTime")}");

        using (ImRaii.PushId("Cast"))
        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(150f * GlobalFontScale);

            if (ImGui.InputFloat
                (
                    $"{GetLoc("CustomActionCastRecastTime-DefaultReduction")}(ms)###DefaultReduction",
                    ref ModuleConfig.LongCastTimeReduction,
                    10,
                    100,
                    "%.0f"
                ))
            {
                ModuleConfig.LongCastTimeReduction = MathF.Max(0, ModuleConfig.LongCastTimeReduction);
                SaveConfig(ModuleConfig);
            }

            ImGuiOm.HelpMarker(GetLoc("CustomActionCastRecastTime-DefaultReduction-Help", ModuleConfig.LongCastTimeReduction));

            ImGui.NewLine();

            using (ImRaii.Disabled(CastActionCombo.SelectedID == 0 || ModuleConfig.CustomCastTimeSet.ContainsKey(CastActionCombo.SelectedID)))
            {
                if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, GetLoc("Add")))
                {
                    if (ModuleConfig.CustomCastTimeSet.TryAdd
                        (
                            CastActionCombo.SelectedID,
                            ActionManager.GetAdjustedCastTime(ActionType.Action, CastActionCombo.SelectedID)
                        ))
                        SaveConfig(ModuleConfig);
                }
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(300f * GlobalFontScale);
            CastActionCombo.DrawRadio();
            
            using (ImRaii.Disabled(CastJobCombo.SelectedID == 0))
            {
                if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, $"{GetLoc("Add")}##ClassJob") &&
                    LuminaGetter.TryGetSubRowAll<ClassJobActionUI>(CastJobCombo.SelectedID, out var rows))
                {
                    foreach (var actionUI in rows)
                    {
                        if (actionUI.UpgradeAction.RowId == 0 || actionUI.UpgradeAction.Value.Name.IsEmpty) continue;
                        
                        ModuleConfig.CustomCastTimeSet.TryAdd
                        (
                            actionUI.UpgradeAction.RowId,
                            ActionManager.GetAdjustedCastTime(ActionType.Action, actionUI.UpgradeAction.RowId)
                        );
                    }
                    
                    SaveConfig(ModuleConfig);
                }
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(300f * GlobalFontScale);
            CastJobCombo.DrawRadio();

            ImGui.Spacing();
            
            using (var table = ImRaii.Table("OptimizedLongCastTimeActionCastTable", 3, ImGuiTableFlags.Borders))
            {
                if (table)
                {
                    ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(1340),          ImGuiTableColumnFlags.WidthStretch, 40);
                    ImGui.TableSetupColumn($"{LuminaWrapper.GetAddonText(701)} (ms)", ImGuiTableColumnFlags.WidthStretch, 20);
                    ImGui.TableSetupColumn(GetLoc("Operation"),                       ImGuiTableColumnFlags.WidthFixed,   2 * ImGui.GetTextLineHeightWithSpacing());

                    ImGui.TableHeadersRow();

                    uint actionToRemove = 0;

                    foreach (var pair in ModuleConfig.CustomCastTimeSet)
                    {
                        if (!LuminaGetter.TryGetRow<Action>(pair.Key, out var action)) continue;

                        ImGui.TableNextRow();
                        
                        ImGui.TableNextColumn();
                        if (DService.Instance().Texture.TryGetFromGameIcon(new(action.Icon), out var icon))
                        {
                            ImGuiOm.SelectableImageWithText
                            (
                                icon.GetWrapOrEmpty().Handle,
                                new(ImGui.GetTextLineHeightWithSpacing()),
                                $"{action.Name.ToString()} ({action.RowId})",
                                false
                            );
                        }
                        else
                            ImGui.TextUnformatted($"{action.Name.ToString()} ({action.RowId})");

                        ImGui.TableNextColumn();
                        var customTime = pair.Value;
                        ImGui.SetNextItemWidth(-1);
                        if (ImGui.InputFloat($"###CustomCastTime_{pair.Key}", ref customTime, 10, 100, "%.0f"))
                            ModuleConfig.CustomCastTimeSet[pair.Key] = MathF.Max(0, customTime);

                        if (ImGui.IsItemDeactivatedAfterEdit())
                            SaveConfig(ModuleConfig);

                        ImGui.TableNextColumn();
                        if (ImGuiOm.ButtonIcon($"###DeleteCast_{pair.Key}", FontAwesomeIcon.Trash, GetLoc("Delete")))
                            actionToRemove = pair.Key;
                    }

                    if (actionToRemove != 0)
                    {
                        ModuleConfig.CustomCastTimeSet.Remove(actionToRemove);
                        SaveConfig(ModuleConfig);
                    }
                }
            }
        }
        
        ImGui.NewLine();
        
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("CustomActionCastRecastTime-CustomRecastTime")}");

        using (ImRaii.PushId("Recast"))
        using (ImRaii.PushIndent())
        {
            using (ImRaii.Disabled(RecastActionCombo.SelectedID == 0 || ModuleConfig.CustomRecastTimeSet.ContainsKey(RecastActionCombo.SelectedID)))
            {
                if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, $"{GetLoc("Add")}##Action") &&
                    ModuleConfig.CustomRecastTimeSet.TryAdd
                    (
                        RecastActionCombo.SelectedID,
                        ActionManager.GetAdjustedRecastTime(ActionType.Action, RecastActionCombo.SelectedID)
                    ))
                    SaveConfig(ModuleConfig);
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(300f * GlobalFontScale);
            RecastActionCombo.DrawRadio();
            
            using (ImRaii.Disabled(RecastJobCombo.SelectedID == 0))
            {
                if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, $"{GetLoc("Add")}##ClassJob") &&
                    LuminaGetter.TryGetSubRowAll<ClassJobActionUI>(RecastJobCombo.SelectedID, out var rows))
                {
                    foreach (var actionUI in rows)
                    {
                        if (actionUI.UpgradeAction.RowId == 0 || actionUI.UpgradeAction.Value.Name.IsEmpty) continue;
                        
                        ModuleConfig.CustomRecastTimeSet.TryAdd
                        (
                            actionUI.UpgradeAction.RowId,
                            ActionManager.GetAdjustedRecastTime(ActionType.Action, actionUI.UpgradeAction.RowId)
                        );
                    }
                    
                    SaveConfig(ModuleConfig);
                }
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(300f * GlobalFontScale);
            RecastJobCombo.DrawRadio();

            ImGui.Spacing();
            
            using (var table = ImRaii.Table("OptimizedLongCastTimeActionRecastTable", 3, ImGuiTableFlags.Borders))
            {
                if (table)
                {
                    ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(1340),          ImGuiTableColumnFlags.WidthStretch, 40);
                    ImGui.TableSetupColumn($"{LuminaWrapper.GetAddonText(702)} (ms)", ImGuiTableColumnFlags.WidthStretch, 20);
                    ImGui.TableSetupColumn(GetLoc("Operation"),                       ImGuiTableColumnFlags.WidthFixed,   2 * ImGui.GetTextLineHeightWithSpacing());

                    ImGui.TableHeadersRow();

                    var actionToRemove = -1;
                    foreach (var pair in ModuleConfig.CustomRecastTimeSet)
                    {
                        if (!LuminaGetter.TryGetRow<Action>(pair.Key, out var action)) continue;

                        ImGui.TableNextRow();
                        
                        ImGui.TableNextColumn();
                        if (DService.Instance().Texture.TryGetFromGameIcon(new(action.Icon), out var icon))
                        {
                            ImGuiOm.SelectableImageWithText
                            (
                                icon.GetWrapOrEmpty().Handle,
                                new(ImGui.GetTextLineHeightWithSpacing()),
                                $"{action.Name.ToString()} ({action.RowId})",
                                false
                            );
                        }
                        else
                            ImGui.TextUnformatted($"{action.Name.ToString()} ({action.RowId})");

                        ImGui.TableNextColumn();
                        var customTime = pair.Value;
                        ImGui.SetNextItemWidth(-1);
                        if (ImGui.InputFloat($"###CustomRecastTime_{pair.Key}", ref customTime, 10, 100, "%.0f"))
                            ModuleConfig.CustomRecastTimeSet[pair.Key] = MathF.Max(0, customTime);

                        if (ImGui.IsItemDeactivatedAfterEdit())
                            SaveConfig(ModuleConfig);

                        ImGui.TableNextColumn();
                        if (ImGuiOm.ButtonIcon($"###DeleteRecast_{pair.Key}", FontAwesomeIcon.Trash, GetLoc("Delete")))
                            actionToRemove = (int)pair.Key;
                    }

                    if (actionToRemove != -1)
                    {
                        ModuleConfig.CustomRecastTimeSet.Remove((uint)actionToRemove);
                        SaveConfig(ModuleConfig);
                    }
                }
            }
        }
    }

    private static int GetAdjustedRecastTimeDetour(ActionType actionType, uint actionID, bool applyClassMechanics)
    {
        var orig = GetAdjustedRecastTimeHook.Original(actionType, actionID, applyClassMechanics);
        if (actionType != ActionType.Action) return orig;

        if (ModuleConfig.CustomRecastTimeSet.TryGetValue(actionID, out var customTime))
            return (int)customTime;

        return orig;
    }
    
    private static int GetAdjustedCastTimeDetour(ActionType actionType, uint actionID, bool applyProcess, ActionManager.CastTimeProc* castTimeProc)
    {
        var orig = GetAdjustedCastTimeHook.Original(actionType, actionID, applyProcess, castTimeProc);
        if (actionType != ActionType.Action) return orig;

        if (ModuleConfig.CustomCastTimeSet.TryGetValue(actionID, out var customTime))
            return (int)customTime;

        // 咏唱大于复唱
        var recastTime = ActionManager.GetAdjustedRecastTime(actionType, actionID);
        if (recastTime <= orig) 
            return (int)MathF.Max(0, orig - (int)ModuleConfig.LongCastTimeReduction);

        return orig;
    }

    private static uint CastInfoUpdateTotalDetour(nint data, uint spellActionID, float processTotal, float processStart)
    {
        var actionID   = *(uint*)((byte*)data + 4);
        var actionType = (ActionType)(*((byte*)data + 2));

        if (actionID == spellActionID && actionType == ActionType.Action)
        {
            if (ModuleConfig.CustomCastTimeSet.TryGetValue(actionID, out var customTime))
            {
                processTotal     = customTime / 1000f;
                *CastTimeCurrent = processTotal;
            }
            else
            {
                var recastTime = ActionManager.GetAdjustedRecastTime(actionType, actionID);

                if (recastTime <= processTotal * 1000)
                {
                    processTotal     = Math.Max(processTotal - ModuleConfig.LongCastTimeReduction / 1000f, 0);
                    *CastTimeCurrent = processTotal;
                }
            }
        }

        return CastInfoUpdateTotalHook.Original(data, spellActionID, processTotal, processStart);
    }

    private class Config : ModuleConfiguration
    {
        // 咏唱
        public float                   LongCastTimeReduction = 400; // 毫秒
        public Dictionary<uint, float> CustomCastTimeSet     = [];
        
        // 复唱
        public Dictionary<uint, float> CustomRecastTimeSet = [];
    }
}
