using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Action = Lumina.Excel.Sheets.Action;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoReplaceActionAnimation : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoReplaceActionAnimationTitle"),
        Description = GetLoc("AutoReplaceActionAnimationDescription"),
        Category    = ModuleCategories.Action
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private static Config ModuleConfig = null!;

    private static ActionSelectCombo InputCombo  = null!;
    private static ActionSelectCombo OutputCombo = null!;

    private static EffectType EffectTypeInput = EffectType.All;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        if (InputCombo == null || OutputCombo == null)
        {
            var source = LuminaGetter.Get<Action>();
            InputCombo  ??= new("Input", source);
            OutputCombo ??= new("Output", source);
        }

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
            ImGui.SetNextItemWidth(300f * GlobalFontScale);
            using (ImRaii.PushId("Input"))
                InputCombo.DrawRadio();

            ImGui.SameLine();
            ImGui.TextUnformatted(GetLoc("Input"));

            ImGui.SetNextItemWidth(300f * GlobalFontScale);
            using (ImRaii.PushId("Output"))
                OutputCombo.DrawRadio();
            
            ImGui.SameLine();
            ImGui.TextUnformatted(GetLoc("Output"));

            ImGui.SetNextItemWidth(300f * GlobalFontScale);
            using (ImRaii.PushId("Output"))
            using (var combo = ImRaii.Combo("###EffectTypeCombo", GetEffectTypeName(EffectTypeInput)))
            {
                if (combo)
                {
                    foreach (var target in Enum.GetValues<EffectType>())
                    {
                        if (ImGui.Selectable(GetEffectTypeName(target), target == EffectTypeInput))
                            EffectTypeInput = target;
                    }
                }
            }
            
            ImGui.SameLine();
            ImGui.TextUnformatted(GetLoc("Range"));
        }

        ImGui.SameLine(0, 10f * GlobalFontScale);
        if (ImGui.Button(GetLoc("Confirm"), new(ImGui.CalcTextSize(GetLoc("Confirm")).X * 2, ImGui.GetItemRectSize().Y)))
        {
            if (InputCombo.SelectedItem.RowId != 0 && OutputCombo.SelectedItem.RowId != 0)
            {
                var actionConfig = new ActionConfig
                {
                    IsEnabled           = true,
                    ReplacementActionID = OutputCombo.SelectedItem.RowId,
                    EffectType          = EffectTypeInput
                };

                ModuleConfig.ActionConfigs[InputCombo.SelectedItem.RowId] = actionConfig;
                ModuleConfig.ActionConfigs = ModuleConfig.ActionConfigs
                                                         .OrderBy(x => LuminaGetter.GetRow<Action>(x.Key)?.ClassJobCategory.ValueNullable?.RowId ?? uint.MaxValue)
                                                         .ThenBy(x => x.Key)
                                                         .ToDictionary(x => x.Key, x => x.Value);
                ModuleConfig.Save(this);
            }
        }

        ImGui.NewLine();

        if (ModuleConfig.ActionConfigs.Count == 0) return;
        
        using var table = ImRaii.Table("###Table", 10, ImGuiTableFlags.None, 
                                       new(ImGui.GetContentRegionAvail().X - (4 * ImGui.GetStyle().ItemSpacing.X), 0));
        if (!table) return;
        
        ImGui.TableSetupColumn("操作 1", ImGuiTableColumnFlags.WidthFixed,   3 * ImGui.GetTextLineHeight());
        ImGui.TableSetupColumn("输入 1", ImGuiTableColumnFlags.WidthStretch, 20);
        ImGui.TableSetupColumn("箭头 1", ImGuiTableColumnFlags.WidthFixed,   ImGui.CalcTextSize("→").X * 3);
        ImGui.TableSetupColumn("输出 1", ImGuiTableColumnFlags.WidthStretch, 20);
        ImGui.TableSetupColumn("范围 1", ImGuiTableColumnFlags.WidthFixed,   ImGui.CalcTextSize($"[{GetLoc("All")}]").X * 1.5f);
        
        ImGui.TableSetupColumn("操作 2", ImGuiTableColumnFlags.WidthFixed,   3 * ImGui.GetTextLineHeight());
        ImGui.TableSetupColumn("输入 2", ImGuiTableColumnFlags.WidthStretch, 20);
        ImGui.TableSetupColumn("箭头 2", ImGuiTableColumnFlags.WidthFixed,   ImGui.CalcTextSize("→").X * 3);
        ImGui.TableSetupColumn("输出 2", ImGuiTableColumnFlags.WidthStretch, 20);
        ImGui.TableSetupColumn("范围 2", ImGuiTableColumnFlags.WidthFixed,   ImGui.CalcTextSize($"[{GetLoc("All")}]").X * 1.5f);
        
        var counter = 0;
        foreach (var (input, config) in ModuleConfig.ActionConfigs)
        {
            var output    = config.ReplacementActionID;
            var isEnabled = config.IsEnabled;
            
            if (counter % 2 == 0)
                ImGui.TableNextRow();
            counter++;
            
            using var id    = ImRaii.PushId($"{input}_{output}");
            using var group = ImRaii.Group();

            ImGui.TableNextColumn();
            if (ImGui.Checkbox("##Enabled", ref isEnabled))
            {
                config.IsEnabled = isEnabled;
                ModuleConfig.Save(this);
            }

            ImGui.SameLine();

            if (ImGui.Button(FontAwesomeIcon.TrashAlt.ToIconString()))
            {
                ModuleConfig.ActionConfigs.Remove(input);
                ModuleConfig.Save(this);
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
                InputCombo.SelectedID = input;
            
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
                OutputCombo.SelectedID = output;

            ImGui.TableNextColumn();
            ImGui.TextColored(KnownColor.Gray.ToVector4(), $"[{GetEffectTypeName(config.EffectType)}]");
            
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
                        var isSelected = config.EffectType == target;
                        if (ImGui.Selectable(GetEffectTypeName(target), isSelected))
                        {
                            config.EffectType = target;
                            ModuleConfig.Save(this);
                        }
                    }
                }
            }
        }

        return;

        static string GetEffectTypeName(EffectType target) => target switch
        {
            EffectType.All    => GetLoc("All"),
            EffectType.Self   => GetLoc("AutoReplaceActionAnimation-EffectType-Self"),
            EffectType.Others => GetLoc("AutoReplaceActionAnimation-EffectType-Others"),
            _                 => string.Empty
        };
    }

    private static void OnCharacterStartCast(
        ref bool         isPrevented,
        ref IBattleChara player,
        ref ActionType   type,
        ref uint         actionID,
        ref nint         a4,
        ref float        rotation,
        ref float        a6)
    {
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return;

        var isSelf = player.Address == (nint)localPlayer;

        if (type != ActionType.Action                                         ||
            !ModuleConfig.ActionConfigs.TryGetValue(actionID, out var config) ||
            !config.IsEnabled)
            return;

        var shouldReplace = config.EffectType switch
        {
            EffectType.All    => true,
            EffectType.Self   => isSelf,
            EffectType.Others => !isSelf,
            _                 => false
        };
        if (!shouldReplace) return;

        actionID = config.ReplacementActionID;
    }

    private static void OnCharacterCompleteCast(
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
        ref int          ballistaEntityID)
    {
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return;

        var isSelf = player.Address == (nint)localPlayer;

        if (type != ActionType.Action                                         ||
            !ModuleConfig.ActionConfigs.TryGetValue(actionID, out var config) ||
            !config.IsEnabled                                                 ||
            !LuminaGetter.TryGetRow<Action>(config.ReplacementActionID, out _))
            return;

        var shouldReplace = config.EffectType switch
        {
            EffectType.All    => true,
            EffectType.Self   => isSelf,
            EffectType.Others => !isSelf,
            _                 => false
        };
        if (!shouldReplace) return;

        if (isSelf && TargetManager.Target is { } target &&
            ActionManager.CanUseActionOnTarget(config.ReplacementActionID, target.ToStruct()))
            animationTargetID = target.GameObjectID;

        actionID = spellID = config.ReplacementActionID;
    }

    private class Config : ModuleConfiguration
    {
        public Dictionary<uint, ActionConfig> ActionConfigs = [];
    }

    public enum EffectType
    {
        All,   // 所有目标
        Self,  // 仅自身
        Others // 仅他人
    }

    public class ActionConfig
    {
        public bool       IsEnabled           { get; set; } = true;
        public uint       ReplacementActionID { get; set; }
        public EffectType EffectType          { get; set; } = EffectType.All;
    }
}
