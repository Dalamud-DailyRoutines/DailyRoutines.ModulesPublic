using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace DailyRoutines.ModulesPublic;

public class AutoInDutySelectYes : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoInDutySelectYesTitle"),
        Description = GetLoc("AutoInDutySelectYesDescription"),
        Category    = ModuleCategories.Combat,
    };

    private static readonly AhoCorasick Blacklist = new(
    [
        "小队", "传送邀请", "救助", "复活", "无法战斗", "即将返回", "开始地点", "回归点", "准备确认", "倒计时",
        "小隊", "傳送邀請", "無法戰鬥", "即將返回", "開始地點", "回归點", "準備確認", "倒計時",
        "Party", "Teleport Offer", "Raise", "Arise", "Incapacitated ", "Return", "Starting Point", "Ready Check", "Timer", "Countdown",
        "パーティ", "テレポ勧誘", "テレポの勧誘", "蘇生", "アレイズ", "ホームポイント", "戦闘不能", "開始地点", "復帰地点", "レディチェック", "カウント"
    ]);

    protected override void Init() => 
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnAddonSelectYesno);

    private static unsafe void OnAddonSelectYesno(AddonEvent type, AddonArgs args)
    {
        if (GameState.ContentFinderCondition == 0) return;
        
        var addon = (AddonSelectYesno*)args.Addon;
        if (addon == null) return;
        
        var text = addon->PromptText->NodeText.ExtractText();
        if (string.IsNullOrWhiteSpace(text) || Blacklist.ContainsAny(text))
            return;
        
        ClickSelectYesnoYes();
    }

    protected override void Uninit() => 
        DService.AddonLifecycle.UnregisterListener(OnAddonSelectYesno);
}
