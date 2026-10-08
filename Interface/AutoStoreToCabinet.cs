using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.Lumina;
using OmenTools.KamiToolKit.Addons;
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

        addon = new(this)
        {
            InternalName = "DRAutoStoreToCabinet",
            Title        = Info.Title,
            Size         = new(220f, 100f)
        };
    }

    protected override void Uninit()
    {
        addon?.Dispose();
        addon = null;
    }

    private static List<uint> GetItemsToStoreToCabinet() =>
        Inventories.Player.TryGetItems
        (
            x =>
            {
                var itemID = x.GetBaseItemId();
                if (itemID == 0) return false;

                return CabinetItems.TryGetValue(itemID, out var index) &&
                       !UIState.Instance()->Cabinet.IsItemInCabinet(index);
            },
            out var items
        ) ?
            [.. items.Select(x => CabinetItems[x.GetBaseItemId()])] :
            [];

    private sealed class AutoStoreToCabinetAddon
    (
        AutoStoreToCabinet module
    ) : AttachedAddon("Cabinet")
    {
        private TextButtonNode? operationButton;

        protected override void OnSetup
        (
            AtkUnitBase*   addon,
            Span<AtkValue> atkValues
        )
        {
            var layout = new VerticalListNode
            {
                Position    = ContentStartPosition,
                ItemSpacing = 4,
                Size        = ContentSize,
                FitContents = true
            };

            operationButton = new()
            {
                Size        = ContentSize with { Y = 32f },
                String      = Lang.Get("Start"),
                TextureType = ButtonTextureType.ButtonB,
                OnClick = () =>
                {
                    if (module.TaskHelper.IsBusy)
                    {
                        module.TaskHelper.Abort();
                        return;
                    }

                    var list    = GetItemsToStoreToCabinet();
                    var cabinet = UIState.Instance()->Cabinet;

                    foreach (var item in list)
                    {
                        module.TaskHelper.Enqueue(() => cabinet.State != Cabinet.CabinetState.Requested);
                        module.TaskHelper.Enqueue(() => cabinet.StoreCabinetItem(item));
                    }
                }
            };

            layout.AddNode(operationButton);
            layout.AttachNode(this);

            SetWindowSize(Size.X, ContentStartPosition.Y + layout.Height + 16f);
        }

        protected override void OnUpdate
        (
            AtkUnitBase* addon
        )
        {
            operationButton?.String = module.TaskHelper.IsBusy ?
                                          Lang.Get("Stop") :
                                          Lang.Get("AutoStoreToCabinet-Button");

            base.OnUpdate(addon);
        }

        protected override void OnHostAddon
        (
            AddonEvent type,
            AddonArgs? args
        )
        {
            if (type == AddonEvent.PreFinalize)
            {
                operationButton = null;
                module.TaskHelper.Abort();
            }
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
