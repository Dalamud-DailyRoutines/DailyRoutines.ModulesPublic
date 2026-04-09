using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Internal;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoRequestItemSubmit : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoRequestItemSubmitTitle"),
        Description = Lang.Get("AutoRequestItemSubmitDescription"),
        Category    = ModuleCategory.UIOperation
    };

    private Config config = null!;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "Request", OnAddonRequest);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "Request", OnAddonRequest);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "Request", OnAddonRequest);
    }

    protected override void ConfigUI()
    {
        ImGuiOm.ConflictKeyText();

        if (ImGui.Checkbox(Lang.Get("AutoRequestItemSubmit-SubmitHQItem"), ref config.IsSubmitHQItem))
            config.Save(this);
    }

    private void OnAddonRequest(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnAddonSelectYesno);
                break;
            case AddonEvent.PostDraw:
                OperateOnRequest();
                break;
            case AddonEvent.PreFinalize:
                DService.Instance().AddonLifecycle.UnregisterListener(OnAddonSelectYesno);
                break;
        }
    }

    private void OnAddonSelectYesno(AddonEvent type, AddonArgs args)
    {
        if (!config.IsSubmitHQItem) return;

        var text = ((AddonSelectYesno*)SelectYesno)->PromptText->NodeText.ToString();
        if (!HQItemTexts.Contains(text)) return;

        AddonSelectYesnoEvent.ClickYes();
    }

    private static void OperateOnRequest()
    {
        if (PluginConfig.Instance().ConflictKeyBinding.IsPressed())
            return;

        var addon = (AddonRequest*)Request;
        if (addon == null) return;

        if (addon->HandOverButton->IsEnabled)
        {
            addon->HandOverButton->Click();
            return;
        }

        var agent = AgentNpcTrade.Instance();
        if (agent == null) return;

        var manager = InventoryManager.Instance();
        if (manager == null) return;

        var container = manager->GetInventoryContainer(InventoryType.HandIn);
        if (container == null) return;

        var requestState = UIState.Instance()->NpcTrade.Requests;

        for (var i = 0; i < requestState.Count; i++)
        {
            var slotState   = container->GetInventorySlot(i);
            var itemRequest = requestState.Items[i];
            if (slotState->ItemId == itemRequest.ItemId) continue;

            // 数据没好, 先请求加载
            if (!ContextIconMenu->IsAddonAndNodesReady())
            {
                AgentId.NpcTrade.SendEvent(0, 2, i, 0, 0);
                return;
            }

            var firstItem = agent->SelectedTurnInSlotItemOptionValues[0].Value;
            if (firstItem == null || firstItem->ItemId == 0) return;

            AgentId.NpcTrade.SendEvent(1, 0, 0, firstItem->GetItemId(), 0U, 0);
            return;
        }
    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonRequest);
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonSelectYesno);
    }

    private class Config : ModuleConfig
    {
        public bool IsSubmitHQItem = true;
    }
    
    #region 常量

    private static readonly FrozenSet<string> HQItemTexts =
    [
        LuminaWrapper.GetAddonText(5450),
        LuminaWrapper.GetAddonText(11514),
        LuminaWrapper.GetAddonText(102434)
    ];

    #endregion
}
