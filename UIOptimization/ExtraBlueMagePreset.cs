using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
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
    public List<BlueMagePresetEntry> Presets { get; set; } = [];
    public string NewPresetName = string.Empty;
    public int? RenameIndex;
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
        Overlay ??= new Overlay(this);
        Overlay.Flags |=  ImGuiWindowFlags.AlwaysAutoResize;
        Config = LoadConfig<BlueMagePresetConfig>();

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,  "AOZNotebook", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,   "AOZNotebook", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize,"AOZNotebook", OnAddon);
        
        if (IsAddonAndNodesReady(AOZNotebook))
            OnAddon(AddonEvent.PostSetup, null);
    }

    public override void OverlayUI()
    {
        var addon = AOZNotebook;
        if (!IsAddonAndNodesReady(addon)) return;
        
        var resNode = addon->GetNodeById(5);
        if (resNode == null) return;

        var nodeState = NodeState.Get(resNode);
        var overlayWidth = nodeState.Size.X;
        var overlayHeight = nodeState.Size.Y;
        var pos = new Vector2(addon->GetX() - ImGui.GetWindowSize().X - 10 , addon->GetY() + 20);
        ImGui.SetWindowPos(pos);
        ImGui.SetWindowSize(new Vector2(overlayWidth, overlayHeight*2));
        
        ImGui.TextColored(LightSkyBlue, GetLoc("ExtraBlueMagePreset-BlueMagePreset")); // 青魔法预设
        ImGui.Separator();
        using (var presetList = ImRaii.Child("list", new Vector2(overlayWidth - 1, overlayHeight), true))
        {
            if (!presetList) return;

            for (var i = 0; i < Config.Presets.Count; i++)
            {
                var preset = Config.Presets[i];
                using var id = ImRaii.PushId(i);
                using var group = ImRaii.Group();

                var nameFieldWidth = ImGui.GetContentRegionAvail().X - 88f;

                ImGui.SetNextItemWidth(50f);
                if (ImGui.Button(GetLoc("Apply") + $"##{i}"))
                    ApplyCustomPreset(preset.Actions);

                ImGui.SameLine();
                
                ImGui.SetNextItemWidth(nameFieldWidth);
                if (Config.RenameIndex == i)
                {
                    var nameBuffer = preset.Name;
                    if (ImGui.InputText($"##rename{i}", ref nameBuffer, 64, ImGuiInputTextFlags.EnterReturnsTrue))
                    {
                        Config.Presets[i].Name = nameBuffer;
                        Config.RenameIndex = null;
                        Config.Save(this);
                        break;
                    }
                }
                else
                {
                    ImGui.Selectable(preset.Name, false, ImGuiSelectableFlags.AllowDoubleClick, new Vector2(nameFieldWidth, 0));
                    if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                        Config.RenameIndex = i;
                }

                ImGui.SameLine();
                using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(2, 2)))
                using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 4))
                {
                    if (ImGui.Button($"\uf1f8##{i}", new Vector2(30, 28)))
                    {
                        Config.Presets.RemoveAt(i);
                        Config.Save(this);
                        NotificationInfo(GetLoc("ExtraBlueMagePreset-PresetDeleted") + $":{preset.Name}");// 清空全部预设
                        break;
                    }
                }

                ImGui.Spacing();
            }
        }
        
        ImGui.SetNextItemWidth(overlayWidth);
        ImGui.InputTextWithHint("##NewPreset", GetLoc("ExtraBlueMagePreset-NewPresetName"), ref Config.NewPresetName, 64); // 请输入新预设的名称
        if (ImGui.Button(GetLoc("ExtraBlueMagePreset-SaveCurrentAsNewPreset")) && !string.IsNullOrWhiteSpace(Config.NewPresetName)) // 保存当前为新预设
        {
            SaveCurrentPreset(Config.NewPresetName);
            Config.NewPresetName = string.Empty;
        }

        if (ImGui.Button(GetLoc("ExtraBlueMagePreset-ClearAllPresets"))) // 清空全部预设
        {
            Config.Presets.Clear();
            Config.Save(this);
            NotificationInfo(GetLoc("ExtraBlueMagePreset-AllPresetsCleared")); // 已清空所有预设
        }
    }

    private void OnAddon(AddonEvent type, AddonArgs? args)
    {
        Overlay!.IsOpen = type switch
        {
            AddonEvent.PostSetup   => true,
            AddonEvent.PreFinalize => false,
            _                      => Overlay.IsOpen
        };
    }

    private void SaveCurrentPreset(string name)
    {
        var actionManager = ActionManager.Instance();
        var actions = new uint[24];

        for (var i = 0; i < 24; i++)
            actions[i] = actionManager->GetActiveBlueMageActionInSlot(i);

        Config.Presets.Add(new BlueMagePresetEntry
        {
            Name = name,
            Actions = actions
        });
        Config.Save(this);

        NotificationSuccess(GetLoc("ExtraBlueMagePreset-PresetSaved") + $":{name}"); // 已保存当前技能配置为预设：
    }

    private void ApplyCustomPreset(uint[] preset)
    {
        if (preset.Length != 24)
        {
            NotificationError(GetLoc("ExtraBlueMagePreset-InvalidPresetData")); // 预设数据不正确
            return;
        }

        var actionManager = ActionManager.Instance();

        Span<uint> current = stackalloc uint[24];
        Span<uint> final   = stackalloc uint[24];

        for (var i = 0; i < 24; i++)
        {
            current[i] = actionManager->GetActiveBlueMageActionInSlot(i);
            final[i]   = preset[i];
        }

        for (var i = 0; i < 24; i++)
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

        NotificationSuccess(GetLoc("ExtraBlueMagePreset-PresetApplied")); // 已应用预设
    }

    public override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        base.Uninit();
    }
}
