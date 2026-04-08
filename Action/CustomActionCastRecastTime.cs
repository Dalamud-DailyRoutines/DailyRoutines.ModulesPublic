using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using Action = Lumina.Excel.Sheets.Action;

namespace DailyRoutines.ModulesPublic;

public unsafe class CustomActionCastRecastTime : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("CustomActionCastRecastTimeTitle"),
        Description = Lang.Get("CustomActionCastRecastTimeDescription"),
        Category    = ModuleCategory.Action
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };
    
    private delegate int GetAdjustedCastTimeDelegate
    (
        ActionType                  actionType,
        uint                        actionID,
        bool                        applyProc,
        ActionManager.CastTimeProc* outCastTimeProc
    );
    private Hook<GetAdjustedCastTimeDelegate>? GetAdjustedCastTimeHook;

    private delegate int GetAdjustedRecastTimeDelegate
    (
        ActionType actionType,
        uint       actionID,
        bool       applyClassMechanics
    );
    private Hook<GetAdjustedRecastTimeDelegate>? GetAdjustedRecastTimeHook;

    private static readonly CompSig                            CastInfoUpdateTotalSig = new("48 89 5C 24 ?? 57 48 83 EC ?? 48 8B F9 0F 29 74 24 ?? 0F B6 49");
    private delegate        uint                               CastInfoUpdateTotalDelegate(nint data, uint spellActionID, float process, float processTotal);
    private                 Hook<CastInfoUpdateTotalDelegate>? CastInfoUpdateTotalHook;

    private static readonly CompSig CastTimeCurrentSig = new("F3 44 0F 2C C0 BA ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? F3 44 0F 10 1D");
    private                 float*  CastTimeCurrent;

    private Config config = null!;

    private readonly ActionSelectCombo castActionCombo = new("CastActionSelect");
    private readonly JobSelectCombo    castJobCombo    = new("CastJobSelect");

    private readonly ActionSelectCombo recastActionCombo = new("RecastActionSelect");
    private readonly JobSelectCombo    recastJobCombo    = new("RecastJobSelect");

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

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
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("CustomActionCastRecastTime-CustomCastTime")}");

        using (ImRaii.PushId("Cast"))
        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(150f * GlobalUIScale);

            if (ImGui.InputFloat
                (
                    $"{Lang.Get("CustomActionCastRecastTime-DefaultReduction")}(ms)###DefaultReduction",
                    ref config.LongCastTimeReduction,
                    10,
                    100,
                    "%.0f"
                ))
            {
                config.LongCastTimeReduction = MathF.Max(0, config.LongCastTimeReduction);
                config.Save(this);
            }

            ImGuiOm.HelpMarker(Lang.Get("CustomActionCastRecastTime-DefaultReduction-Help", config.LongCastTimeReduction));

            ImGui.NewLine();

            using (ImRaii.Disabled(castActionCombo.SelectedID == 0 || config.CustomCastTimeSet.ContainsKey(castActionCombo.SelectedID)))
            {
                if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, Lang.Get("Add")))
                {
                    if (config.CustomCastTimeSet.TryAdd
                        (
                            castActionCombo.SelectedID,
                            ActionManager.GetAdjustedCastTime(ActionType.Action, castActionCombo.SelectedID)
                        ))
                        config.Save(this);
                }
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(300f * GlobalUIScale);
            castActionCombo.DrawRadio();

            using (ImRaii.Disabled(castJobCombo.SelectedID == 0))
            {
                if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, $"{Lang.Get("Add")}##ClassJob") &&
                    LuminaGetter.TryGetSubRowAll<ClassJobActionUI>(castJobCombo.SelectedID, out var rows))
                {
                    foreach (var actionUI in rows)
                    {
                        if (actionUI.UpgradeAction.RowId == 0 || actionUI.UpgradeAction.Value.Name.IsEmpty) continue;

                        config.CustomCastTimeSet.TryAdd
                        (
                            actionUI.UpgradeAction.RowId,
                            ActionManager.GetAdjustedCastTime(ActionType.Action, actionUI.UpgradeAction.RowId)
                        );
                    }

                    config.Save(this);
                }
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(300f * GlobalUIScale);
            castJobCombo.DrawRadio();

            ImGui.Spacing();

            using (var table = ImRaii.Table("OptimizedLongCastTimeActionCastTable", 3, ImGuiTableFlags.Borders))
            {
                if (table)
                {
                    ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(1340),          ImGuiTableColumnFlags.WidthStretch, 40);
                    ImGui.TableSetupColumn($"{LuminaWrapper.GetAddonText(701)} (ms)", ImGuiTableColumnFlags.WidthStretch, 20);
                    ImGui.TableSetupColumn(Lang.Get("Operation"),                     ImGuiTableColumnFlags.WidthFixed,   2 * ImGui.GetTextLineHeightWithSpacing());

                    ImGui.TableHeadersRow();

                    uint actionToRemove = 0;

                    foreach (var pair in config.CustomCastTimeSet)
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
                            config.CustomCastTimeSet[pair.Key] = MathF.Max(0, customTime);

                        if (ImGui.IsItemDeactivatedAfterEdit())
                            config.Save(this);

                        ImGui.TableNextColumn();
                        if (ImGuiOm.ButtonIcon($"###DeleteCast_{pair.Key}", FontAwesomeIcon.Trash, Lang.Get("Delete")))
                            actionToRemove = pair.Key;
                    }

                    if (actionToRemove != 0)
                    {
                        config.CustomCastTimeSet.Remove(actionToRemove);
                        config.Save(this);
                    }
                }
            }
        }

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("CustomActionCastRecastTime-CustomRecastTime")}");

        using (ImRaii.PushId("Recast"))
        using (ImRaii.PushIndent())
        {
            using (ImRaii.Disabled(recastActionCombo.SelectedID == 0 || config.CustomRecastTimeSet.ContainsKey(recastActionCombo.SelectedID)))
            {
                if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, $"{Lang.Get("Add")}##Action") &&
                    config.CustomRecastTimeSet.TryAdd
                    (
                        recastActionCombo.SelectedID,
                        ActionManager.GetAdjustedRecastTime(ActionType.Action, recastActionCombo.SelectedID)
                    ))
                    config.Save(this);
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(300f * GlobalUIScale);
            recastActionCombo.DrawRadio();

            using (ImRaii.Disabled(recastJobCombo.SelectedID == 0))
            {
                if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, $"{Lang.Get("Add")}##ClassJob") &&
                    LuminaGetter.TryGetSubRowAll<ClassJobActionUI>(recastJobCombo.SelectedID, out var rows))
                {
                    foreach (var actionUI in rows)
                    {
                        if (actionUI.UpgradeAction.RowId == 0 || actionUI.UpgradeAction.Value.Name.IsEmpty) continue;

                        config.CustomRecastTimeSet.TryAdd
                        (
                            actionUI.UpgradeAction.RowId,
                            ActionManager.GetAdjustedRecastTime(ActionType.Action, actionUI.UpgradeAction.RowId)
                        );
                    }

                    config.Save(this);
                }
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(300f * GlobalUIScale);
            recastJobCombo.DrawRadio();

            ImGui.Spacing();

            using (var table = ImRaii.Table("OptimizedLongCastTimeActionRecastTable", 3, ImGuiTableFlags.Borders))
            {
                if (table)
                {
                    ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(1340),          ImGuiTableColumnFlags.WidthStretch, 40);
                    ImGui.TableSetupColumn($"{LuminaWrapper.GetAddonText(702)} (ms)", ImGuiTableColumnFlags.WidthStretch, 20);
                    ImGui.TableSetupColumn(Lang.Get("Operation"),                     ImGuiTableColumnFlags.WidthFixed,   2 * ImGui.GetTextLineHeightWithSpacing());

                    ImGui.TableHeadersRow();

                    var actionToRemove = -1;

                    foreach (var pair in config.CustomRecastTimeSet)
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
                            config.CustomRecastTimeSet[pair.Key] = MathF.Max(0, customTime);

                        if (ImGui.IsItemDeactivatedAfterEdit())
                            config.Save(this);

                        ImGui.TableNextColumn();
                        if (ImGuiOm.ButtonIcon($"###DeleteRecast_{pair.Key}", FontAwesomeIcon.Trash, Lang.Get("Delete")))
                            actionToRemove = (int)pair.Key;
                    }

                    if (actionToRemove != -1)
                    {
                        config.CustomRecastTimeSet.Remove((uint)actionToRemove);
                        config.Save(this);
                    }
                }
            }
        }
    }

    private int GetAdjustedRecastTimeDetour(ActionType actionType, uint actionID, bool applyClassMechanics)
    {
        var orig = GetAdjustedRecastTimeHook.Original(actionType, actionID, applyClassMechanics);
        if (actionType != ActionType.Action) return orig;

        if (config.CustomRecastTimeSet.TryGetValue(actionID, out var customTime))
            return (int)customTime;

        return orig;
    }

    private int GetAdjustedCastTimeDetour(ActionType actionType, uint actionID, bool applyProcess, ActionManager.CastTimeProc* castTimeProc)
    {
        var orig = GetAdjustedCastTimeHook.Original(actionType, actionID, applyProcess, castTimeProc);
        if (actionType != ActionType.Action) return orig;

        if (config.CustomCastTimeSet.TryGetValue(actionID, out var customTime))
            return (int)customTime;

        // 咏唱大于复唱
        var recastTime = ActionManager.GetAdjustedRecastTime(actionType, actionID);
        if (recastTime <= orig)
            return (int)MathF.Max(0, orig - (int)config.LongCastTimeReduction);

        return orig;
    }

    private uint CastInfoUpdateTotalDetour(nint data, uint spellActionID, float processTotal, float processStart)
    {
        var actionID   = *(uint*)((byte*)data + 4);
        var actionType = (ActionType)(*((byte*)data + 2));

        if (actionID == spellActionID && actionType == ActionType.Action)
        {
            if (config.CustomCastTimeSet.TryGetValue(actionID, out var customTime))
            {
                processTotal     = customTime / 1000f;
                *CastTimeCurrent = processTotal;
            }
            else
            {
                var recastTime = ActionManager.GetAdjustedRecastTime(actionType, actionID);

                if (recastTime <= processTotal * 1000)
                {
                    processTotal     = Math.Max(processTotal - config.LongCastTimeReduction / 1000f, 0);
                    *CastTimeCurrent = processTotal;
                }
            }
        }

        return CastInfoUpdateTotalHook.Original(data, spellActionID, processTotal, processStart);
    }

    private class Config : ModuleConfig
    {
        public Dictionary<uint, float> CustomCastTimeSet = [];

        // 复唱
        public Dictionary<uint, float> CustomRecastTimeSet = [];

        // 咏唱
        public float LongCastTimeReduction = 400; // 毫秒
    }
}
