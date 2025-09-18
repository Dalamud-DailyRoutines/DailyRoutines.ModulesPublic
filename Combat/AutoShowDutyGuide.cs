using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using DailyRoutines.Managers;
using DailyRoutines.Windows;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;

namespace DailyRoutines.Modules;

public class AutoShowDutyGuide : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("AutoShowDutyGuideTitle"),
        Description = GetLoc("AutoShowDutyGuideDescription"),
        Category = ModuleCategories.Combat,
    };

    private const string FF14OrgLinkBase = "https://gh.atmoomen.top/novice-network/master/docs/duty/{0}.md";

    private static CancellationTokenSource? CancelSource;

    private static Config ModuleConfig = null!;

    private static uint CurrentDuty;
    private static ISharedImmediateTexture? NoviceIcon;

    private static List<string> GuideText = [];

    private static bool IsOnDebug;

    protected override void Init()
    {
        ModuleConfig =   LoadConfig<Config>() ?? new();
        CancelSource ??= new();
        NoviceIcon   ??= DService.Texture.GetFromGameIcon(new(61523));
        TaskHelper   ??= new TaskHelper { TimeLimitMS = 60000 };

        Overlay ??= new Overlay(this);
        Overlay.Flags &= ~ImGuiWindowFlags.NoTitleBar;
        Overlay.Flags &= ~ImGuiWindowFlags.AlwaysAutoResize;
        Overlay.Flags |= ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavInputs;
        Overlay.ShowCloseButton = false;

        DService.ClientState.TerritoryChanged += OnZoneChange;
        if (BoundByDuty)
            OnZoneChange(DService.ClientState.TerritoryType);
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("WorkTheory")}:");
        ImGuiOm.HelpMarker(GetLoc("AutoShowDutyGuide-TheoryHelp"), 30f);

        ImGui.SetNextItemWidth(80f * GlobalFontScale);
        ImGui.InputFloat(GetLoc("FontScale"), ref ModuleConfig.FontScale);
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(ModuleConfig);

        using (ImRaii.Disabled(BoundByDuty))
        {
            if (ImGui.Checkbox(GetLoc("AutoShowDutyGuide-DebugMode"), ref IsOnDebug))
            {
                if (IsOnDebug) 
                    OnZoneChange(172);
                else
                {
                    GuideText.Clear();
                    CurrentDuty = 0;
                }
            }
        }
        
        ImGuiOm.TooltipHover(GetLoc("AutoShowDutyGuide-DebugModeHelp"));
    }

    protected override void OverlayOnOpen() => ImGui.SetScrollHereY();

    protected override void OverlayPreDraw()
    {
        if (!IsOnDebug && (!BoundByDuty || GuideText.Count <= 0))
        {
            Overlay.IsOpen = false;
            GuideText.Clear();
            return;
        }

        if (GuideText.Count > 0)
            Overlay.WindowName = $"{GuideText[0]}###AutoShowDutyGuide-GuideWindow";
    }

    protected override void OverlayUI()
    {
        using (FontManager.GetUIFont(ModuleConfig.FontScale).Push())
        {
            if (ImGuiOm.SelectableImageWithText(NoviceIcon.GetWrapOrEmpty().Handle, ScaledVector2(24f),
                                                GetLoc("AutoShowDutyGuide-Source"), false))
                Util.OpenLink($"https://ff14.org/duty/{CurrentDuty}.htm");

            ImGui.Separator();

            using (ImRaii.TextWrapPos(ImGui.GetWindowWidth()))
            {
                for (var i = 1; i < GuideText.Count; i++)
                {
                    var       text = GuideText[i];
                    using var id   = ImRaii.PushId($"DutyGuideLine-{i}");
                    
                    ImGui.Text(text);
                    
                    if (ImGui.IsItemHovered())
                        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                    {
                        ImGui.SetClipboardText(text);
                        NotificationSuccess(GetLoc("AutoShowDutyGuide-CopyNotice"));
                    }
                    
                    ImGui.NewLine();
                }
            }
        }
    }

    private void OnZoneChange(ushort territory)
    {
        if (!PresetSheet.Contents.TryGetValue(territory, out var content))
        {
            CurrentDuty = 0;
            GuideText.Clear();
            Overlay.IsOpen = false;
            return;
        }

        DService.Framework.RunOnTick(() => GetDutyGuide(content.RowId), default, default, CancelSource.Token);
    }

    private async Task GetDutyGuide(uint dutyID)
    {
        try
        {
            CurrentDuty = dutyID;
            var originalText = await HttpClientHelper.Get().GetStringAsync(string.Format(FF14OrgLinkBase, dutyID));

            var plainText = MarkdownToPlainText(originalText);
            if (!string.IsNullOrWhiteSpace(plainText))
            {
                GuideText      = [.. plainText.Split('\n')];
                Overlay.IsOpen = true;
            }
        }
        catch (Exception)
        {
            // ignored
        }
    }

    protected override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnZoneChange;
        
        CancelSource?.Cancel();
        CancelSource?.Dispose();
        CancelSource = null;
        
        base.Uninit();
    }

    private class Config : ModuleConfiguration
    {
        public float FontScale = 1f;
    }
}
