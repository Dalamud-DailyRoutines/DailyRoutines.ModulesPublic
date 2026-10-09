using System.Runtime.InteropServices;
using DailyRoutines.Common.Info;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoSortItems : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoSortItemsTitle"),
        Description = Lang.Get("AutoSortItemsDescription"),
        Category    = ModuleCategory.System,
        Author      = ["那年雪落"]
    };

    private static readonly CompSig AddSortConditionSig = new("40 55 48 8D 6C 24 ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 45 ?? 83 79");
    private delegate byte AddSortConditionDelegate
    (
        ItemOrderModuleSorter* sorter,
        int                    condition,
        byte                   descending
    );
    private AddSortConditionDelegate AddSortCondition = null!;

    private static readonly CompSig StartSortSig = new("48 89 5C 24 ?? 48 89 6C 24 ?? 56 48 83 EC ?? 48 8B 41 ?? 0F B6 EA");
    private delegate void StartSortDelegate
    (
        ItemOrderModuleSorter* sorter,
        byte                   savePreviousOrder
    );
    private StartSortDelegate StartSort = null!;

    private static readonly CompSig SeparateTabsSig =
        new("48 89 5C 24 ?? 56 48 83 EC ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 44 24 ?? 48 8B 11");
    private nint separateTabs;

    private Config             config = null!;
    private SortSettingsMenu?  settingsMenu;
    private SortSettingsAddon? settingsAddon;

    private int  sorterIndex;
    private long readyAt;
    private bool waitingForSort;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        AddSortCondition = AddSortConditionSig.GetDelegate<AddSortConditionDelegate>();
        StartSort        = StartSortSig.GetDelegate<StartSortDelegate>();
        separateTabs     = SeparateTabsSig.ScanText();

        if (separateTabs == 0)
            throw new InvalidOperationException("无法找到原生物品整理函数");

        settingsAddon = new(this)
        {
            InternalName = "DRAutoSortItemsSettings",
            Title        = Lang.Get("AutoSortItems-Addon"),
            Size         = new(450, 500)
        };

        settingsMenu = new(this);
        ContextMenuManager.Instance().Reg(settingsMenu);

        IClientState.Instance().TerritoryChanged += OnTerritoryChanged;
        IClientState.Instance().Logout           += OnLogout;

        if (GameState.IsLoggedIn)
            RequestSort();
    }

    protected override void Uninit()
    {
        IClientState.Instance().TerritoryChanged -= OnTerritoryChanged;
        IClientState.Instance().Logout           -= OnLogout;
        FrameworkManager.Instance().Unreg(OnUpdate);
        if (settingsMenu != null)
            ContextMenuManager.Instance().Unreg(settingsMenu);

        settingsAddon?.Dispose();
        settingsAddon = null;
    }

    private void OnTerritoryChanged
    (
        uint territoryType
    ) =>
        RequestSort();

    private void OnLogout
    (
        int type,
        int code
    )
    {
        FrameworkManager.Instance().Unreg(OnUpdate);
        settingsAddon.Close();
        readyAt        = 0;
        waitingForSort = false;
    }

    private void OnUpdate
    (
        IFramework framework
    )
    {
        if (!GameState.IsLoggedIn)
        {
            FrameworkManager.Instance().Unreg(OnUpdate);
            return;
        }

        if (GameState.TerritoryType == 0 ||
            GameState.Map           == 0 ||
            !UIModule.IsScreenReady()    ||
            !ICondition.Instance().IsIdle)
        {
            readyAt = 0;
            return;
        }

        if (GameState.IsInPVPArea ||
            GameState.ContentFinderCondition != 0)
        {
            FrameworkManager.Instance().Unreg(OnUpdate);
            return;
        }

        var module = ItemOrderModule.Instance();

        if (module == null || module->CharacterContentId == 0)
        {
            readyAt = 0;
            return;
        }

        if (sorterIndex > module->ArmourySorter.Length)
        {
            FrameworkManager.Instance().Unreg(OnUpdate);
            return;
        }

        var sorter = sorterIndex == 0 ?
                         module->InventorySorter :
                         module->ArmourySorter[sorterIndex - 1].Value;

        if (!IsSorterReady
            (
                sorter,
                sorterIndex == 0 ?
                    4 :
                    1
            ))
        {
            readyAt = 0;
            return;
        }

        if (sorter->SortFunctionIndex >= 0)
            return;

        if (waitingForSort)
        {
            waitingForSort = false;
            sorterIndex++;
            return;
        }

        var now = Environment.TickCount64;
        if (readyAt == 0)
            readyAt = now + 750;
        if (now < readyAt)
            return;

        sorter->SortFunctions.Clear();

        var success = AddSortCondition(sorter, (int)SortCondition.GroupItems, 0) != 0;

        if (sorterIndex == 0)
        {
            success &= AddSortCondition(sorter, (int)SortCondition.Quality,   (byte)(1 - config.InventoryHQ))        != 0;
            success &= AddSortCondition(sorter, (int)SortCondition.ItemID,    (byte)(1 - config.InventoryID))        != 0;
            success &= AddSortCondition(sorter, (int)SortCondition.ItemLevel, (byte)(1 - config.InventoryItemLevel)) != 0;
            success &= AddSortCondition(sorter, (int)SortCondition.Category,  (byte)(1 - config.InventoryCategory))  != 0;

            if (config.InventoryTab == 0)
            {
                ((SorterCallbacks*)sorter)->SeparateTabs = separateTabs;

                success &= AddSortCondition(sorter, (int)SortCondition.SeparateTabs, 0) != 0;
            }
        }
        else
        {
            success &= AddSortCondition(sorter, (int)SortCondition.ItemID,    (byte)(1 - config.ArmouryChestID))   != 0;
            success &= AddSortCondition(sorter, (int)SortCondition.ItemLevel, (byte)(1 - config.ArmouryItemLevel)) != 0;
            success &= AddSortCondition(sorter, (int)SortCondition.Category,  (byte)(1 - config.ArmouryCategory))  != 0;
        }

        if (!success)
        {
            sorter->SortFunctions.Clear();
            FrameworkManager.Instance().Unreg(OnUpdate);
            throw new InvalidOperationException("无法设置原生物品整理规则");
        }

        StartSort(sorter, 0);
        waitingForSort = true;
    }

    private void RequestSort()
    {
        sorterIndex    = 0;
        readyAt        = 0;
        waitingForSort = false;
        FrameworkManager.Instance().Reg(OnUpdate, 100);
    }

    private static bool IsSorterReady
    (
        ItemOrderModuleSorter* sorter,
        int                    pageCount
    )
    {
        if (sorter               == null ||
            sorter->ItemsPerPage <= 0)
            return false;

        var manager = InventoryManager.Instance();
        if (manager == null)
            return false;

        var slotCount = 0;

        for (var page = 0; page < pageCount; page++)
        {
            var container = manager->GetInventoryContainer(sorter->InventoryType + (uint)page);
            if (container == null        ||
                !container->IsLoaded     ||
                container->Items == null ||
                container->Size  != sorter->ItemsPerPage)
                return false;

            slotCount += container->Size;
        }

        return sorter->Items.Count == slotCount;
    }

    private sealed class SortSettingsMenu
    (
        AutoSortItems module
    ) : ContextMenuEntry
    {
        public override string Identifier => nameof(AutoSortItems);

        public override ContextMenuItem? Create
        (
            ContextMenuOpenedArgs args
        )
        {
            if (args.TargetInventoryItem is null)
                return null;

            return new()
            {
                Name      = Lang.Get("AutoSortItems-ContextMenu"),
                OnClicked = _ => module.settingsAddon.Open()
            };
        }
    }

    private sealed class SortSettingsAddon
    (
        AutoSortItems module
    ) : NativeAddon
    {
        protected override void OnSetup
        (
            AtkUnitBase*   addon,
            Span<AtkValue> atkValues
        )
        {
            var layout = new VerticalListNode
            {
                Position         = ContentStartPosition,
                Width            = ContentSize.X,
                ItemSpacing      = 4,
                FirstItemSpacing = 4f,
                FitContents      = true
            };
            layout.AttachNode(this);

            // 兵装库
            var armoryText = new TextNode
            {
                TextFlags = TextFlags.AutoAdjustNodeSize,
                String    = LuminaWrapper.GetAddonText(12210),
                FontSize  = 16
            };
            AtkColors.Text.ApplyTo(armoryText);
            layout.AddNode(armoryText);

            layout.AddDummy();

            string[] directions = [Lang.Get("Descending"), Lang.Get("Ascending")];
            var      typeText   = LuminaWrapper.GetAddonText(9448);

            AddRow(layout, Lang.Get("ID"),    module.config.ArmouryChestID,   directions, value => module.config.ArmouryChestID   = value);
            AddRow(layout, Lang.Get("Level"), module.config.ArmouryItemLevel, directions, value => module.config.ArmouryItemLevel = value);
            AddRow
            (
                layout,
                typeText,
                module.config.ArmouryCategory,
                directions,
                value => module.config.ArmouryCategory = value,
                Lang.Get("AutoSortItems-ArmouryCategoryDesc")
            );

            layout.AddDummy();

            // 背包
            var inventoryText = new TextNode
            {
                TextFlags = TextFlags.AutoAdjustNodeSize,
                String    = LuminaWrapper.GetAddonText(12209),
                FontSize  = 16
            };
            AtkColors.Text.ApplyTo(inventoryText);
            layout.AddNode(inventoryText);

            layout.AddDummy();

            AddRow(layout, Lang.Get("HQ"),    module.config.InventoryHQ,        directions, value => module.config.InventoryHQ        = value);
            AddRow(layout, Lang.Get("ID"),    module.config.InventoryID,        directions, value => module.config.InventoryID        = value);
            AddRow(layout, Lang.Get("Level"), module.config.InventoryItemLevel, directions, value => module.config.InventoryItemLevel = value);
            AddRow
            (
                layout,
                typeText,
                module.config.InventoryCategory,
                directions,
                value => module.config.InventoryCategory = value,
                Lang.Get("AutoSortItems-InventoryCategoryDesc")
            );
            AddRow
            (
                layout,
                Lang.Get("AutoSortItems-Split"),
                module.config.InventoryTab,
                [Lang.Get("AutoSortItems-Split"), Lang.Get("AutoSortItems-Merge")],
                value => module.config.InventoryTab = value,
                Lang.Get("AutoSortItems-InventoryTabDesc")
            );


            var separator = new HorizontalLineNode
            {
                Size = ContentSize with { Y = 2 }
            };
            layout.AddDummy();
            layout.AddNode(separator);
            layout.AddDummy();

            var manualButton = new TextButtonNode
            {
                String      = LuminaWrapper.GetAddonText(1389),
                Size        = ContentSize with { Y = 36 },
                TextureType = ButtonTextureType.ButtonB,
                OnClick     = module.RequestSort
            };
            layout.AddNode(manualButton);

            SetWindowSize(Size.X, ContentStartPosition.Y + layout.Height + 16f);
        }

        private void AddRow
        (
            VerticalListNode layout,
            string           label,
            int              value,
            string[]         options,
            Action<int>      onChanged,
            string           note = ""
        )
        {
            const float ITEM_SPACING   = 10;
            const float DROPDOWN_WIDTH = 175;

            var row = new HorizontalListNode
            {
                Size             = ContentSize with { Y = 30 },
                ItemSpacing      = ITEM_SPACING,
                FirstItemSpacing = ITEM_SPACING
            };

            var labelNode = new TextNode
            {
                String        = label,
                TextFlags     = TextFlags.Ellipsis,
                AlignmentType = AlignmentType.Left,
                Size          = new(ContentSize.X - DROPDOWN_WIDTH - (2 * ITEM_SPACING), 30),
                FontSize      = 14
            };
            row.AddNode(labelNode);

            var dropDownNode = new DropDownNode<int>
            {
                Size             = new(DROPDOWN_WIDTH, 30),
                GetLabelFunction = index => options[index],
                TextTooltip      = note,
                Options          = [0, 1],
                SelectedOption   = value,
                OnOptionSelected = selected =>
                {
                    onChanged(selected);
                    module.config.Save(module);
                }
            };
            row.AddNode(dropDownNode);

            layout.AddNode(row);
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x68)]
    private struct SorterCallbacks
    {
        [FieldOffset(0x30)]
        public nint SeparateTabs;
    }

    private enum SortCondition
    {
        GroupItems   = 1,
        ItemID       = 2,
        Category     = 3,
        SeparateTabs = 4,
        ItemLevel    = 6,
        Quality      = 10
    }

    private class Config : ModuleConfig
    {
        public int ArmouryCategory;
        public int ArmouryChestID;
        public int ArmouryItemLevel;
        public int InventoryCategory;
        public int InventoryHQ;
        public int InventoryID;
        public int InventoryItemLevel;
        public int InventoryTab;
    }
}
