using System;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using DailyRoutines.Windows;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Colors;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace DailyRoutines.Modules;

public class AutoStoreToCabinet : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("AutoStoreToCabinetTitle"),
        Description = GetLoc("AutoStoreToCabinetDescription"),
        Category = ModuleCategories.UIOperation,
    };

    private static readonly List<InventoryType> ValidInventoryTypes =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3,
        InventoryType.Inventory4, InventoryType.ArmoryBody, InventoryType.ArmoryEar, InventoryType.ArmoryFeets,
        InventoryType.ArmoryHands, InventoryType.ArmoryHead, InventoryType.ArmoryLegs, InventoryType.ArmoryRings,
        InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings, InventoryType.ArmoryMainHand,
        InventoryType.ArmoryOffHand,
    ];

    private static Dictionary<uint, uint>? CabinetItems; // Item ID - Cabinet Index

    private static CancellationTokenSource? CancelSource;
    private static bool IsOnTask;

    private static unsafe AtkUnitBase* Cabinet => (AtkUnitBase*)DService.Gui.GetAddonByName("Cabinet");

    protected override void Init()
    {
        CabinetItems ??= LuminaGetter.Get<Cabinet>()
                                    .Where(x => x.Item.RowId > 0)
                                    .ToDictionary(x => x.Item.RowId, x => x.RowId);

        CancelSource ??= new();
        Overlay ??= new Overlay(this);

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "Cabinet", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "Cabinet", OnAddon);
    }

    protected override unsafe void OverlayPreDraw()
    {
        if (Cabinet == null)
            Overlay.IsOpen = false;
    }

    protected override void OverlayUI()
    {
        using (FontManager.UIFont.Push())
        {
            unsafe
            {
                var addon = Cabinet;
                var pos = new Vector2(addon->GetX() + 6, addon->GetY() - ImGui.GetWindowHeight() + 6);
                ImGui.SetWindowPos(pos);
            }

            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(ImGuiColors.DalamudYellow, Lang.Get("AutoStoreToCabinetTitle"));

            ImGui.SameLine();
            ImGui.Spacing();

            ImGui.SameLine();
            ImGui.BeginDisabled(IsOnTask);
            if (ImGui.Button(Lang.Get("Start")))
            {
                IsOnTask = true;
                DService.Framework.RunOnTick(async () =>
                {
                    try
                    {
                        var list = ScanValidCabinetItems();
                        if (list.Count > 0)
                        {
                            foreach (var item in list)
                            {
                                ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.StoreToCabinet, item);
                                await Task.Delay(100).ConfigureAwait(false);
                            }
                        }
                    }
                    finally
                    {
                        IsOnTask = false;
                    }
                }, cancellationToken: CancelSource.Token);
            }

            ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("Stop")))
            {
                CancelSource.Cancel();
                IsOnTask = false;
            }

            ImGuiOm.HelpMarker(Lang.Get("AutoStoreToCabinet-StoreHelp"));
        }
    }

    private static List<uint> ScanValidCabinetItems()
    {
        var list = new List<uint>();
        unsafe
        {
            var inventoryManager = InventoryManager.Instance();
            foreach (var inventory in ValidInventoryTypes)
            {
                var container = inventoryManager->GetInventoryContainer(inventory);
                if (container == null) continue;

                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null) continue;

                    var item = slot->ItemId;
                    if (item == 0) continue;

                    if (!CabinetItems.TryGetValue(item, out var index)) continue;

                    list.Add(index);
                }
            }
        }

        return list;
    }

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        Overlay.IsOpen = type switch
        {
            AddonEvent.PostSetup => true,
            AddonEvent.PreFinalize => false,
            _ => Overlay.IsOpen,
        };
    }

    protected override void Uninit()
    {
        CancelSource?.Cancel();
        CancelSource?.Dispose();
        CancelSource = null;

        base.Uninit();
    }
}
