using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Colors;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.Interop.Game.Models.Packets.Upstream;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoCollectableExchange : ModuleBase
{
    private static readonly CompSig HandInCollectablesSig =
        new("48 89 6C 24 ?? 48 89 74 24 ?? 57 41 56 41 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B F1 48 8B 49");

    private static HandInCollectablesDelegate? HandInCollectables;

    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoCollectableExchangeTitle"),
        Description = Lang.Get("AutoCollectableExchangeDescription"),
        Category    = ModuleCategory.UIOperation
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    protected override void Init()
    {
        TaskHelper ??= new();
        Overlay    ??= new(this);

        HandInCollectables ??= HandInCollectablesSig.GetDelegate<HandInCollectablesDelegate>();

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "CollectablesShop", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "CollectablesShop", OnAddon);
        if (CollectablesShopAddon != null)
            OnAddon(AddonEvent.PostSetup, null);
    }

    protected override void OverlayUI()
    {
        var addon = CollectablesShopAddon;

        if (addon == null)
        {
            Overlay.IsOpen = false;
            return;
        }

        var buttonNode = CollectablesShopAddon->GetNodeById(51);
        if (buttonNode == null) return;

        if (buttonNode->IsVisible())
            buttonNode->ToggleVisibility(false);

        using var font = FontManager.Instance().UIFont80.Push();

        ImGui.SetWindowPos
        (
            new Vector2(addon->X + addon->GetScaledWidth(true), addon->Y + addon->GetScaledHeight(true)) -
            ImGui.GetWindowSize()                                                                        -
            ScaledVector2(12f)
        );

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(ImGuiColors.DalamudYellow, Lang.Get("AutoCollectableExchangeTitle"));

        using (ImRaii.Disabled(!buttonNode->NodeFlags.HasFlag(NodeFlags.Enabled) || TaskHelper.IsBusy))
        {
            if (ImGui.Button(Lang.Get("Start")))
                EnqueueExchange();
        }

        ImGui.SameLine();

        using (ImRaii.Disabled(!TaskHelper.IsBusy))
        {
            if (ImGui.Button(Lang.Get("Stop")))
                TaskHelper.Abort();
        }

        ImGui.SameLine();
        ImGui.TextDisabled("|");

        using (ImRaii.Disabled(TaskHelper.IsBusy))
        {
            ImGui.SameLine();

            using (ImRaii.Disabled(!buttonNode->NodeFlags.HasFlag(NodeFlags.Enabled)))
            {
                if (ImGui.Button(LuminaWrapper.GetAddonText(531)))
                    HandInCollectables(AgentModule.Instance()->GetAgentByInternalId(AgentId.CollectablesShop));
            }

            ImGui.SameLine();

            if (ImGui.Button(LuminaGetter.GetRow<InclusionShop>(3801094)!.Value.Unknown0.ToString()))
            {
                TaskHelper.Enqueue
                (() =>
                    {
                        if (CollectablesShopAddon->IsAddonAndNodesReady())
                            CollectablesShopAddon->Close(true);
                    }
                );
                TaskHelper.Enqueue(() => !DService.Instance().Condition.IsOccupiedInEvent);
                TaskHelper.Enqueue
                (() => GamePacketManager.Instance().SendPackt
                 (
                     new EventStartPackt
                     (
                         DService.Instance().ObjectTable.LocalPlayer.GameObjectID,
                         GetScriptEventID(GameState.TerritoryType)
                     )
                 )
                );
            }
        }
    }

    private void EnqueueExchange()
    {
        TaskHelper.Enqueue
        (
            () =>
            {
                if (CollectablesShopAddon == null || SelectYesno->IsAddonAndNodesReady())
                {
                    TaskHelper.Abort();
                    return true;
                }

                var list = CollectablesShopAddon->GetComponentNodeById(31)->GetAsAtkComponentList();
                if (list == null) return false;

                if (list->ListLength <= 0)
                {
                    TaskHelper.Abort();
                    return true;
                }

                HandInCollectables(AgentModule.Instance()->GetAgentByInternalId(AgentId.CollectablesShop));
                return true;
            },
            "ClickExchange"
        );

        TaskHelper.Enqueue(EnqueueExchange, "EnqueueNewRound");
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
        var addon = args.Addon.ToStruct();
        if (addon == null) return;

        Overlay.IsOpen = type switch
        {
            AddonEvent.PostSetup   => true,
            AddonEvent.PreFinalize => false,
            _                      => Overlay.IsOpen
        };
    }

    protected override void Uninit() =>
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);

    private delegate nint HandInCollectablesDelegate(AgentInterface* agentCollectablesShop);
}
