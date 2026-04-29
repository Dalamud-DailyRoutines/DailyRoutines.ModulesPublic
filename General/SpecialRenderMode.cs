using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Interface.Components;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.Interop.Game.Models;

namespace DailyRoutines.ModulesPublic;

public unsafe class SpecialRenderMode : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("SpecialRenderModeTitle"),
        Description = Lang.Get("SpecialRenderModeDescription"),
        Category    = ModuleCategory.General
    };
    
    private delegate void ToggleFadeDelegate(EnvironmentManager* manager, int a2, float fadeDuration, Vector4* fadeColor);
    private readonly ToggleFadeDelegate ToggleFade =
        new CompSig("E8 ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8D 8F ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8D 4C 24").GetDelegate<ToggleFadeDelegate>();

    private Config config = null!;

    protected override void Init() => 
        config = Config.Load(this) ?? new();

    protected override void Uninit() =>
        config?.Save(this);

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("SpecialRenderMode-Mode-DisableWorldRenderButAddons"));

        using (ImRaii.PushId("DisableWorldRenderButAddons"))
        using (ImRaii.PushIndent())
        {
            var color = config.BackgroundColor;

            if (ImGui.Button(Lang.Get("Enable")))
                ToggleFade(Framework.Instance()->EnvironmentManager, 1, 0.1f, &color);

            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("Disable")))
                ToggleFade(Framework.Instance()->EnvironmentManager, 0, 0.1f, &color);

            ImGui.SameLine(0, 8f * GlobalUIScale);
            ImGui.TextUnformatted($"{Lang.Get("Color")}:");

            ImGui.SameLine();
            config.BackgroundColor = ImGuiComponents.ColorPickerWithPalette(1, string.Empty, config.BackgroundColor);
        }

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("SpecialRenderMode-Mode-HideAddonsButNameplate"));

        using (ImRaii.PushId("HideAddonsButNameplate"))
        using (ImRaii.PushIndent())
        {
            if (ImGui.Button(Lang.Get("Enable")))
            {
                UIModule.Instance()->ToggleUi
                (
                    UiFlags.ActionBars | UiFlags.Chat | UiFlags.Hud | UiFlags.TargetInfo | UiFlags.Shortcuts,
                    false
                );
                DTR->IsVisible = false;
            }

            ImGui.SameLine();

            if (ImGui.Button(Lang.Get("Disable")))
            {
                UIModule.Instance()->ToggleUi
                (
                    UiFlags.ActionBars | UiFlags.Chat | UiFlags.Hud | UiFlags.TargetInfo | UiFlags.Shortcuts,
                    true
                );
                DTR->IsVisible = true;
            }
        }

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("SpecialRenderMode-Mode-HideAddonsButChatLog"));

        using (ImRaii.PushId("HideAddonsButChatLog"))
        using (ImRaii.PushIndent())
        {
            if (ImGui.Button(Lang.Get("Enable")))
            {
                UIModule.Instance()->ToggleUi
                (
                    UiFlags.ActionBars | UiFlags.Nameplates | UiFlags.Hud | UiFlags.TargetInfo | UiFlags.Shortcuts,
                    false
                );
                DTR->IsVisible = false;
            }

            ImGui.SameLine();

            if (ImGui.Button(Lang.Get("Disable")))
            {
                UIModule.Instance()->ToggleUi
                (
                    UiFlags.ActionBars | UiFlags.Nameplates | UiFlags.Hud | UiFlags.TargetInfo | UiFlags.Shortcuts,
                    true
                );
                DTR->IsVisible = true;
            }
        }

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("SpecialRenderMode-Mode-HideChatLog"));

        using (ImRaii.PushId("HideChatLog"))
        using (ImRaii.PushIndent())
        {
            if (ImGui.Button(Lang.Get("Enable")))
                UIModule.Instance()->ToggleUi(UiFlags.Chat, false);

            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("Disable")))
                UIModule.Instance()->ToggleUi(UiFlags.Chat, true);
        }

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("SpecialRenderMode-Mode-HideActionBars"));

        using (ImRaii.PushId("HideActionBars"))
        using (ImRaii.PushIndent())
        {
            if (ImGui.Button(Lang.Get("Enable")))
                UIModule.Instance()->ToggleUi(UiFlags.ActionBars, false);

            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("Disable")))
                UIModule.Instance()->ToggleUi(UiFlags.ActionBars, true);
        }

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("SpecialRenderMode-Mode-HideTargetInfo"));

        using (ImRaii.PushId("HideTargetInfo"))
        using (ImRaii.PushIndent())
        {
            if (ImGui.Button(Lang.Get("Enable")))
                UIModule.Instance()->ToggleUi(UiFlags.TargetInfo, false);

            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("Disable")))
                UIModule.Instance()->ToggleUi(UiFlags.TargetInfo, true);
        }

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("SpecialRenderMode-Mode-HideNameplate"));

        using (ImRaii.PushId("HideNameplate"))
        using (ImRaii.PushIndent())
        {
            if (ImGui.Button(Lang.Get("Enable")))
                UIModule.Instance()->ToggleUi(UiFlags.Nameplates, false);

            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("Disable")))
                UIModule.Instance()->ToggleUi(UiFlags.Nameplates, true);
        }
    }
    
    private class Config : ModuleConfig
    {
        public Vector4 BackgroundColor = KnownColor.LightSkyBlue.ToVector4();
    }
}
