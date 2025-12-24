using System.Collections.Generic;
using System.Threading.Tasks;
using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using DailyRoutines.Windows;
using Dalamud.Utility;

namespace DailyRoutines.ModulesPublic;

public class AutoShowDutyGuide : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "自动显示副本攻略",
        Description = "进入副本后, 自动以悬浮窗形式显示来自\"新大陆见闻录\"网站的副本攻略",
        Category    = ModuleCategories.Combat,
    };

    private const string FF14OrgLinkBase = 
        "https://gh.atmoomen.top/raw.githubusercontent.com/thewakingsands/novice-network/refs/heads/master/docs/duty/{0}.md";
    
    private static Config ModuleConfig = null!;
    
    private static List<string> GuideData = [];
    private static bool         IsOnDebug;

    protected override void Init()
    {
        ModuleConfig =   LoadConfig<Config>() ?? new();
        TaskHelper   ??= new TaskHelper { TimeLimitMS = 60_000 };

        Overlay ??= new Overlay(this);
        Overlay.Flags &= ~ImGuiWindowFlags.NoTitleBar;
        Overlay.Flags &= ~ImGuiWindowFlags.AlwaysAutoResize;
        Overlay.Flags |= ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavInputs;
        Overlay.ShowCloseButton = false;

        DService.ClientState.TerritoryChanged += OnZoneChange;
        OnZoneChange(0);
    }

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(100f * GlobalFontScale);
        ImGui.InputFloat(GetLoc("FontScale"), ref ModuleConfig.FontScale);
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(ModuleConfig);

        using (ImRaii.Disabled(BoundByDuty))
        {
            if (ImGui.Checkbox("调试模式", ref IsOnDebug))
            {
                TaskHelper.Abort();
                GuideData.Clear();
                Overlay.IsOpen = false;
                
                if (IsOnDebug) 
                    TaskHelper.EnqueueAsync(() => GetDutyGuide(1));
            }
            
            ImGuiOm.TooltipHover("进入调试模式需要拉取在线数据, 请耐心等待, 切勿频繁开关");
        }
    }

    protected override void OverlayOnOpen() => 
        ImGui.SetScrollHereY();

    protected override void OverlayPreDraw()
    {
        if (!IsOnDebug && (!BoundByDuty || GuideData.Count <= 0))
        {
            Overlay.IsOpen = false;
            GuideData.Clear();
            TaskHelper.Abort();
            return;
        }

        if (GuideData.Count > 0)
            Overlay.WindowName = $"{GuideData[0]}###AutoShowDutyGuide-GuideWindow";
    }

    protected override void OverlayUI()
    {
        using var font = FontManager.GetUIFont(ModuleConfig.FontScale).Push();
        
        if (ImGuiOm.SelectableImageWithText(ImageHelper.GetGameIcon(61523).Handle, 
                                            ScaledVector2(24f),
                                            "来源: 新大陆见闻录",
                                            false))
            Util.OpenLink($"https://ff14.org/duty/{GameState.ContentFinderCondition}.htm");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        for (var i = 1; i < GuideData.Count; i++)
        {
            var       text = GuideData[i];
            using var id   = ImRaii.PushId($"DutyGuideLine-{i}");
                
            ImGui.TextWrapped(text);
                    
            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                
            if (ImGui.IsItemClicked())
            {
                ImGui.SetClipboardText(text);
                Chat("已将本段攻略内容复制至剪贴板");
            }
                    
            ImGui.NewLine();
        }
    }

    private void OnZoneChange(ushort zone)
    {
        TaskHelper.Abort();
        GuideData.Clear();
        Overlay.IsOpen = false;
        
        if (GameState.ContentFinderCondition == 0) return;

        TaskHelper.EnqueueAsync(() => GetDutyGuide(GameState.ContentFinderCondition));
    }

    private async Task GetDutyGuide(uint dutyID)
    {
        try
        {
            var originalText = await HttpClientHelper.Get().GetStringAsync(string.Format(FF14OrgLinkBase, dutyID));

            var plainText = MarkdownToPlainText(originalText);
            if (!string.IsNullOrWhiteSpace(plainText))
            {
                GuideData      = [.. plainText.Split('\n')];
                Overlay.IsOpen = true;
            }
        }
        catch
        {
            // ignored
        }
    }

    protected override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnZoneChange;
        GuideData.Clear();
    }

    private class Config : ModuleConfiguration
    {
        public float FontScale = 1f;
    }
}
