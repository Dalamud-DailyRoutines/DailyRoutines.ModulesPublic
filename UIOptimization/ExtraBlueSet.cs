using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using Action = Lumina.Excel.Sheets.Action;

namespace DailyRoutines.ModulesPublic;

public unsafe class ExtraBlueSet : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("ExtraBlueSetTitle"),
        Description = Lang.Get("ExtraBlueSetDescription", COMMAND),
        Category    = ModuleCategory.UIOptimization,
        Author      = ["Marsh"]
    };
    
    private Config config = null!;

    private string newPresetNameInput = string.Empty;

    protected override void Init()
    {
        Overlay ??= new(this);

        config = Config.Load(this) ?? new();

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "AOZNotebook", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "AOZNotebook", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "AOZNotebook", OnAddon);
        if (AOZNotebook->IsAddonAndNodesReady())
            OnAddon(AddonEvent.PostSetup, null);

        CommandManager.Instance().AddSubCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("ExtraBlueSet-CommandHelp") });
    }
    
    protected override void Uninit()
    {
        CommandManager.Instance().RemoveSubCommand(COMMAND);
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
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

        using var font = FontManager.Instance().UIFont80.Push();

        var pos = new Vector2
        (
            addon->GetX() - ImGui.GetWindowSize().X + 20f * GlobalUIScale,
            addon->GetY()                           + 30  * GlobalUIScale
        );
        ImGui.SetWindowPos(pos);

        var origPosY = ImGui.GetCursorPosY();
        ImGui.SetCursorPosY(origPosY + 2f * GlobalUIScale);
        using (FontManager.Instance().UIFont.Push())
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Info.Title);

        ImGui.SameLine(0, 8f * GlobalUIScale);
        ImGui.SetCursorPosY(origPosY - GlobalUIScale);
        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, Lang.Get("Add")))
            ImGui.OpenPopup("NewPresetPopup");

        using (var popup = ImRaii.Popup("NewPresetPopup"))
        {
            if (popup)
            {
                ImGui.InputTextWithHint("###NewPresetNameInput", Lang.Get("Name"), ref newPresetNameInput, 256);

                using (ImRaii.Disabled(string.IsNullOrWhiteSpace(newPresetNameInput)))
                {
                    if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Check, Lang.Get("Confirm")) &&
                        !string.IsNullOrWhiteSpace(newPresetNameInput)                         &&
                        config.Presets.FirstOrDefault(x => x.Name == newPresetNameInput) is null)
                    {
                        var manager = ActionManager.Instance();
                        var actions = new uint[24];

                        for (var i = 0; i < 24; i++)
                            actions[i] = manager->GetActiveBlueMageActionInSlot(i);

                        config.Presets.Add
                        (
                            new()
                            {
                                Name    = newPresetNameInput,
                                Actions = actions
                            }
                        );
                        config.Save(this);

                        ImGui.CloseCurrentPopup();
                        newPresetNameInput = string.Empty;
                    }
                }
            }
        }

        ImGui.Spacing();

        var       nodeState  = resNode->GetNodeState();
        using var presetList = ImRaii.Child("List", nodeState.Size, true);
        if (!presetList) return;

        for (var i = 0; i < config.Presets.Count; i++)
        {
            var preset = config.Presets[i];

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
                    if (!DService.Instance().Texture.TryGetFromGameIcon(new(actionData.Icon), out var actionIcon)) continue;

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
                    if (ImGui.MenuItem($"{Lang.Get("Delete")}"))
                    {
                        config.Presets.RemoveAt(i);
                        config.Save(this);
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
            if (final[i] != 0)
                manager->AssignBlueMageActionToSlot(i, final[i]);

        NotifyHelper.Instance().NotificationSuccess(Lang.Get("ExtraBlueSet-Notification", entry.Name));
    }

    private void OnCommand(string command, string args)
    {
        args = args.Trim();
        if (string.IsNullOrWhiteSpace(args) || config.Presets.FirstOrDefault(x => x.Name == args) is not { } preset) return;

        ApplyCustomPreset(preset);
    }
    
    private class BlueMagePresetEntry
    {
        public string Name    { get; set; } = string.Empty;
        public uint[] Actions { get; set; } = new uint[24];
    }

    private class Config : ModuleConfig
    {
        public List<BlueMagePresetEntry> Presets = [];
    }
    
    #region 常量

    private const string COMMAND = "exblueset";

    #endregion
}
