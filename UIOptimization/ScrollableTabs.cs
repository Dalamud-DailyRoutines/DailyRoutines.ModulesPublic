using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;
using OmenTools.Interop.Game.Helpers;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class ScrollableTabs : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("ScrollableTabsTitle"),
        Description = Lang.Get("ScrollableTabsDescription"),
        Category    = ModuleCategory.UIOptimization,
        Author      = ["Cyf5119"]
    };
    
    private bool IsNext =>
        wheelState == (!config.Invert ? 1 : -1);

    private bool IsPrev =>
        wheelState == (!config.Invert ? -1 : 1);
    
    private static AtkCollisionNode* IntersectingCollisionNode =>
        RaptureAtkModule.Instance()->AtkCollisionManager.IntersectingCollisionNode;
    
    private delegate void AddonUpdateHandler(AtkUnitBase* unitBase);

    private Config config = null!;
    private int    wheelState;
    
    private readonly Dictionary<string, AddonUpdateHandler> uiHandlerMapping = [];
    private readonly Dictionary<string, string>             uiNameMapping    = [];

    public ScrollableTabs()
    {
        InitUINameMapping();
        InitUIHandlerMapping();

        return;
        
        void InitUINameMapping()
        {
            var directUseNames = new[]
            {
                "AetherCurrent", "ArmouryBoard", "AOZNotebook", "OrnamentNoteBook",
                "MYCWarResultNotebook", "FishGuide2", "GSInfoCardList", "GSInfoEditDeck",
                "LovmPaletteEdit", "Inventory", "InventoryLarge", "InventoryExpansion",
                "MinionNoteBook", "MountNoteBook", "InventoryRetainer", "InventoryRetainerLarge",
                "FateProgress", "AdventureNoteBook", "MJIMinionNoteBook", "InventoryBuddy",
                "InventoryBuddy2", "Character", "CharacterClass", "CharacterRepute",
                "Buddy", "MiragePrismPrismBox", "InventoryEvent", "Currency"
            };

            foreach (var name in directUseNames)
                uiNameMapping[name] = name;

            // Inventory
            foreach (var name in new[] { "InventoryGrid", "InventoryGridCrystal" })
                uiNameMapping[name] = "Inventory";

            // InventoryEvent
            foreach (var name in new[] { "InventoryEventGrid" })
                uiNameMapping[name] = "InventoryEvent";

            // InventoryLarge / InventoryExpansion
            uiNameMapping["InventoryCrystalGrid"] = "InventoryLarge";

            // InventoryLarge 相关映射
            foreach (var name in new[] { "InventoryEventGrid0", "InventoryEventGrid1", "InventoryEventGrid2", "InventoryGrid0", "InventoryGrid1" })
                uiNameMapping[name] = "InventoryLarge";

            // InventoryExpansion
            foreach (var name in new[]
                     {
                         "InventoryEventGrid0E", "InventoryEventGrid1E", "InventoryEventGrid2E", "InventoryGrid0E", "InventoryGrid1E", "InventoryGrid2E",
                         "InventoryGrid3E"
                     })
                uiNameMapping[name] = "InventoryExpansion";

            // InventoryRetainer
            foreach (var name in new[] { "RetainerGridCrystal", "RetainerGrid" })
                uiNameMapping[name] = "InventoryRetainer";

            // InventoryRetainerLarge
            foreach (var name in new[] { "RetainerCrystalGrid", "RetainerGrid0", "RetainerGrid1", "RetainerGrid2", "RetainerGrid3", "RetainerGrid4" })
                uiNameMapping[name] = "InventoryRetainerLarge";

            // Character
            foreach (var name in new[] { "CharacterStatus", "CharacterProfile" })
                uiNameMapping[name] = "Character";

            // Buddy
            foreach (var name in new[] { "BuddyAction", "BuddySkill", "BuddyAppearance" })
                uiNameMapping[name] = "Buddy";
        }

        void InitUIHandlerMapping()
        {
            uiHandlerMapping["ArmouryBoard"] = addon => UpdateArmouryBoard((AddonArmouryBoard*)addon);

            // Inventory
            uiHandlerMapping["Inventory"]          = addon => UpdateInventory((AddonInventory*)addon);
            uiHandlerMapping["InventoryEvent"]     = addon => UpdateInventoryEvent((AddonInventoryEvent*)addon);
            uiHandlerMapping["InventoryLarge"]     = addon => UpdateInventoryLarge((AddonInventoryLarge*)addon);
            uiHandlerMapping["InventoryExpansion"] = addon => UpdateInventoryExpansion((AddonInventoryExpansion*)addon);

            // Retainer
            uiHandlerMapping["InventoryRetainer"]      = addon => UpdateInventoryRetainer((AddonInventoryRetainer*)addon);
            uiHandlerMapping["InventoryRetainerLarge"] = addon => UpdateInventoryRetainerLarge((AddonInventoryRetainerLarge*)addon);

            // NoteBook
            uiHandlerMapping["MinionNoteBook"] = addon => UpdateMountMinion((AddonMinionMountBase*)addon);
            uiHandlerMapping["MountNoteBook"]  = addon => UpdateMountMinion((AddonMinionMountBase*)addon);

            // TabController
            uiHandlerMapping["FishGuide2"]        = addon => UpdateTabController(addon, &((AddonFishGuide2*)addon)->TabController);
            uiHandlerMapping["AdventureNoteBook"] = addon => UpdateTabController(addon, &((AddonAdventureNoteBook*)addon)->TabController);
            uiHandlerMapping["OrnamentNoteBook"]  = addon => UpdateTabController(addon, &((AddonOrnamentNoteBook*)addon)->TabController);
            uiHandlerMapping["GSInfoCardList"]    = addon => UpdateTabController(addon, &((AddonGSInfoCardList*)addon)->TabController);
            uiHandlerMapping["GSInfoEditDeck"]    = addon => UpdateTabController(addon, &((AddonGSInfoEditDeck*)addon)->TabController);
            uiHandlerMapping["LovmPaletteEdit"]   = addon => UpdateTabController(addon, &((AddonLovmPaletteEdit*)addon)->TabController);

            // 其他
            uiHandlerMapping["AOZNotebook"]          = addon => UpdateAOZNotebook((AddonAOZNotebook*)addon);
            uiHandlerMapping["AetherCurrent"]        = addon => UpdateAetherCurrent((AddonAetherCurrent*)addon);
            uiHandlerMapping["FateProgress"]         = addon => UpdateFateProgress((AddonFateProgress*)addon);
            uiHandlerMapping["MYCWarResultNotebook"] = addon => UpdateFieldNotes((AddonMYCWarResultNotebook*)addon);
            uiHandlerMapping["MJIMinionNoteBook"]    = addon => UpdateMJIMinionNoteBook((AddonMJIMinionNoteBook*)addon);
            uiHandlerMapping["InventoryBuddy"]       = addon => UpdateInventoryBuddy((AddonInventoryBuddy*)addon);
            uiHandlerMapping["InventoryBuddy2"]      = addon => UpdateInventoryBuddy((AddonInventoryBuddy*)addon);
            uiHandlerMapping["Buddy"]                = addon => UpdateBuddy((AddonBuddy*)addon);
            uiHandlerMapping["MiragePrismPrismBox"]  = addon => UpdateMiragePrismPrismBox((AddonMiragePrismPrismBox*)addon);

            // Currency
            uiHandlerMapping["Currency"] = addon => UpdateCurrency((AddonCurrency*)addon);

            // Character
            uiHandlerMapping["Character"]       = addon => UpdateCharacter((AddonCharacter*)addon);
            uiHandlerMapping["CharacterClass"]  = HandleCharacterUI;
            uiHandlerMapping["CharacterRepute"] = HandleCharacterUI;
        }
    }

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        FrameworkManager.Instance().Reg(OnUpdate);
    }
    
    protected override void Uninit() =>
        FrameworkManager.Instance().Unreg(OnUpdate);

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("ScrollableTabs-Invert"), ref config.Invert))
            config.Save(this);
    }
    
    private void OnUpdate(IFramework _)
    {
        if (!DService.Instance().ClientState.IsLoggedIn)
            return;

        wheelState = Math.Clamp(UIInputData.Instance()->CursorInputs.MouseWheel, -1, 1);
        if (wheelState == 0)
            return;

        if (config.Invert)
            wheelState *= -1;

        var hoveredUnitBase = RaptureAtkModule.Instance()->AtkCollisionManager.IntersectingAddon;

        if (hoveredUnitBase == null)
        {
            wheelState = 0;
            return;
        }

        var originalName = hoveredUnitBase->NameString;

        if (string.IsNullOrEmpty(originalName))
        {
            wheelState = 0;
            return;
        }

        if (!uiNameMapping.TryGetValue(originalName, out var mappedName))
        {
            wheelState = 0;
            return;
        }

        // InventoryCrystalGrid
        if (originalName == "InventoryCrystalGrid"                                                                            &&
            DService.Instance().GameConfig.UiConfig.TryGet("ItemInventryWindowSizeType", out uint itemInventryWindowSizeType) &&
            itemInventryWindowSizeType == 2)
            mappedName = "InventoryExpansion";

        if (!AddonHelper.TryGetByName(mappedName, out var unitBase))
        {
            wheelState = 0;
            return;
        }

        if (uiHandlerMapping.TryGetValue(mappedName, out var handler))
            handler(unitBase);

        wheelState = 0;
    }

    private void HandleCharacterUI(AtkUnitBase* unitBase)
    {
        var name           = unitBase->NameString;
        var addonCharacter = name == "Character" ? (AddonCharacter*)unitBase : AddonHelper.GetByName<AddonCharacter>("Character");

        if (addonCharacter == null                             ||
            !addonCharacter->AddonControl.IsChildSetupComplete ||
            IntersectingCollisionNode == addonCharacter->PreviewController.CollisionNode)
        {
            wheelState = 0;
            return;
        }

        switch (name)
        {
            case "Character":
                UpdateCharacter(addonCharacter);
                break;
            case "CharacterClass":
                UpdateCharacterClass(addonCharacter, (AddonCharacterClass*)unitBase);
                break;
            case "CharacterRepute":
                UpdateCharacterRepute(addonCharacter, (AddonCharacterRepute*)unitBase);
                break;
        }
    }

    private int GetTabIndex(int currentTabIndex, int numTabs) => 
        Math.Clamp(currentTabIndex + wheelState, 0, numTabs - 1);

    private void UpdateArmouryBoard(AddonArmouryBoard* addon)
    {
        var tabIndex = GetTabIndex(addon->TabIndex, NUM_ARMOURY_BOARD_TABS);

        if (addon->TabIndex < tabIndex)
            addon->NextTab(0);
        else if (addon->TabIndex > tabIndex)
            addon->PreviousTab(0);
    }

    private void UpdateInventory(AddonInventory* addon)
    {
        if (addon->TabIndex == NUM_INVENTORY_TABS - 1 && wheelState > 0)
            addon->AtkUnitBase.Callback(22, *(int*)((nint)addon + 0x228), 0);
        else
        {
            var tabIndex = GetTabIndex(addon->TabIndex, NUM_INVENTORY_TABS);

            if (addon->TabIndex == tabIndex)
                return;

            addon->SetTab(tabIndex);
        }
    }

    private void UpdateInventoryEvent(AddonInventoryEvent* addon)
    {
        if (addon->TabIndex == 0 && wheelState < 0)
            addon->AtkUnitBase.Callback(22, *(int*)((nint)addon + 0x228), 2);
        else
        {
            var numEnabledButtons = 0;

            foreach (ref var button in addon->Buttons)
            {
                if ((button.Value->AtkComponentButton.Flags & 0x40000) != 0)
                    numEnabledButtons++;
            }

            var tabIndex = GetTabIndex(addon->TabIndex, numEnabledButtons);

            if (addon->TabIndex == tabIndex)
                return;

            addon->SetTab(tabIndex);
        }
    }

    private void UpdateInventoryLarge(AddonInventoryLarge* addon)
    {
        var tabIndex = GetTabIndex(addon->TabIndex, NUM_INVENTORY_LARGE_TABS);

        if (addon->TabIndex == tabIndex)
            return;

        addon->SetTab(tabIndex);
    }

    private void UpdateInventoryExpansion(AddonInventoryExpansion* addon)
    {
        var tabIndex = GetTabIndex(addon->TabIndex, NUM_INVENTORY_EXPANSION_TABS);

        if (addon->TabIndex == tabIndex)
            return;

        addon->SetTab(tabIndex, false);
    }

    private void UpdateInventoryRetainer(AddonInventoryRetainer* addon)
    {
        var tabIndex = GetTabIndex(addon->TabIndex, NUM_INVENTORY_RETAINER_TABS);

        if (addon->TabIndex == tabIndex)
            return;

        addon->SetTab(tabIndex);
    }

    private void UpdateInventoryRetainerLarge(AddonInventoryRetainerLarge* addon)
    {
        var tabIndex = GetTabIndex(addon->TabIndex, NUM_INVENTORY_RETAINER_LARGE_TABS);

        if (addon->TabIndex == tabIndex)
            return;

        addon->SetTab(tabIndex);
    }

    private void UpdateTabController(AtkUnitBase* addon, TabController* tabController)
    {
        var tabIndex = GetTabIndex(tabController->TabIndex, tabController->TabCount);

        if (tabController->TabIndex == tabIndex)
            return;

        tabController->TabIndex = tabIndex;
        tabController->CallbackFunction(tabIndex, addon);
    }

    private void UpdateAOZNotebook(AddonAOZNotebook* addon)
    {
        var tabIndex = GetTabIndex(addon->TabIndex, addon->TabCount);

        if (addon->TabIndex == tabIndex)
            return;

        addon->SetTab(tabIndex, true);
    }

    private void UpdateAetherCurrent(AddonAetherCurrent* addon)
    {
        var tabIndex = GetTabIndex(addon->TabIndex, addon->TabCount);
        if (addon->TabIndex == tabIndex) return;

        addon->SetTab(tabIndex);

        for (var i = 0; i < addon->Tabs.Length; i++)
            addon->Tabs[i].Value->IsSelected = i == tabIndex;
    }

    private void UpdateFateProgress(AddonFateProgress* addon)
    {
        var tabIndex = GetTabIndex(addon->TabIndex, addon->TabCount);
        if (!addon->IsLoaded || addon->TabIndex == tabIndex)
            return;

        var atkEvent = new AtkEvent();
        addon->SetTab(tabIndex, &atkEvent);
    }

    private void UpdateFieldNotes(AddonMYCWarResultNotebook* addon)
    {
        if (IntersectingCollisionNode == addon->DescriptionCollisionNode)
            return;

        var atkEvent   = new AtkEvent();
        var eventParam = Math.Clamp(addon->CurrentNoteIndex % 10 + wheelState, -1, addon->MaxNoteIndex - 1);

        if (eventParam == -1)
        {
            if (addon->CurrentPageIndex > 0)
            {
                var page = addon->CurrentPageIndex                             - 1;
                addon->AtkUnitBase.ReceiveEvent(AtkEventType.ButtonClick, page + 10, &atkEvent);
                addon->AtkUnitBase.ReceiveEvent(AtkEventType.ButtonClick, 9,         &atkEvent);
            }
        }
        else if (eventParam == 10)
        {
            if (addon->CurrentPageIndex < 4)
            {
                var page = addon->CurrentPageIndex                             + 1;
                addon->AtkUnitBase.ReceiveEvent(AtkEventType.ButtonClick, page + 10, &atkEvent);
            }
        }
        else
            addon->AtkUnitBase.ReceiveEvent(AtkEventType.ButtonClick, eventParam, &atkEvent);
    }

    private void UpdateMountMinion(AddonMinionMountBase* addon)
    {
        switch (addon->CurrentView)
        {
            case AddonMinionMountBase.ViewType.Normal when addon->TabController.TabIndex == 0 && wheelState < 0:
                addon->SwitchToFavorites();
                break;
            case AddonMinionMountBase.ViewType.Normal:
                UpdateTabController((AtkUnitBase*)addon, &addon->TabController);
                break;
            case AddonMinionMountBase.ViewType.Favorites when wheelState > 0:
                addon->TabController.CallbackFunction(0, (AtkUnitBase*)addon);
                break;
        }
    }

    private void UpdateMJIMinionNoteBook(AddonMJIMinionNoteBook* addon)
    {
        var agent = AgentMJIMinionNoteBook.Instance();

        if (agent->CurrentView == AgentMJIMinionNoteBook.ViewType.Normal)
        {
            if (addon->TabController.TabIndex == 0 && wheelState < 0)
            {
                agent->CurrentView                      = AgentMJIMinionNoteBook.ViewType.Favorites;
                agent->SelectedFavoriteMinion.TabIndex  = 0;
                agent->SelectedFavoriteMinion.SlotIndex = agent->SelectedNormalMinion.SlotIndex;
                agent->SelectedFavoriteMinion.MinionId  = agent->GetSelectedMinionId();
                agent->SelectedMinion                   = &agent->SelectedFavoriteMinion;
                agent->HandleCommand(0x407);
            }
            else
            {
                UpdateTabController((AtkUnitBase*)addon, &addon->TabController);
                agent->HandleCommand(0x40B);
            }
        }
        else if (agent->CurrentView == AgentMJIMinionNoteBook.ViewType.Favorites && wheelState > 0)
        {
            agent->CurrentView                    = AgentMJIMinionNoteBook.ViewType.Normal;
            agent->SelectedNormalMinion.TabIndex  = 0;
            agent->SelectedNormalMinion.SlotIndex = agent->SelectedFavoriteMinion.SlotIndex;
            agent->SelectedNormalMinion.MinionId  = agent->GetSelectedMinionId();
            agent->SelectedMinion                 = &agent->SelectedNormalMinion;

            addon->TabController.TabIndex = 0;
            addon->TabController.CallbackFunction(0, (AtkUnitBase*)addon);

            agent->HandleCommand(0x40B);
        }
    }

    private void UpdateInventoryBuddy(AddonInventoryBuddy* addon)
    {
        if (!PlayerState.Instance()->HasPremiumSaddlebag)
            return;

        var tabIndex = GetTabIndex(addon->TabIndex, 2);

        if (addon->TabIndex == tabIndex)
            return;

        addon->SetTab((byte)tabIndex);
    }

    private void UpdateCurrency(AddonCurrency* addon)
    {
        var atkStage    = AtkStage.Instance();
        var numberArray = atkStage->GetNumberArrayData(NumberArrayType.Currency);
        var currentTab  = numberArray->IntArray[0];
        var newTab      = currentTab;

        var enableStates = new bool[addon->Tabs.Length];
        for (var i = 0; i < addon->Tabs.Length; i++)
            enableStates[i] = addon->Tabs[i].Value != null && addon->Tabs[i].Value->IsEnabled;


        if (wheelState > 0 && currentTab < enableStates.Length)
        {
            for (var i = currentTab + 1; i < enableStates.Length; i++)
                if (enableStates[i])
                {
                    newTab = i;
                    break;
                }
        }
        else if (currentTab > 0)
        {
            for (var i = currentTab - 1; i >= 0; i--)
                if (enableStates[i])
                {
                    newTab = i;
                    break;
                }
        }

        if (currentTab == newTab)
            return;

        numberArray->SetValue(0, newTab);
        addon->AtkUnitBase.OnRequestedUpdate(atkStage->GetNumberArrayData(), atkStage->GetStringArrayData());
    }

    private void UpdateBuddy(AddonBuddy* addon)
    {
        var tabIndex = GetTabIndex(addon->TabIndex, NUM_BUDDY_TABS);

        if (addon->TabIndex == tabIndex)
            return;

        addon->SetTab(tabIndex);

        for (var i = 0; i < NUM_BUDDY_TABS; i++)
        {
            var button = addon->RadioButtons.GetPointer(i);
            if (button->Value != null)
                button->Value->IsSelected = i == addon->TabIndex;
        }
    }

    private void UpdateMiragePrismPrismBox(AddonMiragePrismPrismBox* addon)
    {
        if (addon->JobDropdown                                   == null ||
            addon->JobDropdown->List                             == null ||
            addon->JobDropdown->List->AtkComponentBase.OwnerNode == null ||
            addon->JobDropdown->List->AtkComponentBase.OwnerNode->AtkResNode.IsVisible())
            return;

        if (addon->OrderDropdown                                   == null ||
            addon->OrderDropdown->List                             == null ||
            addon->OrderDropdown->List->AtkComponentBase.OwnerNode == null ||
            addon->OrderDropdown->List->AtkComponentBase.OwnerNode->AtkResNode.IsVisible())
            return;

        var prevButton = !config.Invert ? addon->PrevButton : addon->NextButton;
        var nextButton = !config.Invert ? addon->NextButton : addon->PrevButton;

        if (prevButton == null || IsPrev && !prevButton->IsEnabled)
            return;

        if (nextButton == null || IsNext && !nextButton->IsEnabled)
            return;

        // if (IsAddonOpen("MiragePrismPrismBoxFilter"))
        // return;
        // TODO 先这样写着，但可能有BUG
        if (MiragePrismPrismBoxFilter->IsAddonAndNodesReady())
            return;

        var agent = AgentMiragePrismPrismBox.Instance();
        agent->PageIndex += (byte)wheelState;
        agent->UpdateItems(false, false);
    }

    private void UpdateCharacter(AddonCharacter* addon)
    {
        var tabIndex = GetTabIndex(addon->TabIndex, addon->TabCount);

        if (addon->TabIndex == tabIndex)
            return;

        addon->SetTab(tabIndex);

        for (var i = 0; i < addon->TabCount; i++)
        {
            var button = addon->Tabs.GetPointer(i);
            if (button->Value != null)
                button->Value->IsSelected = i == addon->TabIndex;
        }
    }

    private void UpdateCharacterClass(AddonCharacter* addonCharacter, AddonCharacterClass* addon)
    {
        // prev or next embedded addon
        if (addon->TabIndex + wheelState < 0 || addon->TabIndex + wheelState > 1)
        {
            UpdateCharacter(addonCharacter);
            return;
        }

        var tabIndex = GetTabIndex(addon->TabIndex, 2);

        if (addon->TabIndex == tabIndex)
            return;

        addon->SetTab(tabIndex);
    }

    private void UpdateCharacterRepute(AddonCharacter* addonCharacter, AddonCharacterRepute* addon)
    {
        // prev embedded addon
        if (addon->SelectedExpansion + wheelState < 0)
        {
            UpdateCharacter(addonCharacter);
            return;
        }

        var tabIndex = GetTabIndex(addon->SelectedExpansion, addon->ExpansionsCount);

        if (addon->SelectedExpansion == tabIndex)
            return;

        var atkEvent = new AtkEvent();
        var data     = new AtkEventData();
        data.ListItemData.SelectedIndex = tabIndex; // technically the index of an id array, but it's literally the same value
        addon->AtkUnitBase.ReceiveEvent((AtkEventType)37, 0, &atkEvent, &data);
    }
    
    private class Config : ModuleConfig
    {
        public bool Invert = true;
    }
    
    #region 常量

    private const int NUM_ARMOURY_BOARD_TABS            = 12;
    private const int NUM_INVENTORY_TABS                = 5;
    private const int NUM_INVENTORY_LARGE_TABS          = 4;
    private const int NUM_INVENTORY_EXPANSION_TABS      = 2;
    private const int NUM_INVENTORY_RETAINER_TABS       = 6;
    private const int NUM_INVENTORY_RETAINER_LARGE_TABS = 3;
    private const int NUM_BUDDY_TABS                    = 3;

    #endregion
}
