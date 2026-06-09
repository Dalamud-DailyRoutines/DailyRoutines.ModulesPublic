using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using OmenTools.Info.Game.Data;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using CabinetSheet = Lumina.Excel.Sheets.Cabinet;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe class AutoStoreToCabinet : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoStoreToCabinetTitle"),
        Description = Lang.Get("AutoStoreToCabinetDescription"),
        Category    = ModuleCategory.Interface
    };

    protected override void Init()
    {
        Overlay = new(this);

        TaskHelper = new();

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "Cabinet", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "Cabinet", OnAddon);
    }

    protected override void Uninit() =>
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);

    // TODO: 改成原生的
    private void OnAddon(AddonEvent type, AddonArgs args) =>
        Overlay.IsOpen = type switch
        {
            AddonEvent.PostSetup   => true,
            AddonEvent.PreFinalize => false,
            _                      => Overlay.IsOpen
        };

    protected override void OverlayPreDraw()
    {
        if (CabinetAddon == null)
            Overlay.IsOpen = false;
    }

    protected override void OverlayUI()
    {
        var addon = CabinetAddon;
        var pos   = new Vector2(addon->GetX() + 6, addon->GetY() - ImGui.GetWindowHeight() + 6);
        ImGui.SetWindowPos(pos);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("AutoStoreToCabinetTitle"));

        ImGui.SameLine();
        ImGui.Spacing();

        var cabinet = UIState.Instance()->Cabinet;

        ImGui.SameLine();
        using (ImRaii.Disabled(TaskHelper.IsBusy))
        {
            if (ImGui.Button(Lang.Get("Start")))
            {
                var list = GetItemsToStoreToCabinet();
                foreach (var item in list)
                {
                    TaskHelper.Enqueue(() => cabinet.State != Cabinet.CabinetState.Requested);
                    TaskHelper.Enqueue(() => cabinet.StoreCabinetItem(item));
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button(Lang.Get("Stop")))
            TaskHelper.Abort();
    }

    private static List<uint> GetItemsToStoreToCabinet() =>
        Inventories.PlayerWithArmory.TryGetItems
        (
            x =>
            {
                var itemID = x.GetBaseItemId();
                if (itemID == 0) return false;

                return CabinetItems.TryGetValue(itemID, out var index) &&
                       !UIState.Instance()->Cabinet.IsItemInCabinet(index);
            },
            out var items
        )
            ? items.Select(x => CabinetItems[x.GetBaseItemId()]).ToList()
            : [];

    #region 常量

    // Item ID - Cabinet Index
    private static readonly FrozenDictionary<uint, uint> CabinetItems =
        LuminaGetter.Get<CabinetSheet>()
                    .Where(x => x.Item.RowId > 0)
                    .ToFrozenDictionary(x => x.Item.RowId, x => x.RowId);

    #endregion
}
