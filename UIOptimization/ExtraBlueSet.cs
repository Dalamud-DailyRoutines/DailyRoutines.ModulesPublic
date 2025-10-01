using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface;
using FFXIVClientStructs.FFXIV.Client.Game;
using Action = Lumina.Excel.Sheets.Action;

namespace DailyRoutines.ModulesPublic;

public unsafe class ExtraBlueSet : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("ExtraBlueSetTitle"),
        Description = GetLoc("ExtraBlueSetDescription", Command),
        Category    = ModuleCategories.UIOptimization,
        Author      = ["Marsh"]
    };

    private static Config ModuleConfig = null!;

    private const string Command = "exblueset";

    private static string NewPresetNameInput = string.Empty;

    protected override void Init()
    {
        Overlay ??= new(this);

        ModuleConfig = LoadConfig<Config>() ?? new();

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "AOZNotebook", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "AOZNotebook", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "AOZNotebook", OnAddon);
        if (IsAddonAndNodesReady(AOZNotebook))
            OnAddon(AddonEvent.PostSetup, null);

        CommandManager.AddSubCommand(Command, new(OnCommand) { HelpMessage = GetLoc("ExtraBlueSet-CommandHelp") });
    }

    protected override void OverlayUI()
    {
        var addon = AOZNotebook;
        if (addon == null)
        {
            Overlay.IsOpen = false;
            return;
        }

        var resNode = addon->GetNodeById(5);
        if (resNode == null) return;

        using var font = FontManager.UIFont80.Push();
        
        var pos = new Vector2(addon->GetX() - ImGui.GetWindowSize().X + (20f * GlobalFontScale), 
                              addon->GetY()                           + (30 * GlobalFontScale));
        ImGui.SetWindowPos(pos);

        var origPosY = ImGui.GetCursorPosY();
        ImGui.SetCursorPosY(origPosY + (2f * GlobalFontScale));
        using (FontManager.UIFont.Push())
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Info.Title);
        
        ImGui.SameLine(0, 8f * GlobalFontScale);
        ImGui.SetCursorPosY(origPosY - GlobalFontScale);
        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, GetLoc("Add")))
            ImGui.OpenPopup("NewPresetPopup");

        using (var popup = ImRaii.Popup("NewPresetPopup"))
        {
            if (popup)
            {
                ImGui.InputTextWithHint("###NewPresetNameInput", GetLoc("Name"), ref NewPresetNameInput, 256);

                using (ImRaii.Disabled(string.IsNullOrWhiteSpace(NewPresetNameInput)))
                {
                    if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Check, GetLoc("Confirm")) &&
                        !string.IsNullOrWhiteSpace(NewPresetNameInput) &&
                        ModuleConfig.Presets.FirstOrDefault(x => x.Name == NewPresetNameInput) is null)
                    {
                        var manager = ActionManager.Instance();
                        var actions = new uint[24];

                        for (var i = 0; i < 24; i++)
                            actions[i] = manager->GetActiveBlueMageActionInSlot(i);

                        ModuleConfig.Presets.Add(new()
                        {
                            Name    = NewPresetNameInput,
                            Actions = actions
                        });
                        ModuleConfig.Save(this);
                        
                        ImGui.CloseCurrentPopup();
                        NewPresetNameInput = string.Empty;
                    }
                }
            }
        }

        ImGui.Spacing();
        
        var nodeState = NodeState.Get(resNode);
        using var presetList = ImRaii.Child("List", nodeState.Size, true);
        if (!presetList) return;
        
        for (var i = 0; i < ModuleConfig.Presets.Count; i++)
        {
            var preset = ModuleConfig.Presets[i];

            using var id    = ImRaii.PushId(i);
            using var group = ImRaii.Group();

            if (ImGui.Selectable($"{preset.Name}##{i}"))
                ApplyCustomPreset(preset);

            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();

                ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{preset.Name}");
                
                for (var index = 0; index < preset.Actions.Length; index++)
                {
                    var action = preset.Actions[index];
                    if (!LuminaGetter.TryGetRow<Action>(action, out var actionData)) continue;
                    if (!DService.Texture.TryGetFromGameIcon(new(actionData.Icon), out var actionIcon)) continue;

                    if (index != 0)
                        ImGui.SameLine();

                    ImGui.Image(actionIcon.GetWrapOrEmpty().Handle, new(ImGui.GetTextLineHeight()));
                }

                ImGui.EndTooltip();
            }

            using (var context = ImRaii.ContextPopupItem($"Context{i}"))
            {
                if (context)
                {
                    if (ImGui.MenuItem($"{GetLoc("Delete")}"))
                    {
                        ModuleConfig.Presets.RemoveAt(i);
                        ModuleConfig.Save(this);
                    }
                }
            }
        }
    }

    private void OnAddon(AddonEvent type, AddonArgs? args) =>
        Overlay.IsOpen = type switch
        {
            AddonEvent.PostSetup   => true,
            AddonEvent.PreFinalize => false,
            _                      => Overlay.IsOpen
        };

    private static void ApplyCustomPreset(BlueMagePresetEntry entry)
    {
        var preset = entry.Actions;
        if (preset.Length != 24) return;

        var manager = ActionManager.Instance();

        Span<uint> current = stackalloc uint[24];
        Span<uint> final   = stackalloc uint[24];

        for (var i = 0; i < 24; i++)
        {
            current[i] = manager->GetActiveBlueMageActionInSlot(i);
            final[i]   = preset[i];
        }

        for (var i = 0; i < 24; i++)
        {
            if (final[i] == 0) continue;

            for (var j = 0; j < 24; j++)
            {
                if (i == j) continue;
                if (final[i] == current[j])
                {
                    manager->SwapBlueMageActionSlots(i, j);
                    final[i] = 0;
                    break;
                }
            }
        }

        for (var i = 0; i < 24; i++)
        {
            if (final[i] != 0)
                manager->AssignBlueMageActionToSlot(i, final[i]);
        }
        
        NotificationSuccess(GetLoc("ExtraBlueSet-Notification", entry.Name));
    }
    
    private static void OnCommand(string command, string args)
    {
        args = args.Trim();
        if (string.IsNullOrWhiteSpace(args) || ModuleConfig.Presets.FirstOrDefault(x => x.Name == args) is not { } preset) return;
        
        ApplyCustomPreset(preset);
    }

    protected override void Uninit()
    {
        CommandManager.RemoveSubCommand(Command);
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        
        base.Uninit();
    }

    public class BlueMagePresetEntry
    {
        public string Name    { get; set; } = string.Empty;
        public uint[] Actions { get; set; } = new uint[24];
    }

    public class Config : ModuleConfiguration
    {
        public List<BlueMagePresetEntry> Presets { get; set; } = [];
    }
}
