using DailyRoutines.Common.Interface.Windows;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Utility;
using OmenTools.OmenService;
using OmenTools.Threading.TaskHelper;
using NotifyHelper = OmenTools.OmenService.NotifyHelper;

namespace DailyRoutines.ModulesPublic;

public class AutoShowDutyGuide : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "自动显示副本攻略",
        Description = "进入副本后，自动以悬浮窗形式显示来自“新大陆见闻录”网站的副本攻略",
        Category    = ModuleCategory.Combat
    };

    private Config config = null!;

    private List<string> guideData = [];
    private bool         isOnDebug;

    protected override void Init()
    {
        config =   Config.Load(this) ?? new();
        TaskHelper   ??= new TaskHelper { TimeoutMS = 60_000 };

        Overlay                 ??= new Overlay(this);
        Overlay.Flags           &=  ~ImGuiWindowFlags.NoTitleBar;
        Overlay.Flags           &=  ~ImGuiWindowFlags.AlwaysAutoResize;
        Overlay.Flags           |=  ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavInputs;
        Overlay.ShowCloseButton =   false;

        DService.Instance().ClientState.TerritoryChanged += OnZoneChange;
        OnZoneChange(0);
    }
    
    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChange;
        guideData.Clear();
    }

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(100f * GlobalUIScale);
        ImGui.InputFloat(Lang.Get("FontScale"), ref config.FontScale);
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);

        using (ImRaii.Disabled(DService.Instance().Condition.IsBoundByDuty))
        {
            if (ImGui.Checkbox("调试模式", ref isOnDebug))
            {
                TaskHelper.Abort();
                guideData.Clear();
                Overlay.IsOpen = false;

                if (isOnDebug)
                    TaskHelper.EnqueueAsync(() => GetDutyGuide(1));
            }

            ImGuiOm.TooltipHover("进入调试模式需要拉取在线数据，请耐心等待，切勿频繁开关");
        }
    }

    protected override void OverlayOnOpen() =>
        ImGui.SetScrollHereY();

    protected override void OverlayPreDraw()
    {
        if (!isOnDebug && (!DService.Instance().Condition.IsBoundByDuty || guideData.Count <= 0))
        {
            Overlay.IsOpen = false;
            guideData.Clear();
            TaskHelper.Abort();
            return;
        }

        if (guideData.Count > 0)
            Overlay.WindowName = $"{guideData[0]}###AutoShowDutyGuide-GuideWindow";
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

        for (var i = 1; i < guideData.Count; i++)
        {
            var       text = guideData[i];
            using var id   = ImRaii.PushId($"DutyGuideLine-{i}");

            ImGui.TextWrapped(text);

            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            if (ImGui.IsItemClicked())
            {
                ImGui.SetClipboardText(text);
                NotifyHelper.Instance().Chat("已将本段攻略内容复制至剪贴板");
            }

            ImGui.NewLine();
        }
    }

    private void OnZoneChange(ushort zone)
    {
        TaskHelper.Abort();
        guideData.Clear();
        Overlay.IsOpen = false;

        if (GameState.ContentFinderCondition == 0) return;

        TaskHelper.EnqueueAsync(() => GetDutyGuide(GameState.ContentFinderCondition));
    }

    private async Task GetDutyGuide(uint dutyID)
    {
        try
        {
            var originalText = await HTTPClientHelper.Instance().Get().GetStringAsync(string.Format(FF14_ORG_LINK_BASE, dutyID));

            var plainText = originalText.SanitizeMarkdown();

            if (!string.IsNullOrWhiteSpace(plainText))
            {
                guideData      = [.. plainText.Split('\n')];
                Overlay.IsOpen = true;
            }
        }
        catch
        {
            // ignored
        }
    }
    
    private class Config : ModuleConfig
    {
        public float FontScale = 1f;
    }
    
    #region 常量
    
    private const string FF14_ORG_LINK_BASE =
        "https://gh.atmoomen.top/raw.githubusercontent.com/thewakingsands/novice-network/refs/heads/master/docs/duty/{0}.md";
    
    #endregion
}
