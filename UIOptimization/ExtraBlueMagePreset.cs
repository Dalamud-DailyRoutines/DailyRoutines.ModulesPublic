global using static DailyRoutines.Infos.Widgets;
global using static OmenTools.Helpers.HelpersOm;
global using static DailyRoutines.Infos.Extensions;
global using static OmenTools.Infos.InfosOm;
global using static OmenTools.Helpers.ThrottlerHelper;
global using static DailyRoutines.Managers.Configuration;
global using static DailyRoutines.Managers.LanguageManagerExtensions;
global using static DailyRoutines.Helpers.NotifyHelper;
global using static OmenTools.Helpers.ContentsFinderHelper;
global using Dalamud.Interface.Utility.Raii;
global using OmenTools.Infos;
global using OmenTools.ImGuiOm;
global using OmenTools.Helpers;
global using OmenTools;
global using ImGuiNET;
global using ImPlotNET;
global using Dalamud.Game;
using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using DailyRoutines.Abstracts;
using DailyRoutines.Windows;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DailyRoutines.ModulesPublic;

public class BlueMagePresetEntry
{
    public string Name { get; set; } = string.Empty;
    public uint[] Actions { get; set; } = new uint[24];
}

public class BlueMagePresetConfig : ModuleConfiguration
{
    public List<BlueMagePresetEntry> Presets { get; set; } = new();
    public string NewPresetName = string.Empty;
    public int? RenameIndex = null;
}

