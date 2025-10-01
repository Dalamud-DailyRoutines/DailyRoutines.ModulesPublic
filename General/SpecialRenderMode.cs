using System.Numerics;
using DailyRoutines.Abstracts;
using Dalamud.Interface.Components;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace DailyRoutines.ModulesPublic;

public unsafe class SpecialRenderMode : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("SpecialRenderModeTitle"),
        Description = GetLoc("SpecialRenderModeDescription"),
        Category    = ModuleCategories.General
    };
    
    private delegate        void               ToggleFadeDelegate(EnvironmentManager* manager, int a2, float fadeDuration, Vector4* fadeColor);
    private static readonly ToggleFadeDelegate ToggleFade = 
        new CompSig("E8 ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8D 8F ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8D 4C 24").GetDelegate<ToggleFadeDelegate>();

    private static Config ModuleConfig = null!;
    
    protected override void Init() => ModuleConfig = LoadConfig<Config>() ?? new();

    protected override void Uninit()
    {
        if (ModuleConfig != null)
            SaveConfig(ModuleConfig);
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("SpecialRenderMode-Mode-DisableWorldRenderButAddons"));

        using (ImRaii.PushId("DisableWorldRenderButAddons"))
        using (ImRaii.PushIndent())
        {
            var color = ModuleConfig.BackgroundColor;

            if (ImGui.Button(GetLoc("Enable")))
                ToggleFade(Framework.Instance()->EnvironmentManager, 1, 0.1f, &color);

            ImGui.SameLine();
            if (ImGui.Button(GetLoc("Disable")))
                ToggleFade(Framework.Instance()->EnvironmentManager, 0, 0.1f, &color);

            ImGui.SameLine(0, 8f * GlobalFontScale);
            ImGui.Text($"{GetLoc("Color")}:");
            
            ImGui.SameLine();
            ModuleConfig.BackgroundColor = ImGuiComponents.ColorPickerWithPalette(1, string.Empty, ModuleConfig.BackgroundColor);
        }
        
        ImGui.NewLine();
        
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("SpecialRenderMode-Mode-HideAddonsButNameplate"));

        using (ImRaii.PushId("HideAddonsButNameplate"))
        using (ImRaii.PushIndent())
        {
            if (ImGui.Button(GetLoc("Enable")))
            {
                UIModule.Instance()->ToggleUi(
                    UIModule.UiFlags.ActionBars | UIModule.UiFlags.Chat | UIModule.UiFlags.Hud | UIModule.UiFlags.TargetInfo | UIModule.UiFlags.Shortcuts, false);
                DTR->IsVisible = false;
            }

            ImGui.SameLine();
            if (ImGui.Button(GetLoc("Disable")))
            {
                UIModule.Instance()->ToggleUi(
                    UIModule.UiFlags.ActionBars | UIModule.UiFlags.Chat | UIModule.UiFlags.Hud | UIModule.UiFlags.TargetInfo | UIModule.UiFlags.Shortcuts, true);
                DTR->IsVisible = true;
            }
        }
        
        ImGui.NewLine();
        
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("SpecialRenderMode-Mode-HideAddonsButChatLog"));

        using (ImRaii.PushId("HideAddonsButChatLog"))
        using (ImRaii.PushIndent())
        {
            if (ImGui.Button(GetLoc("Enable")))
            {
                UIModule.Instance()->ToggleUi(
                    UIModule.UiFlags.ActionBars | UIModule.UiFlags.Nameplates | UIModule.UiFlags.Hud | UIModule.UiFlags.TargetInfo | UIModule.UiFlags.Shortcuts, false);
                DTR->IsVisible = false;
            }

            ImGui.SameLine();
            if (ImGui.Button(GetLoc("Disable")))
            {
                UIModule.Instance()->ToggleUi(
                    UIModule.UiFlags.ActionBars | UIModule.UiFlags.Nameplates | UIModule.UiFlags.Hud | UIModule.UiFlags.TargetInfo | UIModule.UiFlags.Shortcuts, true);
                DTR->IsVisible = true;
            }
        }
        
        ImGui.NewLine();
        
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("SpecialRenderMode-Mode-HideChatLog"));

        using (ImRaii.PushId("HideChatLog"))
        using (ImRaii.PushIndent())
        {
            if (ImGui.Button(GetLoc("Enable")))
                UIModule.Instance()->ToggleUi(UIModule.UiFlags.Chat, false);

            ImGui.SameLine();
            if (ImGui.Button(GetLoc("Disable")))
                UIModule.Instance()->ToggleUi(UIModule.UiFlags.Chat, true);
        }
        
        ImGui.NewLine();
        
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("SpecialRenderMode-Mode-HideActionBars"));

        using (ImRaii.PushId("HideActionBars"))
        using (ImRaii.PushIndent())
        {
            if (ImGui.Button(GetLoc("Enable")))
                UIModule.Instance()->ToggleUi(UIModule.UiFlags.ActionBars, false);

            ImGui.SameLine();
            if (ImGui.Button(GetLoc("Disable")))
                UIModule.Instance()->ToggleUi(UIModule.UiFlags.ActionBars, true);
        }
        
        ImGui.NewLine();
        
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("SpecialRenderMode-Mode-HideTargetInfo"));

        using (ImRaii.PushId("HideTargetInfo"))
        using (ImRaii.PushIndent())
        {
            if (ImGui.Button(GetLoc("Enable")))
                UIModule.Instance()->ToggleUi(UIModule.UiFlags.TargetInfo, false);

            ImGui.SameLine();
            if (ImGui.Button(GetLoc("Disable")))
                UIModule.Instance()->ToggleUi(UIModule.UiFlags.TargetInfo, true);
        }
        
        ImGui.NewLine();
        
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("SpecialRenderMode-Mode-HideNameplate"));

        using (ImRaii.PushId("HideNameplate"))
        using (ImRaii.PushIndent())
        {
            if (ImGui.Button(GetLoc("Enable")))
                UIModule.Instance()->ToggleUi(UIModule.UiFlags.Nameplates, false);

            ImGui.SameLine();
            if (ImGui.Button(GetLoc("Disable")))
                UIModule.Instance()->ToggleUi(UIModule.UiFlags.Nameplates, true);
        }
    }

    private class Config : ModuleConfiguration
    {
        public Vector4 BackgroundColor = KnownColor.LightSkyBlue.ToVector4();
    }
}
