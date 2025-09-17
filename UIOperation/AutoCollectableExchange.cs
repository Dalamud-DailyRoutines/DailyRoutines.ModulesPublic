using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Colors;
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
    
    private static readonly CompSig HandInCollectablesSig =
        new("48 89 6C 24 ?? 48 89 74 24 ?? 57 41 56 41 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B F1 48 8B 49");
    private delegate nint HandInCollectablesDelegate(AgentInterface* agentCollectablesShop);
    private static HandInCollectablesDelegate? HandInCollectables;

    protected override void Init()
    {
        TaskHelper ??= new();
        Overlay ??= new(this);
        
        HandInCollectables ??= HandInCollectablesSig.GetDelegate<HandInCollectablesDelegate>();

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "CollectablesShop", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "CollectablesShop", OnAddon);
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
        using (ImRaii.Disabled(!TaskHelper.IsBusy))
        {
            if (ImGui.Button(GetLoc("Stop"))) 
                TaskHelper.Abort();
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
                                       new EventStartPackt(DService.ObjectTable.LocalPlayer.GameObjectId,
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

            var list = InfosOm.CollectablesShop->GetComponentNodeById(31)->GetAsAtkComponentList();
            if (list == null) return false;

            if (list->ListLength <= 0)
            {
                TaskHelper.Abort();
                return true;
            }

            HandInCollectables(AgentModule.Instance()->GetAgentByInternalId(AgentId.CollectablesShop));
            return true;
        }, "ClickExchange");

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
        DService.AddonLifecycle.UnregisterListener(OnAddon);

        base.Uninit();
    }
}
