using System;
using System.Collections.Generic;
using System.Numerics;
using DailyRoutines.Abstracts;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using Lumina.Excel.Sheets;
using Action = Lumina.Excel.Sheets.Action;

namespace DailyRoutines.ModulesPublic;

public class CustomActionQueueTime : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("CustomActionQueueTimeTitle"),
        Description = GetLoc("CustomActionQueueTimeDescription"),
        Category    = ModuleCategories.Action
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private static readonly Dictionary<DefaultQueueMode, string> DefaultQueueModesLoc = new()
    {
        [DefaultQueueMode.None]             = GetLoc("CustomActionQueueTime-DefaultQueueMode-None"),
        [DefaultQueueMode.Fixed]            = GetLoc("CustomActionQueueTime-DefaultQueueMode-Fixed"),
        [DefaultQueueMode.BasedOnFrameRate] = GetLoc("CustomActionQueueTime-DefaultQueueMode-BasedOnFrameRate")
    };
    
    private static Config ModuleConfig = null!;

    private static readonly ActionSelectCombo ActionSelectCombo = new("Action");

    private static float QueueTimeMSInput = 500;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        Overlay      = new(this);

        Overlay.Flags |= ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove;
        
        UseActionManager.Instance().RegPreIsActionOffCooldown(OnPreIsActionOffCooldown);

        if (ModuleConfig.DisplayQueueActionOverlay)
            Overlay.IsOpen = true;
    }

    protected override void ConfigUI()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("CustomActionQueueTime-DisplayOverlay")}:");
        
        ImGui.SameLine();
        if (ImGui.Checkbox("###DisplayQueueActionOverlay", ref ModuleConfig.DisplayQueueActionOverlay))
        {
            Overlay.IsOpen = ModuleConfig.DisplayQueueActionOverlay;
            SaveConfig(ModuleConfig);
        }

        if (ModuleConfig.DisplayQueueActionOverlay)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("FontScale")}:");

            for (var i = 0.6f; i < 1.8f; i += 0.2f)
            {
                var fontScale = (float)Math.Round(i, 1);
                
                ImGui.SameLine();
                using (ImRaii.Disabled(ModuleConfig.OverlayFontScale == fontScale))
                {
                    if (ImGui.Button($"{fontScale}"))
                    {
                        ModuleConfig.OverlayFontScale = fontScale;
                        SaveConfig(ModuleConfig);
                    }
                }
            }
            
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("CustomActionQueueTime-UnlockOverlay")}:");

            var isUnlockOverlay = !Overlay.Flags.HasFlag(ImGuiWindowFlags.NoMove);
            ImGui.SameLine();
            if (ImGui.Checkbox("###UnlockOverlay", ref isUnlockOverlay))
            {
                if (Overlay.Flags.HasFlag(ImGuiWindowFlags.NoMove))
                    Overlay.Flags &= ~ImGuiWindowFlags.NoMove;
                else
                    Overlay.Flags |= ImGuiWindowFlags.NoMove;
            }
            
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("CustomActionQueueTime-OverlayTextColor")}:");
            
            ImGui.SameLine();
            if (ImGui.ColorButton(GetLoc("CustomActionQueueTime-OverlayTextColor"), ModuleConfig.OverlayFontColor,
                                  ImGuiColorEditFlags.AlphaPreview))
                ImGui.OpenPopup("OverlayColorPickerPopup");

            using (var popup = ImRaii.Popup("OverlayColorPickerPopup"))
            {
                if (popup)
                {
                    if (ImGui.ColorPicker4(GetLoc("CustomActionQueueTime-OverlayTextColor"), ref ModuleConfig.OverlayFontColor,
                                           ImGuiColorEditFlags.AlphaPreview))
                        ModuleConfig.Save(this);
                }
            }
        }
        
        ImGui.Spacing();
        
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("CustomActionQueueTime-DefaultQueueMode")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        using (var combo = ImRaii.Combo("###DefaultQueueTimeCombo", DefaultQueueModesLoc[ModuleConfig.DefaultQueueMode],
                                        ImGuiComboFlags.HeightLarge))
        {
            if (combo)
            {
                foreach (var defaultQueueMode in Enum.GetValues<DefaultQueueMode>())
                {
                    var loc = DefaultQueueModesLoc[defaultQueueMode];
                    if (ImGui.Selectable(loc, ModuleConfig.DefaultQueueMode == defaultQueueMode))
                    {
                        ModuleConfig.DefaultQueueMode = defaultQueueMode;
                        SaveConfig(ModuleConfig);
                    }
                }
            }
        }
        
        ImGui.SameLine();
        ImGui.TextDisabled("|");
        
        ImGui.SameLine();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("CustomActionQueueTime-DefaultQueueTime")}:");
        
        ImGui.SameLine();
        if (ModuleConfig.DefaultQueueMode == DefaultQueueMode.Fixed)
        {
            ImGui.SetNextItemWidth(150f * GlobalFontScale);
            if (ImGui.InputFloat("(ms)###FixedDefaultQueueTimeInput", ref ModuleConfig.DefaultQueueTime, 0, 0, "%.1f"))
                ModuleConfig.DefaultQueueTime = Math.Max(1, ModuleConfig.DefaultQueueTime);
            if (ImGui.IsItemDeactivatedAfterEdit())
                SaveConfig(ModuleConfig);
        }
        else
            ImGui.TextUnformatted($"{GetDefaultQueueTime():F1} (ms)");
        
        ImGui.Spacing();
        
        ImGui.TextColored(KnownColor.RoyalBlue.ToVector4(), GetLoc("CustomActionQueueTime-CustomDefaultQueueTime"));

        using (ImRaii.Disabled((ActionSelectCombo.SelectedID) == 0 || 
                               ModuleConfig.QueueTime.ContainsKey(ActionSelectCombo.SelectedID)))
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, GetLoc("Add")))
            {
                if (ActionSelectCombo.SelectedID != 0)
                {
                    ModuleConfig.QueueTime.TryAdd(ActionSelectCombo.SelectedID, 500f);
                    SaveConfig(ModuleConfig);
                }
            }
        }
        
        ImGui.SameLine();
        ImGui.SetNextItemWidth(300f * GlobalFontScale);

        ActionSelectCombo.DrawRadio();
        
        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return;

        var       contentRegion = ImGui.GetContentRegionAvail();
        var       tableWidth    = contentRegion.X * 0.75f;
        var       tableSize     = new Vector2(tableWidth, 0);
        using var table         = ImRaii.Table("CustomActionQueueTimeTable", 3, ImGuiTableFlags.Borders, tableSize);
        if (!table) return;
        
        ImGui.TableSetupColumn(LuminaGetter.GetRow<Addon>(1340)?.Text.ToString(), ImGuiTableColumnFlags.WidthStretch, 20);
        ImGui.TableSetupColumn(LuminaGetter.GetRow<Addon>(702)?.Text.ToString(), ImGuiTableColumnFlags.WidthStretch, 10);
        ImGui.TableSetupColumn(GetLoc("CustomActionQueueTime-QueueTime"), ImGuiTableColumnFlags.WidthStretch, 20);

        ImGui.TableHeadersRow();
        
        List<uint> actionsToRemove = [];
        foreach (var queueTimePair in ModuleConfig.QueueTime)
        {
            if (!LuminaGetter.TryGetRow<Action>(queueTimePair.Key, out var data)) continue;

            var icon = DService.Instance().Texture.GetFromGameIcon(new(data.Icon)).GetWrapOrDefault();
            if (icon == null) continue;

            using var id = ImRaii.PushId(data.RowId.ToString());
            
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGuiOm.SelectableImageWithText(icon.Handle, new(ImGui.GetTextLineHeightWithSpacing()),
                                            data.Name.ToString(), false);

            using (var context = ImRaii.ContextPopupItem("ActionContext"))
            {
                if (context)
                {
                    if (ImGui.MenuItem(GetLoc("Delete")))
                        actionsToRemove.Add(data.RowId);
                }
            }

            var recastTimeCurrent = ActionManager.GetAdjustedRecastTime(ActionType.Action, queueTimePair.Key);
            ImGui.TableNextColumn();
            using (ImRaii.PushColor(ImGuiCol.Text, KnownColor.OrangeRed.ToVector4(),
                                    data.ClassJob.RowId != 0 && localPlayer.ClassJob.RowId != data.ClassJob.RowId))
                ImGuiOm.Text($"{recastTimeCurrent} ms");

            var timeInputMS = queueTimePair.Value;
            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(150f * GlobalFontScale);
            if (ImGui.InputFloat("(ms)", ref timeInputMS, 0, 0, "%.1f"))
                timeInputMS = Math.Clamp(timeInputMS, 0, recastTimeCurrent);
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                ModuleConfig.QueueTime[queueTimePair.Key] = timeInputMS;
                SaveConfig(ModuleConfig);
            }
        }

        if (actionsToRemove.Count > 0)
        {
            actionsToRemove.ForEach(x => ModuleConfig.QueueTime.Remove(x));
            SaveConfig(ModuleConfig);
        }
    }

    protected override unsafe void OverlayUI()
    {
        if (!DService.Instance().Condition[ConditionFlag.InCombat] && 
            !DService.Instance().Condition[ConditionFlag.Casting]) return;
        
        var manager = ActionManager.Instance();
        if (manager == null) return;

        using var font  = FontManager.Instance().GetUIFont(ModuleConfig.OverlayFontScale).Push();
        using var color = ImRaii.PushColor(ImGuiCol.Text, ModuleConfig.OverlayFontColor);
        
        var actionID = manager->QueuedActionId;
        var actionType = manager->QueuedActionType;

        using (ImRaii.Group())
        {
            if (actionID == 0)
                ImGui.TextUnformatted($"({GetLoc("CustomActionQueueTime-NoActionInQueue")})");
            else if (actionType != ActionType.Action)
                ImGui.TextUnformatted($"({GetLoc("CustomActionQueueTime-NonePlayerAction")})");
            else
            {
                if (!LuminaGetter.TryGetRow<Action>(actionID, out var data)) return;

                var icon = DService.Instance().Texture.GetFromGameIcon(new(data.Icon)).GetWrapOrDefault();
                if (icon == null) return;
                
                ImGuiOm.TextImage($"{data.Name.ToString()}", icon.Handle, new(ImGui.GetTextLineHeightWithSpacing()));
            }
        }
        
        if (ImGui.IsItemHovered())
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        if (ImGui.IsItemClicked())
        {
            manager->QueuedActionId   = 0;
            manager->QueuedActionType = ActionType.None;
        }
    }

    protected override void Uninit() => 
        UseActionManager.Instance().Unreg(OnPreIsActionOffCooldown);

    private static void OnPreIsActionOffCooldown(
        ref bool isPrevented, ActionType actionType, uint actionID, ref float queueTimeSecond)
    {
        if (actionType != ActionType.Action) return;

        var queueTimeMS =
            ModuleConfig.QueueTime.TryGetValue(actionID, out var queueTime) ? queueTime : GetDefaultQueueTime();
        queueTimeSecond = queueTimeMS / 1000f;
    }

    private static unsafe float GetDefaultQueueTime() =>
        ModuleConfig.DefaultQueueMode switch
        {
            DefaultQueueMode.None  => 500,
            DefaultQueueMode.Fixed => ModuleConfig.DefaultQueueTime,
            DefaultQueueMode.BasedOnFrameRate => Math.Clamp(500 + ((90 - Framework.Instance()->FrameRate) / 5 * 20),
                                                            300, 800),
            _ => 500
        };

    public class Config : ModuleConfiguration
    {
        // Action ID - Time (ms)
        public Dictionary<uint, float> QueueTime                 = [];
        public DefaultQueueMode        DefaultQueueMode          = DefaultQueueMode.None;
        public float                   DefaultQueueTime          = 500f;
        public bool                    DisplayQueueActionOverlay = true;
        public float                   OverlayFontScale          = 1;
        public Vector4                 OverlayFontColor          = new(0, 0, 0, 1);
    }
    
    public enum DefaultQueueMode
    {
        None,
        Fixed,
        BasedOnFrameRate
    }
}
