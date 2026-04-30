using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Lumina.ExtraSheets;
using OmenTools.Interop.Game.Models.Packets.Upstream;
using OmenTools.OmenService;
using Action = System.Action;
using ObjectKind = FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoGardensWork : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = Lang.Get("AutoGardensWorkTitle"),
        Description         = Lang.Get("AutoGardensWorkDescription"),
        Category            = ModuleCategory.General,
        ModulesPrerequisite = ["AutoTalkSkip"]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };
    
    private Config config = null!;

    private string searchSeed      = string.Empty;
    private string searchSoil      = string.Empty;
    private string searchFertilize = string.Empty;

    protected override void Init()
    {
        config =   Config.Load(this) ?? new();
        TaskHelper   ??= new() { TimeoutMS = 10_000 };

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "HousingGardening", OnAddon);

        TargetManager.Instance().RegPostSetHardTarget(OnSetHardTarget);
    }
    
    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
        TargetManager.Instance().Unreg(OnSetHardTarget);
    }

    protected override void ConfigUI()
    {
        DrawAutoPlant();

        ImGui.NewLine();

        DrawAutoGather();

        ImGui.NewLine();

        DrawAutoFertilize();

        ImGui.NewLine();

        DrawAutoTend();
    }

    private void DrawAutoPlant()
    {
        using var id = ImRaii.PushId("AutoPlant");

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoGardensWork-AutoPlant")}");

        using var indent = ImRaii.PushIndent();

        using (ImRaii.Disabled(TaskHelper?.IsBusy ?? true))
        {
            if (ImGui.Button($"{FontAwesomeIcon.Play.ToIconString()} {Lang.Get("Start")}"))
                StartPlant();
        }

        ImGui.SameLine();
        if (ImGui.Button($"{FontAwesomeIcon.Stop.ToIconString()} {Lang.Get("Stop")}"))
            TaskHelper.Abort();

        ImGui.Spacing();

        ImGui.SetNextItemWidth(300f * GlobalUIScale);
        if (ImGuiOm.SingleSelectCombo
            (
                "SeedSelectCombo",
                Sheets.Seeds,
                ref config.SeedSelected,
                ref searchSeed,
                x => $"{x.Name.ToString()} ({x.RowId})",
                [new(LuminaWrapper.GetAddonText(6412), ImGuiTableColumnFlags.WidthStretch, 0)],
                [
                    x => () =>
                    {
                        var icon = ImageHelper.GetGameIcon(x.Icon);
                        if (ImGuiOm.SelectableImageWithText
                            (
                                icon.Handle,
                                new(ImGui.GetTextLineHeightWithSpacing()),
                                x.Name.ToString(),
                                x.RowId == config.SeedSelected,
                                ImGuiSelectableFlags.DontClosePopups | ImGuiSelectableFlags.SpanAllColumns
                            ))
                            config.SeedSelected = x.RowId;
                    }
                ],
                [x => x.Name.ToString(), x => x.RowId.ToString()],
                true
            ))
            config.Save(this);

        ImGui.SameLine();
        ImGui.TextUnformatted(LuminaWrapper.GetAddonText(6412));

        ImGui.SetNextItemWidth(300f * GlobalUIScale);

        using (ImRaii.PushId("SoilSelectCombo"))
        {
            if (ImGuiOm.SingleSelectCombo
                (
                    "SoilSelectCombo",
                    Sheets.Soils,
                    ref config.SoilSelected,
                    ref searchSoil,
                    x => $"{x.Name.ToString()} ({x.RowId})",
                    [new(LuminaWrapper.GetAddonText(6411), ImGuiTableColumnFlags.WidthStretch, 0)],
                    [
                        x => () =>
                        {
                            var icon = ImageHelper.GetGameIcon(x.Icon);
                            if (ImGuiOm.SelectableImageWithText
                                (
                                    icon.Handle,
                                    new(ImGui.GetTextLineHeightWithSpacing()),
                                    x.Name.ToString(),
                                    x.RowId == config.SeedSelected,
                                    ImGuiSelectableFlags.DontClosePopups
                                ))
                                config.SoilSelected = x.RowId;
                        }
                    ],
                    [x => x.Name.ToString(), x => x.RowId.ToString()],
                    true
                ))
                config.Save(this);
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(LuminaWrapper.GetAddonText(6411));
    }

    private void DrawAutoGather()
    {
        using var id = ImRaii.PushId("AutoGather");

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoGardensWork-AutoGather")}");

        using var indent = ImRaii.PushIndent();

        using (ImRaii.Disabled(TaskHelper?.IsBusy ?? true))
        {
            if (ImGui.Button($"{FontAwesomeIcon.Play.ToIconString()} {Lang.Get("Start")}"))
                StartGather();
        }

        ImGui.SameLine();
        if (ImGui.Button($"{FontAwesomeIcon.Stop.ToIconString()} {Lang.Get("Stop")}"))
            TaskHelper.Abort();
    }

    private void DrawAutoFertilize()
    {
        using var id = ImRaii.PushId("AutoFertilize");

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoGardensWork-AutoFertilize")}");

        using var indent = ImRaii.PushIndent();

        using (ImRaii.Disabled(TaskHelper?.IsBusy ?? true))
        {
            if (ImGui.Button($"{FontAwesomeIcon.Play.ToIconString()} {Lang.Get("Start")}"))
                StartFertilize();
        }

        ImGui.SameLine();
        if (ImGui.Button($"{FontAwesomeIcon.Stop.ToIconString()} {Lang.Get("Stop")}"))
            TaskHelper.Abort();

        ImGui.Spacing();

        ImGui.SetNextItemWidth(300f * GlobalUIScale);
        if (ImGuiOm.SingleSelectCombo
            (
                "FertilizersSelectCombo",
                Sheets.Fertilizers,
                ref config.FertilizerSelected,
                ref searchFertilize,
                x => $"{x.Name.ToString()} ({x.RowId})",
                [new(LuminaWrapper.GetAddonText(6417), ImGuiTableColumnFlags.WidthStretch, 0)],
                [
                    x => () =>
                    {
                        if (ImGuiOm.SelectableImageWithText
                            (
                                ImageHelper.GetGameIcon(x.Icon).Handle,
                                new(ImGui.GetTextLineHeightWithSpacing()),
                                x.Name.ToString(),
                                x.RowId == config.SeedSelected,
                                ImGuiSelectableFlags.DontClosePopups | ImGuiSelectableFlags.SpanAllColumns
                            ))
                            config.FertilizerSelected = x.RowId;
                    }
                ],
                [x => x.Name.ToString(), x => x.RowId.ToString()],
                true
            ))
            config.Save(this);

        ImGui.SameLine();
        ImGui.TextUnformatted(LuminaWrapper.GetAddonText(6417));
    }

    private void DrawAutoTend()
    {
        using var id = ImRaii.PushId("AutoTend");

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoGardensWork-AutoTend")}");

        using var indent = ImRaii.PushIndent();

        using (ImRaii.Disabled(TaskHelper?.IsBusy ?? true))
        {
            if (ImGui.Button($"{FontAwesomeIcon.Play.ToIconString()} {Lang.Get("Start")}"))
                StartTend();
        }

        ImGui.SameLine();
        if (ImGui.Button($"{FontAwesomeIcon.Stop.ToIconString()} {Lang.Get("Stop")}"))
            TaskHelper.Abort();
    }
    
    #region 事件

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        if (config.SeedSelected == 0 || config.SoilSelected == 0) return;

        if (!Inventories.Player.TryGetFirstItem(x => x.ItemId == config.SeedSelected, out var seedItem) ||
            !Inventories.Player.TryGetFirstItem(x => x.ItemId == config.SoilSelected, out var soilItem))
            return;

        TaskHelper.Enqueue
        (
            () =>
            {
                var agent = AgentHousingPlant.Instance();
                if (agent == null) return;

                agent->SelectedItems[0] = new()
                {
                    ItemId        = soilItem->ItemId,
                    InventoryType = soilItem->Container,
                    InventorySlot = (ushort)soilItem->Slot
                };
                agent->SelectedItems[1] = new()
                {
                    ItemId        = seedItem->ItemId,
                    InventoryType = seedItem->Container,
                    InventorySlot = (ushort)seedItem->Slot
                };

                agent->ConfirmSeedAndSoilSelection();
            },
            weight: 2
        );

        TaskHelper.Enqueue(() => AddonSelectYesnoEvent.ClickYes(), weight: 2);
    }

    private void OnSetHardTarget(bool result, IGameObject? target, bool checkMode, bool a4, int a5)
    {
        var outdoorZone = HousingManager.Instance()->OutdoorTerritory;
        if (outdoorZone == null) return;

        switch (target)
        {
            case null when (OverlayConfig?.IsOpen ?? false):
                ToggleOverlayConfig(false);
                break;

            case { ObjectKind: Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj, DataID: 2003757 }:
                ToggleOverlayConfig(true);
                break;

            default:
                ToggleOverlayConfig(false);
                break;
        }
    }

    #endregion

    #region 流程

    private void StartAction(string entryKeyword, Action extraAction = null)
    {
        if (!IsEnvironmentValid(out var objectIDs)) return;

        TaskHelper.Enqueue(() => TargetManager.Target = null, "移除当前目标");

        foreach (var garden in objectIDs)
        {
            TaskHelper.Enqueue(() => new EventStartPackt(garden, 721047).Send(), $"交互园圃: {garden}");
            TaskHelper.Enqueue(() => ClickGardenEntryByText(entryKeyword),       "点击");
            extraAction?.Invoke();
            TaskHelper.Enqueue(() => !DService.Instance().Condition[ConditionFlag.OccupiedInQuestEvent], "等待退出交互状态");
        }
    }

    private void StartGather() =>
        StartAction(LuminaGetter.GetRowOrDefault<HousingGardeningPlant>(6).Text.ToString());

    private void StartTend() =>
        StartAction(LuminaGetter.GetRowOrDefault<HousingGardeningPlant>(4).Text.ToString());

    private void StartPlant() =>
        StartAction(LuminaGetter.GetRowOrDefault<HousingGardeningPlant>(2).Text.ToString(), () => TaskHelper.DelayNext(250));

    private void StartFertilize() =>
        StartAction
        (
            LuminaGetter.GetRowOrDefault<HousingGardeningPlant>(3).Text.ToString(),
            () =>
            {
                TaskHelper.Enqueue(CheckFertilizerState);
                TaskHelper.Enqueue(ClickFertilizer);
                TaskHelper.Enqueue(() => !DService.Instance().Condition[ConditionFlag.OccupiedInQuestEvent]);
            }
        );

    #endregion

    #region 工具

    private static bool IsEnvironmentValid(out List<ulong> objectIDs)
    {
        objectIDs = [];

        if (DService.Instance().ObjectTable.LocalPlayer == null) return false;

        var manager = HousingManager.Instance();
        if (manager == null) return false;

        // 不在房区里
        var outdoorZone = manager->OutdoorTerritory;
        if (outdoorZone == null) return false;

        var houseID = (ulong)manager->GetCurrentHouseId();
        if (houseID == 0) return false;

        // 在自己有权限的房子院子里
        // 具体怎么个有权限法不想测了
        foreach (var type in Enum.GetValues<EstateType>())
        {
            if (type == EstateType.SharedEstate)
            {
                for (var i = 0; i < 2; i++)
                {
                    var typeHouseID = HousingManager.GetOwnedHouseId(type, i);
                    if (typeHouseID == houseID)
                        goto Out;
                }
            }
            else
            {
                var typeHouseID = HousingManager.GetOwnedHouseId(type);
                if (typeHouseID == houseID)
                    goto Out;
            }
        }

        return false;

        Out: ;

        // 找一下有没有园圃
        return TryObtainGardensAround(out objectIDs);
    }

    private static bool TryObtainGardensAround(out List<ulong> objectIDs)
    {
        objectIDs = [];

        var outdoorZone = HousingManager.Instance()->OutdoorTerritory;
        if (outdoorZone == null) return false;

        // 找一下有没有园圃
        List<(ulong GameObjectID, Vector3 Position)> gardenCenters = [];

        foreach (var housingObj in outdoorZone->FurnitureManager.ObjectManager.ObjectArray.Objects)
        {
            if (housingObj == null || housingObj.Value == null) continue;
            if (housingObj.Value->ObjectKind                              != ObjectKind.HousingEventObject) continue;
            if (housingObj.Value->BaseId                                  != 131128) continue;
            if (LocalPlayerState.DistanceTo3D(housingObj.Value->Position) > 10) continue;

            gardenCenters.Add(new(housingObj.Value->GetGameObjectId(), housingObj.Value->Position));
        }

        if (gardenCenters.Count == 0) return false;

        // 园圃家具周围绕一圈的那个实际可交互的坑位
        objectIDs = DService.Instance().ObjectTable
                            .Where
                            (x => x is { ObjectKind: Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj, DataID: 2003757 } &&
                                  gardenCenters.Any(g => Vector3.DistanceSquared(x.Position, g.Position) <= 25)
                            )
                            .Select(x => x.GameObjectID)
                            .ToList();
        return objectIDs.Count > 0;
    }

    private static bool CheckFertilizerState()
    {
        if (SelectString != null) return false;

        return Inventory->IsVisible          ||
               InventoryLarge->IsVisible     ||
               InventoryExpansion->IsVisible ||
               !DService.Instance().Condition[ConditionFlag.OccupiedInQuestEvent];
    }

    private bool ClickFertilizer()
    {
        if (SelectString != null) return false;
        if (!DService.Instance().Condition[ConditionFlag.OccupiedInQuestEvent]) return true;

        if (config.FertilizerSelected == 0 ||
            !Inventories.Player.TryGetFirstItem(x => x.ItemId == config.FertilizerSelected, out var fertilizerItem))
        {
            TaskHelper.Abort();
            return true;
        }

        AgentInventoryContext.Instance()->
            OpenForItemSlot
        (
            fertilizerItem->Container,
            fertilizerItem->Slot,
            0,
            AgentModule.Instance()->GetAgentByInternalId(AgentId.Inventory)->AddonId
        );

        TaskHelper.Enqueue(() => AddonContextMenuEvent.Select(LuminaGetter.GetRowOrDefault<HousingGardeningPlant>(3).Text.ToString()), weight: 2);
        return true;
    }

    private static bool ClickGardenEntryByText(string text)
    {
        if (!SelectString->IsAddonAndNodesReady())
            return false;

        if (!AddonSelectStringEvent.TryScanSelectStringText(text,                                                                  out var index))
            AddonSelectStringEvent.TryScanSelectStringText(LuminaGetter.GetRowOrDefault<HousingGardeningPlant>(1).Text.ToString(), out index);

        return AddonSelectStringEvent.Select(index);
    }

    #endregion
    
    private class Config : ModuleConfig
    {
        public uint FertilizerSelected;
        public uint SeedSelected;
        public uint SoilSelected;
    }
}
