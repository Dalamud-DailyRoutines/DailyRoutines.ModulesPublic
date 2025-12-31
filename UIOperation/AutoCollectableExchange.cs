using System.Collections.Generic;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.IPC;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Colors;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoCollectableExchange : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoCollectableExchangeTitle"),
        Description = GetLoc("AutoCollectableExchangeDescription"),
        Category    = ModuleCategories.UIOperation,
    };


    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };


    private static readonly CompSig HandInCollectablesSig =
        new("48 89 6C 24 ?? 48 89 74 24 ?? 57 41 56 41 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B F1 48 8B 49");


    private delegate nint HandInCollectablesDelegate(AgentInterface* agentCollectablesShop);
    private static HandInCollectablesDelegate? HandInCollectables;

    private static bool IsWaitingRefresh;

    protected override void Init()
    {
        TaskHelper ??= new();
        Overlay ??= new(this);


        HandInCollectables ??= HandInCollectablesSig.GetDelegate<HandInCollectablesDelegate>();
        CollectableByItemID ??= BuildCollectableDict();

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "CollectablesShop", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "CollectablesShop", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "CollectablesShop", OnAddonRefresh);
        if (InfosOm.CollectablesShop != null)
            OnAddon(AddonEvent.PostSetup, null);
    }

    protected override void OverlayUI()
    {
        var addon = InfosOm.CollectablesShop;
        if (addon == null)
        {
            Overlay.IsOpen = false;
            return;
        }


        var buttonNode = InfosOm.CollectablesShop->GetNodeById(51);
        if (buttonNode == null) return;


        if (buttonNode->IsVisible())
            buttonNode->ToggleVisibility(false);

        using var font = FontManager.UIFont80.Push();

        ImGui.SetWindowPos(new Vector2(addon->X + addon->GetScaledWidth(true), addon->Y + addon->GetScaledHeight(true)) - ImGui.GetWindowSize() -
                           ScaledVector2(12f));

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(ImGuiColors.DalamudYellow, GetLoc("AutoCollectableExchangeTitle"));

        using (ImRaii.Disabled(!buttonNode->NodeFlags.HasFlag(NodeFlags.Enabled) || TaskHelper.IsBusy))
        {
            if (ImGui.Button(GetLoc("Start")))
                EnqueueExchange();
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!TaskHelper.IsBusy && !IsWaitingRefresh))
        {
            if (ImGui.Button(GetLoc("Stop")))
            {
                IsWaitingRefresh = false;
                TaskHelper.Abort();
            }
        }


        ImGui.SameLine();
        ImGui.TextDisabled("|");


        using (ImRaii.Disabled(TaskHelper.IsBusy))
        {
            ImGui.SameLine();
            using (ImRaii.Disabled(!buttonNode->NodeFlags.HasFlag(NodeFlags.Enabled)))
            {
                if (ImGui.Button(LuminaGetter.GetRow<Addon>(531)!.Value.Text.ExtractText()))
                    HandInCollectables(AgentModule.Instance()->GetAgentByInternalId(AgentId.CollectablesShop));
            }


            ImGui.SameLine();
            if (ImGui.Button(LuminaGetter.GetRow<InclusionShop>(3801094)!.Value.Unknown0.ExtractText()))
            {
                TaskHelper.Enqueue(() =>
                {
                    if (IsAddonAndNodesReady(InfosOm.CollectablesShop))
                        InfosOm.CollectablesShop->Close(true);
                });
                TaskHelper.Enqueue(() => !OccupiedInEvent);
                TaskHelper.Enqueue(() => GamePacketManager.SendPackt(
                                       new EventStartPackt(DService.ObjectTable.LocalPlayer.GameObjectID,
                                                           GetScriptEventID(DService.ClientState.TerritoryType))));
            }
        }
    }

    private void EnqueueExchange()
    {
        TaskHelper.Enqueue(() =>
        {
            if (InfosOm.CollectablesShop == null || IsAddonAndNodesReady(SelectYesno))
            {
                TaskHelper.Abort();
                return true;
            }

            var node = InfosOm.CollectablesShop->GetComponentNodeById(31);
            if (node == null) return false;

            var list = node->GetAsAtkComponentList();
            if (list == null || list->ListLength <= 0)
            {
                TaskHelper.Abort();
                return true;
            }

            if (WouldScripOverflow())
            {
                TaskHelper.Abort();
                return true;
            }

            IsWaitingRefresh = true;
            HandInCollectables(AgentModule.Instance()->GetAgentByInternalId(AgentId.CollectablesShop));
            return true;
        }, "ClickExchange");
    }

    private static bool WouldScripOverflow()
    {
        var addon = InfosOm.CollectablesShop;
        if (addon == null || addon->AtkValuesCount < 35) return false;

        var node = addon->GetComponentNodeById(31);
        if (node == null) return false;

        var list = node->GetAsAtkComponentList();
        if (list == null || list->ListLength <= 0) return false;

        var idx = list->SelectedItemIndex;
        var count = addon->AtkValues[20].UInt;
        if (idx < 0 || idx >= count) return false;

        var valueIndex = 34 + (11 * idx);
        if (addon->AtkValuesCount <= valueIndex) return false;

        var itemID = addon->AtkValues[valueIndex].UInt % 50_0000;
        if (itemID == 0) return false;

        foreach (var row in LuminaGetter.GetSub<CollectablesShopItem>())
        {
            foreach (var sub in row)
            {
                if (sub.Item.RowId != itemID) continue;

                var scrip = sub.CollectablesShopRewardScrip.ValueNullable;
                if (scrip is not { Currency: > 0 and <= byte.MaxValue } s) return false;

                var mgr = CurrencyManager.Instance();
                var scripItem = mgr->GetItemIdBySpecialId((byte)s.Currency);
                if (scripItem == 0) return false;

                return s.HighReward > mgr->GetItemMaxCount(scripItem) - mgr->GetItemCount(scripItem);
            }
        }

        return false;
    }

    private void OnAddonRefresh(AddonEvent type, AddonArgs args)
    {
        if (!IsWaitingRefresh) return;

        IsWaitingRefresh = false;
        EnqueueExchange();
    }

    private static uint GetScriptEventID(uint zone)
        => zone switch
        {
            478  => 3539065, // 田园郡
            635  => 3539064, // 神拳痕
            820  => 3539063, // 游末邦
            963  => 3539062, // 拉札罕
            1186 => 3539072, // 九号解决方案
            _    => 3539066  // 利姆萨·罗敏萨下层甲板、格里达尼亚旧街、乌尔达哈来生回廊
        };

    private void OnAddon(AddonEvent type, AddonArgs? args)
    {
        var addon = args.Addon.ToAtkUnitBase();
        if (addon == null) return;

        Overlay.IsOpen = type switch
        {
            AddonEvent.PostSetup => true,
            AddonEvent.PreFinalize => false,
            _ => Overlay.IsOpen,
        };
    }

    protected override void Uninit()
    {
        IsWaitingRefresh = false;
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        DService.AddonLifecycle.UnregisterListener(OnAddonRefresh);
    }
}
