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

internal unsafe class SpecialRenderMode : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("SpecialRenderModeTitle"),
        Description = Lang.Get("SpecialRenderModeDescription"),
        Category    = ModuleCategory.System
    };

    public override string ModuleName => nameof(SpecialRenderMode);

    private static readonly CompSig ToggleFadeSig =
        new("E8 ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8D 8F ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8D 4C 24");

    private delegate void ToggleFadeDelegate
    (
        EnvironmentManager* manager,
        int                 a2,
        float               fadeDuration,
        Vector4*            fadeColor
    );

    private ToggleFadeDelegate ToggleFade = null!;

    private Config config = null!;

    protected override void Init()
    {
        ToggleFade = ToggleFadeSig.GetDelegate<ToggleFadeDelegate>();
        config     = Config.Load(this) ?? new();
    }

    protected override void Uninit() =>
        config?.Save(this);

    protected override void ConfigUI()
    {
        var enableText  = Lang.Get("Enable");
        var disableText = Lang.Get("Disable");
        var buttonWidth = MathF.Max(ImGui.CalcTextSize(enableText).X, ImGui.CalcTextSize(disableText).X) +
                          (ImGui.GetStyle().FramePadding.X * 2);

        using var table = ImRaii.Table("RenderModes", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH);

        if (table)
        {
            ImGui.TableSetupColumn("Mode",     ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Settings", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());
            ImGui.TableSetupColumn("Enable",   ImGuiTableColumnFlags.WidthFixed, buttonWidth);
            ImGui.TableSetupColumn("Disable",  ImGuiTableColumnFlags.WidthFixed, buttonWidth);

            if (DrawModeRow("DisableWorldRenderButAddons", enableText, disableText, out var worldRenderDisabled))
            {
                var color = config.BackgroundColor;
                ToggleFade
                (
                    Framework.Instance()->EnvironmentManager,
                    worldRenderDisabled ?
                        1 :
                        0,
                    0.1f,
                    &color
                );
            }

            if (DrawModeRow("HideTitleExtraInfos", enableText, disableText, out var titleInfosHidden))
            {
                if (TitleRevision != null)
                    TitleRevision->IsVisible = !titleInfosHidden;

                if (TitleMenu != null)
                    TitleMenu->IsVisible = !titleInfosHidden;
            }

            if (DrawModeRow("HideAddonsButNameplate", enableText, disableText, out var addonsButNameplateHidden))
            {
                UIModule.Instance()->ToggleUi
                (
                    UiFlags.ActionBars | UiFlags.Chat | UiFlags.Hud | UiFlags.TargetInfo | UiFlags.Shortcuts,
                    !addonsButNameplateHidden
                );
                DTR->IsVisible = !addonsButNameplateHidden;
            }

            if (DrawModeRow("HideAddonsButChatLog", enableText, disableText, out var addonsButChatLogHidden))
            {
                UIModule.Instance()->ToggleUi
                (
                    UiFlags.ActionBars | UiFlags.Nameplates | UiFlags.Hud | UiFlags.TargetInfo | UiFlags.Shortcuts,
                    !addonsButChatLogHidden
                );
                DTR->IsVisible = !addonsButChatLogHidden;
            }

            if (DrawModeRow("HideChatLog", enableText, disableText, out var chatLogHidden))
                UIModule.Instance()->ToggleUi(UiFlags.Chat, !chatLogHidden);

            if (DrawModeRow("HideActionBars", enableText, disableText, out var actionBarsHidden))
                UIModule.Instance()->ToggleUi(UiFlags.ActionBars, !actionBarsHidden);

            if (DrawModeRow("HideTargetInfo", enableText, disableText, out var targetInfoHidden))
                UIModule.Instance()->ToggleUi(UiFlags.TargetInfo, !targetInfoHidden);

            if (DrawModeRow("HideNameplate", enableText, disableText, out var nameplateHidden))
                UIModule.Instance()->ToggleUi(UiFlags.Nameplates, !nameplateHidden);
        }

    }

    private bool DrawModeRow(string mode, string enableText, string disableText, out bool enabled)
    {
        using var id = ImRaii.PushId(mode);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextWrapped(Lang.Get($"SpecialRenderMode-Mode-{mode}"));

        ImGui.TableNextColumn();
        if (mode == "DisableWorldRenderButAddons")
            config.BackgroundColor = ImGuiComponents.ColorPickerWithPalette(1, string.Empty, config.BackgroundColor);

        ImGui.TableNextColumn();
        var enableClicked = ImGui.Button(enableText, new(-1, 0));

        ImGui.TableNextColumn();
        var disableClicked = ImGui.Button(disableText, new(-1, 0));

        enabled = enableClicked;
        return enableClicked || disableClicked;
    }

    private class Config : ModuleConfig
    {
        public Vector4 BackgroundColor = new(0.003f, 0.246f, 0.393f, 1f);
    }
}
