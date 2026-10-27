using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using OmenTools.Info.Algorithms;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoInDutySelectYes : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoInDutySelectYesTitle"),
        Description = Lang.Get("AutoInDutySelectYesDescription"),
        Category    = ModuleCategory.Duty
    };

    protected override void Init()
    {
        Whitelist = new
        (
            LuminaGetter.Get<GimmickYesNo>()
                        .Select(x => x.Message.ToString())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
        );

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnAddonSelectYesno);
    }

    protected override void Uninit() =>
        IAddonLifecycle.Instance().UnregisterListener(OnAddonSelectYesno);

    private unsafe void OnAddonSelectYesno
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        if (GameState.ContentFinderCondition == 0) return;

        var addon = (AddonSelectYesno*)args.Addon.Address;
        if (addon == null) return;

        var text = addon->PromptText->NodeText.ToString();
        if (string.IsNullOrWhiteSpace(text) || !Whitelist.ContainsAny(text))
            return;

        AddonSelectYesnoEvent.ClickYes();
    }

    #region 常量

    private AhoCorasick Whitelist = null!;

    #endregion
}
