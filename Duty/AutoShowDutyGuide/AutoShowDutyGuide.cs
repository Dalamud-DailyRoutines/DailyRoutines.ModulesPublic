using DailyRoutines.Common.Interface.Windows;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Utility;
using OmenTools.ImGuiOm.Markdown;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Duty;

public partial class AutoShowDutyGuide : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "自动显示副本攻略",
        Description = "进入副本后，自动在悬浮窗内显示来自“新大陆见闻录”网站的攻略。",
        Category    = ModuleCategory.Duty
    };

    public override ModulePermission Permission { get; } = new()
    {
        CNDefaultEnabled = true, 
        TCDefaultEnabled = true
    };

    private Config config = null!;

    private string            guideData = string.Empty;
    private MarkdownDocument? guideDocument;

    private DutyGuideMarkdownExtension markdownExtension = null!;
    private ImGuiMarkdown              markdown          = null!;

    private bool isOnDebug;

    protected override void Init()
    {
        config     =   Config.Load(this) ?? new();
        TaskHelper ??= new() { TimeoutMS = 60_000 };

        markdownExtension = new();
        markdown = new
        (
            [markdownExtension],
            new MarkdownConfig
            {
                BaseURL          = "https://ff14.org",
                BaseFontScale    = config.FontScale,
                BreakSoftLines   = true,
                JoinCJKSoftLines = true,
                OnLinkClicked    = OpenGuideLink
            }
        );

        Overlay                 ??= new Overlay(this);
        Overlay.Flags           &=  ~ImGuiWindowFlags.NoTitleBar;
        Overlay.Flags           &=  ~ImGuiWindowFlags.AlwaysAutoResize;
        Overlay.Flags           |=  ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavInputs;
        Overlay.ShowCloseButton =   false;

        IClientState.Instance().TerritoryChanged += OnZoneChange;
        OnZoneChange(0);
    }

    protected override void Uninit()
    {
        IClientState.Instance().TerritoryChanged -= OnZoneChange;

        guideData     = string.Empty;
        guideDocument = null;
    }

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(100f * GlobalUIScale);
        ImGui.InputFloat(Lang.Get("FontScale"), ref config.FontScale);

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.Save(this);
            markdown.Config = markdown.Config with { BaseFontScale = config.FontScale };
        }

        using (ImRaii.Disabled(ICondition.Instance().IsBoundByDuty))
        {
            if (ImGui.Checkbox("调试模式", ref isOnDebug))
            {
                TaskHelper.Abort();
                guideData      = string.Empty;
                Overlay.IsOpen = false;

                if (isOnDebug)
                    TaskHelper.EnqueueAsync(() => GetDutyGuide(1));
            }

            ImGuiOm.TooltipHover("进入调试模式需要拉取在线数据，请耐心等待，切勿频繁开关。");
        }
    }

    protected override void OverlayOnOpen() =>
        ImGui.SetScrollHereY();

    protected override void OverlayPreDraw()
    {
        if (!isOnDebug && (!ICondition.Instance().IsBoundByDuty || string.IsNullOrEmpty(guideData)))
        {
            Overlay.IsOpen = false;
            guideData      = string.Empty;
            TaskHelper.Abort();
        }
    }

    protected override void OverlayUI()
    {
        using var font = FontManager.Instance().GetUIFont(config.FontScale).Push();

        if (ImGuiOm.SelectableImageWithText
            (
                ImageHelper.GetGameIcon(61523).Handle,
                ScaledVector2(24f),
                "来源：新大陆见闻录",
                false
            ))
            Util.OpenLink($"https://ff14.org/duty/{GameState.ContentFinderCondition}.htm");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (guideDocument is not null)
            markdown.Draw(guideDocument);
    }

    private void OnZoneChange
    (
        uint u
    )
    {
        TaskHelper.Abort();
        guideData      = string.Empty;
        Overlay.IsOpen = false;

        if (GameState.ContentFinderCondition == 0) return;

        TaskHelper.EnqueueAsync(() => GetDutyGuide(GameState.ContentFinderCondition));
    }

    private async Task GetDutyGuide
    (
        uint dutyID
    )
    {
        try
        {
            var originalText = await HTTPClientHelper.Instance().Get().GetStringAsync(string.Format(FF14_ORG_LINK_BASE, dutyID));

            if (!string.IsNullOrWhiteSpace(originalText))
            {
                await markdownExtension.LoadComponentsAsync(originalText);
                guideDocument = markdown.Parse(originalText);
                guideData     = originalText;

                Overlay.WindowName = $"{LuminaWrapper.GetContentName(dutyID)}###AutoShowDutyGuide-GuideWindow";
                Overlay.IsOpen     = true;
            }
        }
        catch
        {
            // ignored
        }
    }

    private static void OpenGuideLink
    (
        string url
    )
    {
        var target = new Uri(new Uri("https://ff14.org"), url);

        if (target.Host == "ff14.org" && target.AbsolutePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(target)
            {
                Path = target.AbsolutePath[..^3] + ".htm"
            };
            target = builder.Uri;
        }

        if (target.Scheme is "http" or "https" or "mailto")
            Util.OpenLink(target.AbsoluteUri);
    }

    private class Config : ModuleConfig
    {
        public float FontScale = 1f;
    }

    #region 常量

    private const string FF14_ORG_LINK_BASE =
        "https://gh.atmoomen.top/raw.githubusercontent.com/thewakingsands/novice-network/refs/heads/master/src/content/docs/duty/{0}.mdx";

    #endregion
}
