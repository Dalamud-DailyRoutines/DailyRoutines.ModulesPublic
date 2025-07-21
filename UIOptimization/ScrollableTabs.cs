using System;
using System.Collections.Generic;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace DailyRoutines.Modules;

public unsafe class ScrollableTabs : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("ScrollableTabsTitle"),
        Description = GetLoc("ScrollableTabsDescription"),
        Category    = ModuleCategories.UIOptimization,
        Author      = ["Cyf5119"],
    };
    
    private const int NumArmouryBoardTabs           = 12;
    private const int NumInventoryTabs              = 5;
    private const int NumInventoryLargeTabs         = 4;
    private const int NumInventoryExpansionTabs     = 2;
    private const int NumInventoryRetainerTabs      = 6;
    private const int NumInventoryRetainerLargeTabs = 3;
    private const int NumBuddyTabs                  = 3;

    private static Config ModuleConfig = null!;
    private static int    WheelState;

    private static AtkCollisionNode* IntersectingCollisionNode =>
        RaptureAtkModule.Instance()->AtkCollisionManager.IntersectingCollisionNode;

    private static bool IsNext =>
        WheelState == (!ModuleConfig.Invert ? 1 : -1);

    private static bool IsPrev =>
        WheelState == (!ModuleConfig.Invert ? -1 : 1);

    private delegate        void                                   AddonUpdateHandler(AtkUnitBase* unitBase);
    private static readonly Dictionary<string, AddonUpdateHandler> UIHandlerMapping = new();
    private static readonly Dictionary<string, string>             UINameMapping    = new();

    static ScrollableTabs()
    {
        InitUINameMapping();
        InitUIHandlerMapping();

        static void InitUINameMapping()
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
                UINameMapping[name] = name;

            // Inventory
            foreach (var name in new[] { "InventoryGrid", "InventoryGridCrystal" })
                UINameMapping[name] = "Inventory";

            // InventoryEvent
            foreach (var name in new[] { "InventoryEventGrid" })
                UINameMapping[name] = "InventoryEvent";

            // InventoryLarge / InventoryExpansion
            UINameMapping["InventoryCrystalGrid"] = "InventoryLarge";

            // InventoryLarge 相关映射
            foreach (var name in new[] { "InventoryEventGrid0", "InventoryEventGrid1", "InventoryEventGrid2", "InventoryGrid0", "InventoryGrid1" })
                UINameMapping[name] = "InventoryLarge";

            // InventoryExpansion
            foreach (var name in new[]
                     {
                         "InventoryEventGrid0E", "InventoryEventGrid1E", "InventoryEventGrid2E", "InventoryGrid0E", "InventoryGrid1E", "InventoryGrid2E",
                         "InventoryGrid3E"
                     })
                UINameMapping[name] = "InventoryExpansion";

            // InventoryRetainer
            foreach (var name in new[] { "RetainerGridCrystal", "RetainerGrid" })
                UINameMapping[name] = "InventoryRetainer";

            // InventoryRetainerLarge
            foreach (var name in new[] { "RetainerCrystalGrid", "RetainerGrid0", "RetainerGrid1", "RetainerGrid2", "RetainerGrid3", "RetainerGrid4" })
                UINameMapping[name] = "InventoryRetainerLarge";

            // Character
            foreach (var name in new[] { "CharacterStatus", "CharacterProfile" })
                UINameMapping[name] = "Character";

            // Buddy
            foreach (var name in new[] { "BuddyAction", "BuddySkill", "BuddyAppearance" })
                UINameMapping[name] = "Buddy";
        }

        static void InitUIHandlerMapping()
        {
            UIHandlerMapping["ArmouryBoard"] = unitBase => UpdateArmouryBoard((AddonArmouryBoard*)unitBase);

            // Inventory
            UIHandlerMapping["Inventory"]          = unitBase => UpdateInventory((AddonInventory*)unitBase);
            UIHandlerMapping["InventoryEvent"]     = unitBase => UpdateInventoryEvent((AddonInventoryEvent*)unitBase);
            UIHandlerMapping["InventoryLarge"]     = unitBase => UpdateInventoryLarge((AddonInventoryLarge*)unitBase);
            UIHandlerMapping["InventoryExpansion"] = unitBase => UpdateInventoryExpansion((AddonInventoryExpansion*)unitBase);

            // Retainer
            UIHandlerMapping["InventoryRetainer"]      = unitBase => UpdateInventoryRetainer((AddonInventoryRetainer*)unitBase);
            UIHandlerMapping["InventoryRetainerLarge"] = unitBase => UpdateInventoryRetainerLarge((AddonInventoryRetainerLarge*)unitBase);

            // NoteBook
            UIHandlerMapping["MinionNoteBook"] = unitBase => UpdateMountMinion((AddonMinionMountBase*)unitBase);
            UIHandlerMapping["MountNoteBook"]  = unitBase => UpdateMountMinion((AddonMinionMountBase*)unitBase);

            // TabController
            UIHandlerMapping["FishGuide2"]        = unitBase => UpdateTabController(unitBase, &((AddonFishGuide2*)unitBase)->TabController);
            UIHandlerMapping["AdventureNoteBook"] = unitBase => UpdateTabController(unitBase, &((AddonAdventureNoteBook*)unitBase)->TabController);
            UIHandlerMapping["OrnamentNoteBook"]  = unitBase => UpdateTabController(unitBase, &((AddonOrnamentNoteBook*)unitBase)->TabController);
            UIHandlerMapping["GSInfoCardList"]    = unitBase => UpdateTabController(unitBase, &((AddonGSInfoCardList*)unitBase)->TabController);
            UIHandlerMapping["GSInfoEditDeck"]    = unitBase => UpdateTabController(unitBase, &((AddonGSInfoEditDeck*)unitBase)->TabController);
            UIHandlerMapping["LovmPaletteEdit"]   = unitBase => UpdateTabController(unitBase, &((AddonLovmPaletteEdit*)unitBase)->TabController);

            // 其他
            UIHandlerMapping["AOZNotebook"]          = unitBase => UpdateAOZNotebook((AddonAOZNotebook*)unitBase);
            UIHandlerMapping["AetherCurrent"]        = unitBase => UpdateAetherCurrent((AddonAetherCurrent*)unitBase);
            UIHandlerMapping["FateProgress"]         = unitBase => UpdateFateProgress((AddonFateProgress*)unitBase);
            UIHandlerMapping["MYCWarResultNotebook"] = unitBase => UpdateFieldNotes((AddonMYCWarResultNotebook*)unitBase);
            UIHandlerMapping["MJIMinionNoteBook"]    = unitBase => UpdateMJIMinionNoteBook((AddonMJIMinionNoteBook*)unitBase);
            UIHandlerMapping["InventoryBuddy"]       = unitBase => UpdateInventoryBuddy((AddonInventoryBuddy*)unitBase);
            UIHandlerMapping["InventoryBuddy2"]      = unitBase => UpdateInventoryBuddy((AddonInventoryBuddy*)unitBase);
            UIHandlerMapping["Buddy"]                = unitBase => UpdateBuddy((AddonBuddy*)unitBase);
            UIHandlerMapping["MiragePrismPrismBox"]  = unitBase => UpdateMiragePrismPrismBox((AddonMiragePrismPrismBox*)unitBase);

            // Currency
            UIHandlerMapping["Currency"] = unitBase => UpdateCurrency((AddonCurrency*)unitBase);

            // Character
            UIHandlerMapping["Character"]       = unitBase => UpdateCharacter((AddonCharacter*)unitBase);
            UIHandlerMapping["CharacterClass"]  = HandleCharacterUI;
            UIHandlerMapping["CharacterRepute"] = HandleCharacterUI;
        }
    }

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new Config();

        FrameworkManager.Register(OnUpdate);
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("ScrollableTabs-Invert"), ref ModuleConfig.Invert))
            SaveConfig(ModuleConfig);
    }

    protected override void Uninit()
    {
        FrameworkManager.Unregister(OnUpdate);
        base.Uninit();
    }

    private static void OnUpdate(IFramework _)
    {
        if (!DService.ClientState.IsLoggedIn)
            return;

        WheelState = Math.Clamp(UIInputData.Instance()->CursorInputs.MouseWheel, -1, 1);
        if (WheelState == 0)
            return;

        if (ModuleConfig.Invert)
            WheelState *= -1;

        var hoveredUnitBase = RaptureAtkModule.Instance()->AtkCollisionManager.IntersectingAddon;
        if (hoveredUnitBase == null)
        {
            WheelState = 0;
            return;
        }

        var originalName = hoveredUnitBase->NameString;
        if (string.IsNullOrEmpty(originalName))
        {
            WheelState = 0;
            return;
        }

        if (!UINameMapping.TryGetValue(originalName, out var mappedName))
        {
            WheelState = 0;
            return;
        }

        // InventoryCrystalGrid
        if (originalName == "InventoryCrystalGrid" && 
            DService.GameConfig.UiConfig.TryGet("ItemInventryWindowSizeType", out uint itemInventryWindowSizeType) && 
            itemInventryWindowSizeType == 2)
            mappedName = "InventoryExpansion";

        if (!TryGetAddonByName(mappedName, out var unitBase))
        {
            WheelState = 0;
            return;
        }

        if (UIHandlerMapping.TryGetValue(mappedName, out var handler))
            handler(unitBase);

        WheelState = 0;
    }

    private static void HandleCharacterUI(AtkUnitBase* unitBase)
    {
        var name = unitBase->NameString;
        var addonCharacter = name == "Character" ? (AddonCharacter*)unitBase : GetAddonByName<AddonCharacter>("Character");

        if (addonCharacter == null || !addonCharacter->AddonControl.IsChildSetupComplete ||
            IntersectingCollisionNode == addonCharacter->CharacterPreviewCollisionNode)
        {
            WheelState = 0;
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

    private static int GetTabIndex(int currentTabIndex, int numTabs) => Math.Clamp(currentTabIndex + WheelState, 0, numTabs - 1);

    private static void UpdateArmouryBoard(AddonArmouryBoard* addon)
    {
        var tabIndex = GetTabIndex(addon->TabIndex, NumArmouryBoardTabs);

        if (addon->TabIndex < tabIndex)
            addon->NextTab(0);
        else if (addon->TabIndex > tabIndex)
            addon->PreviousTab(0);
    }

    private static void UpdateInventory(AddonInventory* addon)
    {
        if (addon->TabIndex == NumInventoryTabs - 1 && WheelState > 0)
        {
            var values = stackalloc AtkValue[3];

            values[0].Ctor();
            values[0].Type = ValueType.Int;
            values[0].Int  = 22;

            values[1].Ctor();
            values[1].Type = ValueType.Int;
            values[1].Int  = *(int*)((nint)addon + 0x228);

            values[2].Ctor();
            values[2].Type = ValueType.UInt;
            values[2].UInt = 0;

            addon->AtkUnitBase.FireCallback(3, values);
        }
        else
        {
            var tabIndex = GetTabIndex(addon->TabIndex, NumInventoryTabs);

            if (addon->TabIndex == tabIndex)
                return;

            addon->SetTab(tabIndex);
        }
    }

    private static void UpdateInventoryEvent(AddonInventoryEvent* addon)
    {
        if (addon->TabIndex == 0 && WheelState < 0)
        {
            // inside Vf68, fn call before return with a2 being 2
            var values = stackalloc AtkValue[3];

            values[0].Ctor();
            values[0].Type = ValueType.Int;
            values[0].Int  = 22;

            values[1].Ctor();
            values[1].Type = ValueType.Int;
            values[1].Int  = *(int*)((nint)addon + 0x280);

            values[2].Ctor();
            values[2].Type = ValueType.UInt;
            values[2].UInt = 2;

            addon->AtkUnitBase.FireCallback(3, values);
        }
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

    private static void UpdateInventoryLarge(AddonInventoryLarge* addon)
    {
        var tabIndex = GetTabIndex(addon->TabIndex, NumInventoryLargeTabs);

        if (addon->TabIndex == tabIndex)
            return;

        addon->SetTab(tabIndex);
    }

    private static void UpdateInventoryExpansion(AddonInventoryExpansion* addon)
    {
        var tabIndex = GetTabIndex(addon->TabIndex, NumInventoryExpansionTabs);

        if (addon->TabIndex == tabIndex)
            return;

        addon->SetTab(tabIndex, false);
    }

    private static void UpdateInventoryRetainer(AddonInventoryRetainer* addon)
    {
        var tabIndex = GetTabIndex(addon->TabIndex, NumInventoryRetainerTabs);

        if (addon->TabIndex == tabIndex)
            return;

        addon->SetTab(tabIndex);
    }

    private static void UpdateInventoryRetainerLarge(AddonInventoryRetainerLarge* addon)
    {
        var tabIndex = GetTabIndex(addon->TabIndex, NumInventoryRetainerLargeTabs);

        if (addon->TabIndex == tabIndex)
            return;

        addon->SetTab(tabIndex);
    }

    private static void UpdateTabController(AtkUnitBase* addon, TabController* tabController)
    {
        var tabIndex = GetTabIndex(tabController->TabIndex, tabController->TabCount);

        if (tabController->TabIndex == tabIndex)
            return;

        tabController->TabIndex = tabIndex;
        tabController->CallbackFunction(tabIndex, addon);
    }

    private static void UpdateAOZNotebook(AddonAOZNotebook* addon)
    {
        var tabIndex = GetTabIndex(addon->TabIndex, addon->TabCount);

        if (addon->TabIndex == tabIndex)
            return;

        addon->SetTab(tabIndex, true);
    }

    private static void UpdateAetherCurrent(AddonAetherCurrent* addon)
    {
        var tabIndex = GetTabIndex(addon->TabIndex, addon->TabCount);
        if (addon->TabIndex == tabIndex) return;

        addon->SetTab(tabIndex);

        for (var i = 0; i < addon->Tabs.Length; i++) 
            addon->Tabs[i].Value->IsSelected = i == tabIndex;
    }

    private static void UpdateFateProgress(AddonFateProgress* addon)
    {
        var tabIndex = GetTabIndex(addon->TabIndex, addon->TabCount);
        if (!addon->IsLoaded || addon->TabIndex == tabIndex)
            return;

        var atkEvent = new AtkEvent();
        addon->SetTab(tabIndex, &atkEvent);
    }

    private static void UpdateFieldNotes(AddonMYCWarResultNotebook* addon)
    {
        if (IntersectingCollisionNode == addon->DescriptionCollisionNode)
            return;

        var atkEvent   = new AtkEvent();
        var eventParam = Math.Clamp((addon->CurrentNoteIndex % 10) + WheelState, -1, addon->MaxNoteIndex - 1);

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

    private static void UpdateMountMinion(AddonMinionMountBase* addon)
    {
        switch (addon->CurrentView)
        {
            case AddonMinionMountBase.ViewType.Normal when addon->TabController.TabIndex == 0 && WheelState < 0:
                addon->SwitchToFavorites();
                break;
            case AddonMinionMountBase.ViewType.Normal:
                UpdateTabController((AtkUnitBase*)addon, &addon->TabController);
                break;
            case AddonMinionMountBase.ViewType.Favorites when WheelState > 0:
                addon->TabController.CallbackFunction(0, (AtkUnitBase*)addon);
                break;
        }
    }

    private static void UpdateMJIMinionNoteBook(AddonMJIMinionNoteBook* addon)
    {
        var agent = AgentMJIMinionNoteBook.Instance();

        if (agent->CurrentView == AgentMJIMinionNoteBook.ViewType.Normal)
        {
            if (addon->TabController.TabIndex == 0 && WheelState < 0)
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
        else if (agent->CurrentView == AgentMJIMinionNoteBook.ViewType.Favorites && WheelState > 0)
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

    private static void UpdateInventoryBuddy(AddonInventoryBuddy* addon)
    {
        if (!PlayerState.Instance()->HasPremiumSaddlebag)
            return;

        var tabIndex = GetTabIndex(addon->TabIndex, 2);

        if (addon->TabIndex == tabIndex)
            return;

        addon->SetTab((byte)tabIndex);
    }

    private static void UpdateCurrency(AddonCurrency* addon)
    {
        var atkStage = AtkStage.Instance();
        var numberArray = atkStage->GetNumberArrayData(NumberArrayType.Currency);
        var currentTab = numberArray->IntArray[0];
        var newTab = currentTab;
    
        var enableStates = new bool[addon->Tabs.Length];
        for (var i = 0; i < addon->Tabs.Length; i++)
            enableStates[i] = addon->Tabs[i].Value != null && addon->Tabs[i].Value->IsEnabled;
        
    
        if (WheelState > 0 && currentTab < enableStates.Length)
        {
            for (var i = currentTab + 1; i < enableStates.Length; i++)
            {
                if (enableStates[i])
                {
                    newTab = i;
                    break;
                }
            }
        }
        else if (currentTab > 0)
        {
            for (var i = currentTab - 1; i >= 0; i--)
            {
                if (enableStates[i])
                {
                    newTab = i;
                    break;
                }
            }
        }
    
        if (currentTab == newTab)
            return;
    
        numberArray->SetValue(0, newTab);
        addon->AtkUnitBase.OnRequestedUpdate(atkStage->GetNumberArrayData(), atkStage->GetStringArrayData());
    }

    private static void UpdateBuddy(AddonBuddy* addon)
    {
        var tabIndex = GetTabIndex(addon->TabIndex, NumBuddyTabs);

        if (addon->TabIndex == tabIndex)
            return;

        addon->SetTab(tabIndex);

        for (var i = 0; i < NumBuddyTabs; i++)
        {
            var button = addon->RadioButtons.GetPointer(i);
            if (button->Value != null) 
                button->Value->IsSelected = i == addon->TabIndex;
        }
    }

    private static void UpdateMiragePrismPrismBox(AddonMiragePrismPrismBox* addon)
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

        var prevButton = !ModuleConfig.Invert ? addon->PrevButton : addon->NextButton;
        var nextButton = !ModuleConfig.Invert ? addon->NextButton : addon->PrevButton;

        if (prevButton == null || (IsPrev && !prevButton->IsEnabled))
            return;

        if (nextButton == null || (IsNext && !nextButton->IsEnabled))
            return;

        // if (IsAddonOpen("MiragePrismPrismBoxFilter"))
        // return;
        // TODO 先这样写着，但可能有BUG
        if (IsAddonAndNodesReady(GetAddonByName("MiragePrismPrismBoxFilter")))
            return;

        var agent = AgentMiragePrismPrismBox.Instance();
        agent->PageIndex += (byte)WheelState;
        agent->UpdateItems(false, false);
    }

    private static void UpdateCharacter(AddonCharacter* addon)
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

    private static void UpdateCharacterClass(AddonCharacter* addonCharacter, AddonCharacterClass* addon)
    {
        // prev or next embedded addon
        if (addon->TabIndex + WheelState < 0 || addon->TabIndex + WheelState > 1)
        {
            UpdateCharacter(addonCharacter);
            return;
        }

        var tabIndex = GetTabIndex(addon->TabIndex, 2);

        if (addon->TabIndex == tabIndex)
            return;

        addon->SetTab(tabIndex);
    }

    private static void UpdateCharacterRepute(AddonCharacter* addonCharacter, AddonCharacterRepute* addon)
    {
        // prev embedded addon
        if (addon->SelectedExpansion + WheelState < 0)
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
    
    private class Config : ModuleConfiguration
    {
        public bool Invert = true;
    }
}
