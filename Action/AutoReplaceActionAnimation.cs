using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using Action = Lumina.Excel.Sheets.Action;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoReplaceActionAnimation : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoReplaceActionAnimationTitle"),
        Description = Lang.Get("AutoReplaceActionAnimationDescription"),
        Category    = ModuleCategory.Action
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private Config config = null!;

    private readonly ActionSelectCombo inputCombo  = new("Input");
    private readonly ActionSelectCombo outputCombo = new("Output");

    private EffectType effectTypeInput = EffectType.All;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        UseActionManager.Instance().RegPreCharacterStartCast(OnCharacterStartCast);
        UseActionManager.Instance().RegPreCharacterCompleteCast(OnCharacterCompleteCast);
    }

    protected override void Uninit()
    {
        UseActionManager.Instance().Unreg(OnCharacterStartCast);
        UseActionManager.Instance().Unreg(OnCharacterCompleteCast);
    }

    protected override void ConfigUI()
    {
        using (ImRaii.Group())
        {
            ImGui.SetNextItemWidth(300f * GlobalUIScale);
            using (ImRaii.PushId("Input"))
                inputCombo.DrawRadio();

            ImGui.SameLine();
            ImGui.TextUnformatted(Lang.Get("Input"));

            ImGui.SetNextItemWidth(300f * GlobalUIScale);
            using (ImRaii.PushId("Output"))
                outputCombo.DrawRadio();

            ImGui.SameLine();
            ImGui.TextUnformatted(Lang.Get("Output"));

            ImGui.SetNextItemWidth(300f * GlobalUIScale);

            using (ImRaii.PushId("Output"))
            using (var combo = ImRaii.Combo("###EffectTypeCombo", GetEffectTypeName(effectTypeInput)))
            {
                if (combo)
                {
                    foreach (var target in Enum.GetValues<EffectType>())
                    {
                        if (ImGui.Selectable(GetEffectTypeName(target), target == effectTypeInput))
                            effectTypeInput = target;
                    }
                }
            }

            ImGui.SameLine();
            ImGui.TextUnformatted(Lang.Get("Range"));
        }

        ImGui.SameLine(0, 10f * GlobalUIScale);

        if (ImGui.Button(Lang.Get("Confirm"), new(ImGui.CalcTextSize(Lang.Get("Confirm")).X * 2, ImGui.GetItemRectSize().Y)))
        {
            if (inputCombo.SelectedItem.RowId != 0 && outputCombo.SelectedItem.RowId != 0)
            {
                var actionConfig = new ActionConfig
                {
                    IsEnabled           = true,
                    ReplacementActionID = outputCombo.SelectedItem.RowId,
                    EffectType          = effectTypeInput
                };

                config.ActionConfigs[inputCombo.SelectedItem.RowId] = actionConfig;
                config.ActionConfigs = config.ActionConfigs
                                                         .OrderBy(x => LuminaGetter.GetRow<Action>(x.Key)?.ClassJobCategory.ValueNullable?.RowId ?? uint.MaxValue)
                                                         .ThenBy(x => x.Key)
                                                         .ToDictionary(x => x.Key, x => x.Value);
                config.Save(this);
            }
        }

        ImGui.NewLine();

        if (config.ActionConfigs.Count == 0) return;

        using var table = ImRaii.Table
        (
            "###Table",
            10,
            ImGuiTableFlags.None,
            new(ImGui.GetContentRegionAvail().X - 4 * ImGui.GetStyle().ItemSpacing.X, 0)
        );
        if (!table) return;

        ImGui.TableSetupColumn("操作 1", ImGuiTableColumnFlags.WidthFixed,   3 * ImGui.GetTextLineHeight());
        ImGui.TableSetupColumn("输入 1", ImGuiTableColumnFlags.WidthStretch, 20);
        ImGui.TableSetupColumn("箭头 1", ImGuiTableColumnFlags.WidthFixed,   ImGui.CalcTextSize("→").X * 3);
        ImGui.TableSetupColumn("输出 1", ImGuiTableColumnFlags.WidthStretch, 20);
        ImGui.TableSetupColumn("范围 1", ImGuiTableColumnFlags.WidthFixed,   ImGui.CalcTextSize($"[{Lang.Get("All")}]").X * 1.5f);

        ImGui.TableSetupColumn("操作 2", ImGuiTableColumnFlags.WidthFixed,   3 * ImGui.GetTextLineHeight());
        ImGui.TableSetupColumn("输入 2", ImGuiTableColumnFlags.WidthStretch, 20);
        ImGui.TableSetupColumn("箭头 2", ImGuiTableColumnFlags.WidthFixed,   ImGui.CalcTextSize("→").X * 3);
        ImGui.TableSetupColumn("输出 2", ImGuiTableColumnFlags.WidthStretch, 20);
        ImGui.TableSetupColumn("范围 2", ImGuiTableColumnFlags.WidthFixed,   ImGui.CalcTextSize($"[{Lang.Get("All")}]").X * 1.5f);

        var counter = 0;

        foreach (var (input, actionConfig) in config.ActionConfigs)
        {
            var output    = actionConfig.ReplacementActionID;
            var isEnabled = actionConfig.IsEnabled;

            if (counter % 2 == 0)
                ImGui.TableNextRow();
            counter++;

            using var id    = ImRaii.PushId($"{input}_{output}");
            using var group = ImRaii.Group();

            ImGui.TableNextColumn();

            if (ImGui.Checkbox("##Enabled", ref isEnabled))
            {
                actionConfig.IsEnabled = isEnabled;
                this.config.Save(this);
            }

            ImGui.SameLine();

            if (ImGui.Button(FontAwesomeIcon.TrashAlt.ToIconString()))
            {
                this.config.ActionConfigs.Remove(input);
                this.config.Save(this);
                continue;
            }

            ImGui.TableNextColumn();

            using (ImRaii.Group())
            {
                var inputIcon = ImageHelper.GetGameIcon(LuminaGetter.GetRow<Action>(input)!.Value.Icon);

                if (inputIcon != null)
                {
                    ImGui.Image(inputIcon.Handle, ScaledVector2(24f));

                    ImGui.SameLine();
                }

                ImGui.TextUnformatted(LuminaWrapper.GetActionName(input));
            }

            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsItemClicked())
                inputCombo.SelectedID = input;

            ImGui.TableNextColumn();
            ImGui.TextUnformatted("→");

            ImGui.TableNextColumn();

            using (ImRaii.Group())
            {
                var outputIcon = ImageHelper.GetGameIcon(LuminaGetter.GetRow<Action>(output)!.Value.Icon);

                if (outputIcon != null)
                {
                    ImGui.Image(outputIcon.Handle, ScaledVector2(24f));

                    ImGui.SameLine();
                }

                ImGui.TextUnformatted(LuminaWrapper.GetActionName(output));
            }

            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsItemClicked())
                outputCombo.SelectedID = output;

            ImGui.TableNextColumn();
            ImGui.TextColored(KnownColor.Gray.ToVector4(), $"[{GetEffectTypeName(actionConfig.EffectType)}]");

            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsItemClicked())
                ImGui.OpenPopup($"ActionTargetPopup_{input}");

            using (var popupModify = ImRaii.Popup($"ActionTargetPopup_{input}"))
            {
                if (popupModify)
                {
                    foreach (var target in Enum.GetValues<EffectType>())
                    {
                        var isSelected = actionConfig.EffectType == target;

                        if (ImGui.Selectable(GetEffectTypeName(target), isSelected))
                        {
                            actionConfig.EffectType = target;
                            this.config.Save(this);
                        }
                    }
                }
            }
        }

        return;

        static string GetEffectTypeName(EffectType target)
        {
            return target switch
            {
                EffectType.All    => Lang.Get("All"),
                EffectType.Self   => Lang.Get("AutoReplaceActionAnimation-EffectType-Self"),
                EffectType.Others => Lang.Get("AutoReplaceActionAnimation-EffectType-Others"),
                _                 => string.Empty
            };
        }
    }

    private void OnCharacterStartCast
    (
        ref bool         isPrevented,
        ref IBattleChara player,
        ref ActionType   type,
        ref uint         actionID,
        ref nint         a4,
        ref float        rotation,
        ref float        a6
    )
    {
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return;

        var isSelf = player.Address == (nint)localPlayer;

        if (type != ActionType.Action                                              ||
            !this.config.ActionConfigs.TryGetValue(actionID, out var actionConfig) ||
            !actionConfig.IsEnabled)
            return;

        var shouldReplace = actionConfig.EffectType switch
        {
            EffectType.All    => true,
            EffectType.Self   => isSelf,
            EffectType.Others => !isSelf,
            _                 => false
        };
        if (!shouldReplace) return;

        actionID = actionConfig.ReplacementActionID;
    }

    private void OnCharacterCompleteCast
    (
        ref bool         isPrevented,
        ref IBattleChara player,
        ref ActionType   type,
        ref uint         actionID,
        ref uint         spellID,
        ref GameObjectId animationTargetID,
        ref Vector3      location,
        ref float        rotation,
        ref short        lastUsedActionSequence,
        ref int          animationVariation,
        ref int          ballistaEntityID
    )
    {
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return;

        var isSelf = player.Address == (nint)localPlayer;

        if (type != ActionType.Action                                              ||
            !this.config.ActionConfigs.TryGetValue(actionID, out var actionConfig) ||
            !actionConfig.IsEnabled                                                ||
            !LuminaGetter.TryGetRow<Action>(actionConfig.ReplacementActionID, out _))
            return;

        var shouldReplace = actionConfig.EffectType switch
        {
            EffectType.All    => true,
            EffectType.Self   => isSelf,
            EffectType.Others => !isSelf,
            _                 => false
        };
        if (!shouldReplace) return;

        if (isSelf                             &&
            TargetManager.Target is { } target &&
            ActionManager.CanUseActionOnTarget(actionConfig.ReplacementActionID, target.ToStruct()))
            animationTargetID = target.GameObjectID;

        actionID = spellID = actionConfig.ReplacementActionID;
    }

    private class Config : ModuleConfig
    {
        public Dictionary<uint, ActionConfig> ActionConfigs = [];
    }

    private class ActionConfig
    {
        public bool       IsEnabled           { get; set; } = true;
        public uint       ReplacementActionID { get; set; }
        public EffectType EffectType          { get; set; } = EffectType.All;
    }
    
    private enum EffectType
    {
        All,   // 所有目标
        Self,  // 仅自身
        Others // 仅他人
    }
}
