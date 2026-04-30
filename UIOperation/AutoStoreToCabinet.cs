using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.Data;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoStoreToCabinet : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoStoreToCabinetTitle"),
        Description = Lang.Get("AutoStoreToCabinetDescription"),
        Category    = ModuleCategory.UIOperation
    };

    private readonly CancellationTokenSource cancelSource = new();

    private bool isOnTask;

    protected override void Init()
    {
        Overlay = new(this);

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "Cabinet", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "Cabinet", OnAddon);
    }
    
    protected override void Uninit()
    {
        cancelSource.Cancel();
        cancelSource.Dispose();
        
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
    }
    
    // TODO: 改成原生的
    private void OnAddon(AddonEvent type, AddonArgs args) =>
        Overlay.IsOpen = type switch
        {
            AddonEvent.PostSetup   => true,
            AddonEvent.PreFinalize => false,
            _                      => Overlay.IsOpen
        };

    protected override unsafe void OverlayPreDraw()
    {
        if (CabinetAddon == null)
            Overlay.IsOpen = false;
    }

    protected override void OverlayUI()
    {
        using var font = FontManager.Instance().UIFont.Push();

        unsafe
        {
            var addon = CabinetAddon;
            var pos   = new Vector2(addon->GetX() + 6, addon->GetY() - ImGui.GetWindowHeight() + 6);
            ImGui.SetWindowPos(pos);
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("AutoStoreToCabinetTitle"));

        ImGui.SameLine();
        ImGui.Spacing();

        ImGui.SameLine();
        ImGui.BeginDisabled(isOnTask);

        if (ImGui.Button(Lang.Get("Start")))
        {
            isOnTask = true;
            DService.Instance().Framework.RunOnTick
            (
                async () =>
                {
                    try
                    {
                        var list = ScanValidCabinetItems();

                        if (list.Count > 0)
                        {
                            foreach (var item in list)
                            {
                                ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.StoreToCabinet, item);
                                ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.InventoryRefresh);
                                await Task.Delay(100).ConfigureAwait(false);
                            }
                        }
                    }
                    finally
                    {
                        isOnTask = false;
                    }
                },
                cancellationToken: cancelSource.Token
            );
        }

        ImGui.EndDisabled();

        ImGui.SameLine();

        if (ImGui.Button(Lang.Get("Stop")))
        {
            cancelSource.Cancel();
            isOnTask = false;
        }

        ImGuiOm.HelpMarker(Lang.Get("AutoStoreToCabinet-StoreHelp"));

    }

    private static List<uint> ScanValidCabinetItems()
    {
        var list = new List<uint>();

        unsafe
        {
            var inventoryManager = InventoryManager.Instance();

            foreach (var inventory in Inventories.PlayerWithArmory)
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
    
    #region 常量

    // Item ID - Cabinet Index
    private static readonly FrozenDictionary<uint, uint> CabinetItems =
        LuminaGetter.Get<Cabinet>()
                    .Where(x => x.Item.RowId > 0)
                    .ToFrozenDictionary(x => x.Item.RowId, x => x.RowId);

    #endregion
}
