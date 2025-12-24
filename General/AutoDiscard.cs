using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoDiscard : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoDiscardTitle"),
        Description = GetLoc("AutoDiscardDescription"),
        Category    = ModuleCategories.General,
    };
    
    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private const string ModuleCommand = "/pdrdiscard";

    private static readonly Dictionary<DiscardBehaviour, string> DiscardBehaviourLoc = new()
    {
        [DiscardBehaviour.Discard] = LuminaWrapper.GetAddonText(91),
        [DiscardBehaviour.Sell]    = LuminaWrapper.GetAddonText(93),
    };

    private static readonly InventoryType[] InventoryTypes =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];
    
    private static LuminaSearcher<Item>? ItemSearcher;
    private static Config                ModuleConfig = null!;

    private static string NewGroupNameInput = string.Empty;
    private static string EditGroupNameInput = string.Empty;

    private static string ItemSearchInput = string.Empty;
    private static string SelectedItemSearchInput = string.Empty;
    
    private static string     AddItemsByNameInput  = string.Empty;
    private static List<Item> LastAddedItemsByName = [];
    
    private static uint       AddItemsByCategoryInput  = 61;
    private static List<Item> LastAddedItemsByCategory = [];

    protected override void Init()
    {
        ModuleConfig =   LoadConfig<Config>() ?? new();
        TaskHelper   ??= new() { TimeLimitMS = 2_000 };

        var itemNames = LuminaGetter.Get<Item>()
                                 .Where(x => !string.IsNullOrEmpty(x.Name.ExtractText()) &&
                                             x.ItemSortCategory.RowId != 3 && x.ItemSortCategory.RowId != 4)
                                 .GroupBy(x => x.Name.ExtractText())
                                 .Select(x => x.First())
                                 .ToList();
        ItemSearcher ??= new(itemNames, [x => x.Name.ExtractText(), x => x.RowId.ToString()], x => x.Name.ExtractText());
        
        CommandManager.AddCommand(ModuleCommand, new(OnCommand) { HelpMessage = GetLoc("AutoDiscard-CommandHelp") });
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreSetup, "SelectYesno", OnAddon);
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("Command")}:");

        ImGui.SameLine();
        ImGui.Text($"{ModuleCommand} → {GetLoc("AutoDiscard-CommandHelp")}");

        ImGui.Spacing();

        DrawAddNewGroupButton();

        ImGui.SameLine();
        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.FileImport, GetLoc("Import")))
        {
            var config = ImportFromClipboard<DiscardItemsGroup>();
            if (config != null)
            {
                ModuleConfig.DiscardGroups.Add(config);
                ModuleConfig.Save(this);
            }
        }

        var tableSize = new Vector2(ImGui.GetContentRegionAvail().X - (8f * GlobalFontScale), 0);
        using var table = ImRaii.Table("DiscardGroupTable", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg, tableSize);
        if (!table) return;

        var orderColumnWidth = ImGui.CalcTextSize((ModuleConfig.DiscardGroups.Count + 1).ToString()).X + 24;
        ImGui.TableSetupColumn("Order", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, orderColumnWidth);
        ImGui.TableSetupColumn("UniqueName", ImGuiTableColumnFlags.None, 20f);
        ImGui.TableSetupColumn("Items", ImGuiTableColumnFlags.None, 80f);
        ImGui.TableSetupColumn("Behaviour", ImGuiTableColumnFlags.None, 30f);
        ImGui.TableSetupColumn("Operations", ImGuiTableColumnFlags.None, 30f);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);

        ImGui.TableNextColumn();

        ImGui.TableNextColumn();
        ImGuiOm.Text(GetLoc("Name"));

        ImGui.TableNextColumn();
        ImGuiOm.Text(GetLoc("AutoDiscard-ItemsOverview"));

        ImGui.TableNextColumn();
        ImGuiOm.Text(GetLoc("Mode"));

        ImGui.TableNextColumn();
        ImGuiOm.Text(GetLoc("Operation"));

        for (var i = 0; i < ModuleConfig.DiscardGroups.Count; i++)
        {
            using var id = ImRaii.PushId(i);
            
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGuiOm.TextCentered($"{i + 1}");

            ImGui.TableNextColumn();
            UniqueNameColumn(i);

            ImGui.TableNextColumn();
            ItemsColumn(i);

            ImGui.TableNextColumn();
            BehaviourColumn(i);

            ImGui.TableNextColumn();
            OperationColumn(i);
        }
    }
    
    #region Table

    private void DrawAddNewGroupButton()
    {
        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, GetLoc("Add")))
            ImGui.OpenPopup("AddNewGroupPopup");

        using var popup = ImRaii.Popup("AddNewGroupPopup", ImGuiWindowFlags.AlwaysAutoResize);
        if (!popup) return;

        ImGui.SetNextItemWidth(300f * GlobalFontScale);
        ImGui.InputTextWithHint("###NewGroupNameInput",
                                GetLoc("AutoDiscard-AddNewGroupInputNameHelp"), ref NewGroupNameInput,
                                100);

        if (ImGui.Button(GetLoc("Confirm")))
        {
            var info = new DiscardItemsGroup(NewGroupNameInput);
            if (!string.IsNullOrWhiteSpace(NewGroupNameInput) && !ModuleConfig.DiscardGroups.Contains(info))
            {
                ModuleConfig.DiscardGroups.Add(info);
                SaveConfig(ModuleConfig);

                NewGroupNameInput = string.Empty;
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button(GetLoc("Cancel")))
            ImGui.CloseCurrentPopup();
    }

    private void UniqueNameColumn(int index)
    {
        if (index < 0 || index > ModuleConfig.DiscardGroups.Count) return;
        
        var group = ModuleConfig.DiscardGroups[index];
        using var id = ImRaii.PushId(index);

        if (ImGuiOm.SelectableFillCell($"{group.UniqueName}"))
        {
            EditGroupNameInput = group.UniqueName;
            ImGui.OpenPopup("EditGroupPopup");
        }

        using var popup = ImRaii.Popup("EditGroupPopup", ImGuiWindowFlags.AlwaysAutoResize);
        if (!popup) return;

        ImGui.SetNextItemWidth(300f * GlobalFontScale);
        ImGui.InputTextWithHint("###EditGroupNameInput", GetLoc("AutoDiscard-AddNewGroupInputNameHelp"), ref EditGroupNameInput, 100);

        if (ImGui.Button(GetLoc("Confirm")))
        {
            if (!string.IsNullOrWhiteSpace(EditGroupNameInput) &&
                !ModuleConfig.DiscardGroups.Contains(new(EditGroupNameInput)))
            {
                ModuleConfig.DiscardGroups
                            .FirstOrDefault(x => x.UniqueName == group.UniqueName)
                            .UniqueName = EditGroupNameInput;

                SaveConfig(ModuleConfig);
                EditGroupNameInput = string.Empty;

                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button(GetLoc("Cancel")))
            ImGui.CloseCurrentPopup();
    }

    private void ItemsColumn(int index)
    {
        if (index < 0 || index > ModuleConfig.DiscardGroups.Count) return;
        
        var group = ModuleConfig.DiscardGroups[index];
        using var id = ImRaii.PushId(index);

        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 2.5f);
        using (ImRaii.Group())
        {
            if (group.Items.Count > 0)
            {
                using (ImRaii.Group())
                using (ImRaii.Group())
                {
                    foreach (var item in group.Items.TakeLast(15))
                    {
                        var itemData = LuminaGetter.GetRow<Item>(item);
                        if (itemData == null) continue;

                        var itemIcon = DService.Texture.GetFromGameIcon(new(itemData.Value.Icon)).GetWrapOrDefault();
                        if (itemIcon == null) continue;

                        ImGui.Image(itemIcon.Handle, new(ImGui.GetTextLineHeightWithSpacing()));
                        ImGui.SameLine();
                    }
                }
            }
            else
                ImGui.Text(GetLoc("AutoDiscard-NoItemInGroupHelp"));
        }

        if (ImGui.IsItemClicked())
            ImGui.OpenPopup("ItemsEditMenu");

        var popupToOpen = string.Empty;
        using (var popupMenu = ImRaii.Popup("ItemsEditMenu", ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (popupMenu)
            {
                ImGui.Text(group.UniqueName);
                
                ImGui.Separator();
                ImGui.Spacing();
                
                if (ImGui.MenuItem(GetLoc("AutoDiscard-AddItemsBatch")))
                {
                    ImGui.CloseCurrentPopup();
                    popupToOpen = "AddItemsBatch";
                }
                
                if (ImGui.MenuItem(GetLoc("AutoDiscard-AddItemsManual")))
                {
                    ImGui.CloseCurrentPopup();
                    popupToOpen = "AddItemsManual";
                }
            }
        }
        
        if (!string.IsNullOrEmpty(popupToOpen))
            ImGui.OpenPopup(popupToOpen);

        using (var popup = ImRaii.Popup("AddItemsBatch"))
        {
            if (popup)
            {
                ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("AutoDiscard-AddItemsByName"));
                using (ImRaii.PushIndent())
                {
                    using (ImRaii.Disabled(string.IsNullOrWhiteSpace(AddItemsByNameInput)))
                    {
                        if (ImGui.Button($"{GetLoc("Add")}##AddItemByName"))
                        {
                            LastAddedItemsByName = ItemSearcher.Data
                                                               .Where(x => x.Name.ExtractText().Contains(AddItemsByNameInput, StringComparison.OrdinalIgnoreCase))
                                                               .ToList();
                            LastAddedItemsByName.ForEach(x => group.Items.Add(x.RowId));
                            SaveConfig(ModuleConfig);

                            NotificationSuccess(GetLoc("AutoDiscard-Notification-ItemsAdded", LastAddedItemsByName.Count));
                        }
                    }

                    ImGui.SameLine();
                    using (ImRaii.Disabled(LastAddedItemsByName.Count == 0))
                    {
                        if (ImGui.Button($"{GetLoc("Cancel")}##AddItemByName"))
                        {
                            LastAddedItemsByName.ForEach(x => group.Items.Remove(x.RowId));
                            SaveConfig(ModuleConfig);

                            LastAddedItemsByName.Clear();
                        }
                    }

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(300f * GlobalFontScale);
                    ImGui.InputText("###AddByItemName", ref AddItemsByNameInput, 128);
                }

                ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("AutoDiscard-AddItemsByCategory"));
                using (ImRaii.PushIndent())
                {
                    using (ImRaii.Disabled(!LuminaGetter.TryGetRow<ItemUICategory>(AddItemsByCategoryInput, out _)))
                    {
                        if (ImGui.Button($"{GetLoc("Add")}##AddItemByCategory"))
                        {
                            LastAddedItemsByCategory = ItemSearcher.Data
                                                                   .Where(x => x.ItemUICategory.RowId == AddItemsByCategoryInput)
                                                                   .ToList();
                            LastAddedItemsByCategory.ForEach(x => group.Items.Add(x.RowId));
                            SaveConfig(ModuleConfig);

                            NotificationSuccess(GetLoc("AutoDiscard-Notification-ItemsAdded", LastAddedItemsByCategory.Count));
                        }
                    }

                    ImGui.SameLine();
                    using (ImRaii.Disabled(LastAddedItemsByCategory.Count == 0))
                    {
                        if (ImGui.Button($"{GetLoc("Cancel")}##AddItemByCategory"))
                        {
                            LastAddedItemsByCategory.ForEach(x => group.Items.Remove(x.RowId));
                            SaveConfig(ModuleConfig);

                            LastAddedItemsByCategory.Clear();
                        }
                    }

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(300f * GlobalFontScale);
                    using (var combo = ImRaii.Combo("###AddItemsByCategoryCombo",
                                                    LuminaGetter.TryGetRow<ItemUICategory>(AddItemsByCategoryInput, out var uiCategory)
                                                        ? uiCategory.Name.ExtractText()
                                                        : string.Empty, ImGuiComboFlags.HeightLarge))
                    {
                        if (combo)
                        {
                            foreach (var itemUiCategory in LuminaGetter.Get<ItemUICategory>())
                            {
                                var name = itemUiCategory.Name.ExtractText();
                                if (string.IsNullOrEmpty(name)) continue;
                                if (!ImageHelper.TryGetGameIcon((uint)itemUiCategory.Icon, out var icon)) continue;

                                if (ImGuiOm.SelectableImageWithText(icon.Handle, new(ImGui.GetTextLineHeightWithSpacing()), name,
                                                                    AddItemsByCategoryInput == itemUiCategory.RowId))
                                    AddItemsByCategoryInput = itemUiCategory.RowId;
                            }
                        }
                    }
                }
            }
        }
        
        using (var popup = ImRaii.Popup("AddItemsManual"))
        {
            if (popup)
            {
                var leftChildSize = new Vector2(300 * GlobalFontScale, 500 * GlobalFontScale);
                using (var leftChild = ImRaii.Child("SelectedItemChild", leftChildSize, true))
                {
                    if (leftChild)
                    {
                        ImGui.SetNextItemWidth(-1f);
                        ImGui.InputTextWithHint("###SelectedItemSearchInput", GetLoc("PleaseSearch"), ref SelectedItemSearchInput, 100);

                        ImGui.Separator();
                        foreach (var item in group.Items)
                        {
                            var specificItemNullable = LuminaGetter.GetRow<Item>(item);
                            if (specificItemNullable == null) continue;
                            var specificItem     = specificItemNullable.Value;
                            var specificItemIcon = DService.Texture.GetFromGameIcon(new(specificItem.Icon)).GetWrapOrDefault();
                            if (specificItemIcon == null) continue;

                            if (!string.IsNullOrWhiteSpace(SelectedItemSearchInput) &&
                                !specificItem.Name.ExtractText().Contains(SelectedItemSearchInput, StringComparison.OrdinalIgnoreCase)) continue;

                            if (ImGuiOm.SelectableImageWithText(specificItemIcon.Handle,
                                                                new(ImGui.GetTextLineHeightWithSpacing()),
                                                                specificItem.Name.ExtractText(),
                                                                false,
                                                                ImGuiSelectableFlags.DontClosePopups))
                                group.Items.Remove(specificItem.RowId);
                        }
                    }
                }

                ImGui.SameLine();
                using (ImRaii.Group())
                {
                    using (ImRaii.Disabled())
                    {
                        using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0)))
                        {
                            ImGui.SetCursorPosY((ImGui.GetContentRegionAvail().Y / 2) - 24f);
                            ImGuiOm.ButtonIcon("DecoExchangeIcon", FontAwesomeIcon.ExchangeAlt);
                        }
                    }
                }

                ImGui.SameLine();
                using (var rightChild = ImRaii.Child("SearchItemChild", leftChildSize, true))
                {
                    if (rightChild)
                    {
                        ImGui.SetNextItemWidth(-1f);
                        if (ImGui.InputTextWithHint("###GameItemSearchInput", GetLoc("PleaseSearch"), ref ItemSearchInput, 100))
                            ItemSearcher.Search(ItemSearchInput);

                        ImGui.Separator();
                        foreach (var item in ItemSearcher.SearchResult)
                        {
                            if (group.Items.Contains(item.RowId)) continue;

                            var itemIcon = DService.Texture.GetFromGameIcon(new(item.Icon)).GetWrapOrDefault();
                            if (itemIcon == null) continue;

                            if (ImGuiOm.SelectableImageWithText(itemIcon.Handle,
                                                                new(ImGui.GetTextLineHeightWithSpacing()),
                                                                item.Name.ExtractText(),
                                                                group.Items.Contains(item.RowId),
                                                                ImGuiSelectableFlags.DontClosePopups))
                            {
                                if (!group.Items.Remove(item.RowId))
                                    group.Items.Add(item.RowId);

                                SaveConfig(ModuleConfig);
                            }
                        }
                    }
                }
            }
        }
    }

    private void BehaviourColumn(int index)
    {
        if (index < 0 || index > ModuleConfig.DiscardGroups.Count) return;
        
        var group = ModuleConfig.DiscardGroups[index];
        using var id = ImRaii.PushId(index);

        foreach (var behaviourPair in DiscardBehaviourLoc)
        {
            if (ImGui.RadioButton(behaviourPair.Value, behaviourPair.Key == group.Behaviour))
            {
                group.Behaviour = behaviourPair.Key;
                SaveConfig(ModuleConfig);
            }
            ImGui.SameLine();
        }
    }

    private void OperationColumn(int index)
    {
        if (index < 0 || index > ModuleConfig.DiscardGroups.Count) return;
        
        var group = ModuleConfig.DiscardGroups[index];
        using var id = ImRaii.PushId(index);

        using (ImRaii.Disabled(TaskHelper.IsBusy))
        {
            if (ImGuiOm.ButtonIcon($"Run_{index}", FontAwesomeIcon.Play, GetLoc("Run")))
                group.Enqueue(TaskHelper);
        }

        ImGui.SameLine();
        if (ImGuiOm.ButtonIcon($"Stop_{index}", FontAwesomeIcon.Stop, GetLoc("Stop")))
            TaskHelper.Abort();

        using (ImRaii.Disabled(TaskHelper.IsBusy))
        {
            ImGui.SameLine();
            if (ImGuiOm.ButtonIcon($"Copy_{index}", FontAwesomeIcon.Copy, GetLoc("Copy")))
            {
                var newGroup = new DiscardItemsGroup(GenerateUniqueName(group.UniqueName))
                {
                    Behaviour = group.Behaviour,
                    Items     = group.Items,
                };

                ModuleConfig.DiscardGroups.Add(newGroup);
                SaveConfig(ModuleConfig);
            }

            ImGui.SameLine();
            if (ImGuiOm.ButtonIcon($"Export_{index}", FontAwesomeIcon.FileExport, GetLoc("Export")))
                ExportToClipboard(group);

            ImGui.SameLine();
            if (ImGuiOm.ButtonIcon($"Delete_{index}", FontAwesomeIcon.TrashAlt, GetLoc("HoldCtrlToDelete")))
            {
                if (ImGui.IsKeyDown(ImGuiKey.LeftCtrl))
                {
                    ModuleConfig.DiscardGroups.Remove(group);
                    SaveConfig(ModuleConfig);
                }
            }
        }
    }

    #endregion

    private void OnCommand(string command, string arguments) => 
        EnqueueDiscardGroup(arguments.Trim());

    public void EnqueueDiscardGroup(int index)
    {
        if (index < 0 || index >= ModuleConfig.DiscardGroups.Count) return;
        var group = ModuleConfig.DiscardGroups[index];
        if (group.Items.Count > 0)
            group.Enqueue(TaskHelper);
    }

    public void EnqueueDiscardGroup(string uniqueName)
    {
        var group = ModuleConfig.DiscardGroups.FirstOrDefault(x => x.UniqueName == uniqueName && x.Items.Count > 0);
        if (group == null) return;

        group.Enqueue(TaskHelper);
    }

    private static bool TrySearchItemInInventory(uint itemID, out List<InventoryItem> foundItem)
    {
        foundItem = [];
        if (InventoryExpansion == null) return false;
        
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null) return false;

        foreach (var type in InventoryTypes)
        {
            var container = inventoryManager->GetInventoryContainer(type);
            if (container == null) return false;

            for (var i = 0; i < container->Size; i++)
            {
                var slot       = container->GetInventorySlot(i);
                var slotItemID = slot->ItemId % 100_0000;
                if (slotItemID != itemID) continue;
                
                foundItem.Add(*slot);
            }
        }

        return foundItem.Count > 0;
    }
    
    private static string GenerateUniqueName(string baseName)
    {
        var existingNames = ModuleConfig.DiscardGroups.Select(x => x.UniqueName).ToHashSet();

        if (!existingNames.Contains(baseName))
            return baseName;

        var counter = 0;
        var numberPart = string.Empty;
        foreach (var c in baseName.Reverse())
        {
            if (char.IsDigit(c))
                numberPart = c + numberPart;
            else
                break;
        }

        if (numberPart.Length > 0)
        {
            counter = int.Parse(numberPart) + 1;
            baseName = baseName[..^numberPart.Length];
        }

        while (true)
        {
            var newName = $"{baseName}{counter}";

            if (!existingNames.Contains(newName))
                return newName;

            counter++;
        }
    }
    
    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        if (!TaskHelper.IsBusy) return;
        ClickSelectYesnoYes();
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        
        CommandManager.RemoveCommand(ModuleCommand);
        ItemSearcher = null;
        
        LastAddedItemsByName.Clear();
        LastAddedItemsByCategory.Clear();
    }

    [IPCProvider("DailyRoutines.Modules.AutoDiscard.IsBusy")]
    private bool IsBusy() => 
        TaskHelper.IsBusy;
    
    private enum DiscardBehaviour
    {
        Discard,
        Sell,
    }

    private class DiscardItemsGroup : IEquatable<DiscardItemsGroup>
    {
        public DiscardItemsGroup() { }

        public DiscardItemsGroup(string name) => UniqueName = name;

        public string           UniqueName { get; set; } = null!;
        public HashSet<uint>    Items      { get; set; } = [];
        public DiscardBehaviour Behaviour  { get; set; } = DiscardBehaviour.Discard;

        public void Enqueue(TaskHelper? taskHelper)
        {
            if (taskHelper == null) return;

            var isAny = false;
            foreach (var item in Items)
            {
                if (!TrySearchItemInInventory(item, out var foundItem) || foundItem.Count <= 0) continue;
                
                foreach (var fItem in foundItem)
                {
                    var type = fItem.GetInventoryType();
                    var slot = fItem.Slot;
                    if (type == InventoryType.Invalid || slot < 0) continue;

                    var itemInventory = InventoryManager.Instance()->GetInventorySlot(type, slot);
                    if (itemInventory == null) continue;
                    
                    isAny = true;
                    if (Behaviour == DiscardBehaviour.Discard)
                    {
                        taskHelper.Enqueue(() => AgentInventoryContext.Instance()->DiscardItem(itemInventory, type, slot, GetActiveInventoryAddonID()));
                        taskHelper.Enqueue(() => { ClickSelectYesnoYes(); });
                    }
                    else
                    {
                        taskHelper.Enqueue(() => OpenInventoryItemContext(fItem));
                        taskHelper.Enqueue(() => ClickDiscardContextMenu(taskHelper));
                    }
                }
            }

            if (isAny)
            {
                taskHelper.DelayNext(100);
                taskHelper.Enqueue(() => Enqueue(taskHelper));
            }
        }

        private bool? ClickDiscardContextMenu(TaskHelper? taskHelper)
        {
            if (!IsAddonAndNodesReady(InfosOm.ContextMenuXIV)) return false;

            switch (Behaviour)
            {
                case DiscardBehaviour.Discard:
                    if (!ClickContextMenu(LuminaWrapper.GetAddonText(91)))
                    {
                        InfosOm.ContextMenuXIV->Close(true);
                        break;
                    }

                    taskHelper.Enqueue(() => ClickSelectYesnoYes(), "ConfirmDiscard", weight: 1);
                    break;
                case DiscardBehaviour.Sell:
                    if (!ClickContextMenu(LuminaWrapper.GetAddonText(5480)) &&
                        !ClickContextMenu(LuminaWrapper.GetAddonText(93)))
                    {
                        InfosOm.ContextMenuXIV->Close(true);
                        ChatError(GetLoc("AutoDiscard-NoSellPage"));

                        taskHelper.Abort();
                    }

                    break;
            }

            return true;
        }

        public bool Equals(DiscardItemsGroup? other)
        {
            if (other is null || GetType() != other.GetType())
                return false;

            return UniqueName == other.UniqueName;
        }

        public override bool Equals(object? obj) => Equals(obj as DiscardItemsGroup);

        public override int GetHashCode() => HashCode.Combine(UniqueName);

        public static bool operator ==(DiscardItemsGroup? lhs, DiscardItemsGroup? rhs)
        {
            if (lhs is null) return rhs is null;
            return lhs.Equals(rhs);
        }

        public static bool operator !=(DiscardItemsGroup lhs, DiscardItemsGroup rhs) => !(lhs == rhs);
    }

    private class Config : ModuleConfiguration
    {
        public List<DiscardItemsGroup> DiscardGroups = [];
    }
}