public unsafe class ExtraBlueMagePreset : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("ExtraBlueMagePreset"), //额外的青魔法预设
        Description = GetLoc("ExtraBlueMagePresetDescription"), //保存更多的青魔法技能预设
        Category    = ModuleCategories.UIOptimization,
        Author      = ["Marsh"]
    };

    private new Overlay? Overlay;
    private BlueMagePresetConfig Config = null!;

    public override void Init()
    {
        Overlay = new Overlay(this);
        Config = LoadConfig<BlueMagePresetConfig>();

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "AOZNotebook", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "AOZNotebook", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "AOZNotebook", OnAddon);

        var addon = GetAddonByName("AOZNotebook");
        if (IsAddonAndNodesReady(addon))
            OnAddon(AddonEvent.PostSetup, null);
    }

    public override void OverlayUI()
    {
        var addon = GetAddonByName("AOZNotebook");
        if (addon == null || !addon->IsVisible) return;

        var posX = Math.Max(addon->X - 320, 20);
        var posY = Math.Max(addon->Y + 40, 20);
        ImGui.SetNextWindowPos(new Vector2(posX, posY), ImGuiCond.Always);
        ImGui.SetNextWindowSizeConstraints(new Vector2(320, 100), new Vector2(320, 600));

        if (ImGui.Begin(GetLoc("ExtraBlueMagePreset"), ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextColored(new Vector4(0.3f, 0.7f, 1.0f, 1.0f), GetLoc("BlueMagePresets")); // 自定义技能预设
            ImGui.Separator();

            float listMaxHeight = 400f;
            if (ImGui.BeginChild("##presetList", new Vector2(0, listMaxHeight), true))
            {
                for (int i = 0; i < Config.Presets.Count; i++)
                {
                    var preset = Config.Presets[i];
                    ImGui.PushID(i);
                    ImGui.BeginGroup();

                    float totalWidth = ImGui.GetContentRegionAvail().X;
                    float applyBtnWidth = 50f;
                    float deleteBtnWidth = 30f;
                    float spacing = 4f;
                    float nameFieldWidth = totalWidth - applyBtnWidth - deleteBtnWidth - spacing * 2;

                    ImGui.PushItemWidth(applyBtnWidth);
                    if (ImGui.Button(GetLoc("Apply") + $"##{i}")) // 应用
                        ApplyCustomPreset(preset.Actions);
                    ImGui.PopItemWidth();

                    ImGui.SameLine();
                    ImGui.PushItemWidth(nameFieldWidth);
                    if (Config.RenameIndex == i)
                    {
                        string nameBuffer = preset.Name;
                        if (ImGui.InputText($"##rename{i}", ref nameBuffer, 64, ImGuiInputTextFlags.EnterReturnsTrue))
                        {
                            Config.Presets[i].Name = nameBuffer;
                            Config.RenameIndex = null;
                            Config.Save(this);
                        }
                    }
                    else
                    {
                        if (ImGui.Selectable(preset.Name, false, ImGuiSelectableFlags.AllowDoubleClick, new Vector2(nameFieldWidth, 0)))
                        {
                            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                                Config.RenameIndex = i;
                        }
                    }
                    ImGui.PopItemWidth();

                    ImGui.SameLine();
                    ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(2, 2));
                    ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4);
                    if (ImGui.Button($"\uf1f8##{i}", new Vector2(deleteBtnWidth, 28)))
                    {
                        Config.Presets.RemoveAt(i);
                        Config.Save(this);
                        NotificationInfo(GetLoc("PresetDeleted") + $":{preset.Name}"); // 已删除预设：
                    }
                    ImGui.PopStyleVar(2);

                    ImGui.EndGroup();
                    ImGui.Spacing();
                    ImGui.PopID();
                }
            }
            ImGui.EndChild();

            ImGui.InputTextWithHint(GetLoc("NewPresetLabel"), GetLoc("NewPresetPlaceholder"), ref Config.NewPresetName, 64); // 新预设名 / 请输入新预设的名称
            if (ImGui.Button(GetLoc("SaveCurrentAsNewPreset")) && !string.IsNullOrWhiteSpace(Config.NewPresetName)) // 保存当前为新预设
            {
                SaveCurrentPreset(Config.NewPresetName);
                Config.NewPresetName = string.Empty;
            }

            if (ImGui.Button(GetLoc("ClearAllPresets"))) // 清空全部预设
            {
                Config.Presets.Clear();
                Config.Save(this);
                NotificationInfo(GetLoc("AllPresetsCleared")); // 已清空所有预设
            }
        }
        ImGui.End();
    }

    private void OnAddon(AddonEvent type, AddonArgs? args)
    {
        var addon = GetAddonByName("AOZNotebook");
        if (addon == null) return;

        Overlay!.IsOpen = type switch
        {
            AddonEvent.PostSetup   => true,
            AddonEvent.PreFinalize => false,
            _                      => Overlay.IsOpen
        };

        if (type == AddonEvent.PostDraw && Throttler.Throttle("BlueMagePresetOverlayCheck"))
        {
            if (addon->AtkValues != null && addon->AtkValues->Int >= 9)
                Overlay.IsOpen = false;
            else
                Overlay.IsOpen = true;
        }
    }

    private void SaveCurrentPreset(string name)
    {
        var actionManager = ActionManager.Instance();
        uint[] actions = new uint[24];

        for (int i = 0; i < 24; i++)
            actions[i] = actionManager->GetActiveBlueMageActionInSlot(i);

        Config.Presets.Add(new BlueMagePresetEntry
        {
            Name = name,
            Actions = actions
        });
        Config.Save(this);

        NotificationSuccess(GetLoc("PresetSaved") + $":{name}"); // 已保存当前技能配置为预设：
    }

    private void ApplyCustomPreset(uint[] preset)
    {
        if (preset.Length != 24)
        {
            NotificationError(GetLoc("InvalidPresetData")); // 预设数据不正确
            return;
        }

        var actionManager = ActionManager.Instance();

        Span<uint> current = stackalloc uint[24];
        Span<uint> final   = stackalloc uint[24];

        for (int i = 0; i < 24; i++)
        {
            current[i] = actionManager->GetActiveBlueMageActionInSlot(i);
            final[i]   = preset[i];
        }

        for (int i = 0; i < 24; i++)
        {
            if (final[i] == 0) continue;

            for (int j = 0; j < 24; j++)
            {
                if (i == j) continue;
                if (final[i] == current[j])
                {
                    actionManager->SwapBlueMageActionSlots(i, j);
                    final[i] = 0;
                    break;
                }
            }
        }

        for (int i = 0; i < 24; i++)
        {
            if (final[i] != 0)
                actionManager->AssignBlueMageActionToSlot(i, final[i]);
        }

        NotificationSuccess(GetLoc("PresetApplied")); // 已应用预设
    }

    public override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        base.Uninit();
    }
}
