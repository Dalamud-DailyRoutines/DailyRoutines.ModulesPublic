using System.Collections.Frozen;
using DailyRoutines.Common.KamiToolKit.Addons;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.Lumina;
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

    private AutoStoreToCabinetAddon? addon;

    protected override void Init()
    {
        TaskHelper = new();

        addon ??= new(this)
        {
            InternalName          = "DRAutoStoreToCabinet",
            Title                 = Info.Title,
            Size                  = new(220f, 100f),
            RememberClosePosition = false
        };
    }

    protected override void Uninit()
    {
        addon?.Dispose();
        addon = null;
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

    private sealed class AutoStoreToCabinetAddon(AutoStoreToCabinet module) : AttachedAddon("Cabinet")
    {
        private TextButtonNode? startButton;
        private TextButtonNode? stopButton;

        protected override AttachedAddonPosition AttachPosition =>
            AttachedAddonPosition.LeftTop;

        protected override bool CanOpenAddon =>
            CabinetAddon != null && CabinetAddon->IsAddonAndNodesReady();

        protected override bool CanCloseHostAddon(AtkUnitBase* hostAddon) =>
            false;

        protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
        {
            if (WindowNode is WindowNode windowNode)
                windowNode.CloseButtonNode.IsVisible = false;

            FlagHelper.UpdateFlag(ref addon->Flags1A1, 0x4,  true);
            FlagHelper.UpdateFlag(ref addon->Flags1A0, 0x80, true);
            FlagHelper.UpdateFlag(ref addon->Flags1A1, 0x40, true);
            FlagHelper.UpdateFlag(ref addon->Flags1A3, 0x1,  true);

            var layout = new VerticalListNode
            {
                IsVisible   = true,
                Position    = ContentStartPosition,
                ItemSpacing = 4,
                Size        = ContentSize,
                FitContents = true
            };

            startButton = new()
            {
                IsVisible = true,
                IsEnabled = true,
                Size      = new(ContentSize.X - 4f, 32f),
                String    = Lang.Get("Start"),
                OnClick   = () =>
                {
                    var list = GetItemsToStoreToCabinet();
                    var cabinet = UIState.Instance()->Cabinet;
                    foreach (var item in list)
                    {
                        module.TaskHelper.Enqueue(() => cabinet.State != Cabinet.CabinetState.Requested);
                        module.TaskHelper.Enqueue(() => cabinet.StoreCabinetItem(item));
                    }
                }
            };

            stopButton = new()
            {
                IsVisible = true,
                IsEnabled = true,
                Size      = new(ContentSize.X - 4f, 32f),
                String    = Lang.Get("Stop"),
                OnClick   = module.TaskHelper.Abort
            };

            layout.AddNode(startButton);
            layout.AddNode(stopButton);

            layout.AttachNode(this);
            layout.RecalculateLayout();

            SetWindowSize(Size.X, ContentStartPosition.Y + layout.Height + 16f);
            layout.Position = ContentStartPosition;
            layout.Height   = layout.Height;
        }

        protected override void OnAttachedAddonUpdate(AtkUnitBase* addon, AtkUnitBase* hostAddon) =>
            RefreshState();

        protected override void OnHostAddon(AddonEvent type, AddonArgs? args)
        {
            if (type == AddonEvent.PreFinalize)
                module.TaskHelper.Abort();
        }

        private void RefreshState()
        {
            var isBusy = module.TaskHelper.IsBusy;

            if (startButton != null)
                startButton.IsEnabled = !isBusy;

            if (stopButton != null)
                stopButton.IsEnabled = isBusy;
        }
    }

    #region 常量

    // Item ID - Cabinet Index
    private static readonly FrozenDictionary<uint, uint> CabinetItems =
        LuminaGetter.Get<CabinetSheet>()
                    .Where(x => x.Item.RowId > 0)
                    .ToFrozenDictionary(x => x.Item.RowId, x => x.RowId);

    #endregion
}
