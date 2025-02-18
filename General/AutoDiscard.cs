using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Command;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DailyRoutines.Modules;

public unsafe class AutoDiscard : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoDiscardTitle"),
        Description = GetLoc("AutoDiscardDescription"),
        Category = ModuleCategories.General,
    };

    private const string ModuleCommand = "/pdrdiscard";

    private static readonly Dictionary<DiscardBehaviour, string> DiscardBehaviourLoc = new()
    {
        { DiscardBehaviour.Discard, GetLoc("AutoDiscard-Discard") },
        { DiscardBehaviour.Sell, GetLoc("AutoDiscard-Sell") },
    };

    private static readonly InventoryType[] InventoryTypes =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    private static Config ModuleConfig = null!;

    private static string NewGroupNameInput = string.Empty;
    private static string EditGroupNameInput = string.Empty;

    private static string ItemSearchInput = string.Empty;
    private static string SelectedItemSearchInput = string.Empty;

    private static LuminaSearcher<Item>? ItemSearcher;

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        var itemNames = LuminaCache.Get<Item>()
                                 .Where(x => !string.IsNullOrEmpty(x.Name.ExtractText()) &&
                                             x.ItemSortCategory.RowId != 3 && x.ItemSortCategory.RowId != 4)
                                 .GroupBy(x => x.Name.ExtractText())
                                 .Select(x => x.First())
                                 .ToList();
        ItemSearcher ??= new(itemNames, [x => x.Name.ExtractText(), x => x.RowId.ToString()], x => x.Name.ExtractText());

        TaskHelper ??= new TaskHelper { TimeLimitMS = 10_000 };

        CommandManager.AddCommand(ModuleCommand,
                                          new CommandInfo(OnCommand) { HelpMessage = GetLoc("AutoDiscard-CommandHelp") });
    }

    public override void ConfigUI()
    {
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("Command")}:");

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

    private void OnCommand(string command, string arguments) => EnqueueDiscardGroup(arguments.Trim());

    public void EnqueueDiscardGroup(int index)
    {
        if (index < 0 || index > ModuleConfig.DiscardGroups.Count) return;
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

    private static bool? ConfirmDiscard()
    {
        if (!Throttler.Throttle("AutoDiscard", 100)) return false;
        if (SelectYesno == null || !IsAddonAndNodesReady(SelectYesno)) return false;

        Callback(SelectYesno, true, 0);
        return true;
    }

    private static string GenerateUniqueName(string baseName)
    {
        var existingNames = ModuleConfig.DiscardGroups.Select(x => x.UniqueName).ToHashSet();

        if (!existingNames.Contains(baseName))
            return baseName;

        var counter = 0;
        var numberPart = string.Empty;
        foreach (var c in baseName.Reverse())
            if (char.IsDigit(c))
                numberPart = c + numberPart;
            else
                break;

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

    public override void Uninit()
    {
        CommandManager.RemoveCommand(ModuleCommand);

        base.Uninit();
    }

    private enum DiscardBehaviour
    {
        Discard,
        Sell,
    }

    private class DiscardItemsGroup : IEquatable<DiscardItemsGroup>
    {
        public DiscardItemsGroup() { }

        public DiscardItemsGroup(string name)
        {
            UniqueName = name;
        }

        public string           UniqueName { get; set; } = null!;
        public HashSet<uint>    Items      { get; set; } = [];
        public DiscardBehaviour Behaviour  { get; set; } = DiscardBehaviour.Discard;

        public void Enqueue(TaskHelper? taskHelper)
        {
            if (taskHelper == null) return;
            
            foreach (var item in Items)
            {
                if (!TrySearchItemInInventory(item, out var foundItem) || foundItem.Count <= 0) continue;
                foreach (var fItem in foundItem)
                {
                    taskHelper.Enqueue(() => OpenInventoryItemContext(fItem));
                    taskHelper.Enqueue(() => ClickDiscardContextMenu(taskHelper));
                    taskHelper.DelayNext(500);
                }
            }
        }

        private bool? ClickDiscardContextMenu(TaskHelper? taskHelper)
        {
            if (!Throttler.Throttle("AutoDiscard", 100)) return false;
            if (ContextMenu == null || !IsAddonAndNodesReady(ContextMenu)) return false;

            switch (Behaviour)
            {
                case DiscardBehaviour.Discard:
                    if (!ClickContextMenu(LuminaCache.GetRow<Addon>(91)!.Value.Text.ExtractText()))
                    {
                        ContextMenu->Close(true);
                        break;
                    }

                    taskHelper.Enqueue(ConfirmDiscard, "ConfirmDiscard", null, null, 1);
                    break;
                case DiscardBehaviour.Sell:
                    if (IsAddonAndNodesReady(GetAddonByName("RetainerGrid0")) || IsAddonAndNodesReady(RetainerSellList))
                    {
                        if (!ClickContextMenu(LuminaCache.GetRow<Addon>(5480)!.Value.Text.ExtractText()))
                        {
                            ContextMenu->Close(true);
                            ChatError(GetLoc("AutoDiscard-NoSellPage"));

                            taskHelper.Abort();
                        }

                        break;
                    }

                    if (IsAddonAndNodesReady(Shop))
                    {
                        if (!ClickContextMenu(LuminaCache.GetRow<Addon>(93)!.Value.Text.ExtractText()))
                        {
                            ContextMenu->Close(true);
                            ChatError(GetLoc("AutoDiscard-NoSellPage"));

                            taskHelper.Abort();
                        }

                        break;
                    }

                    ContextMenu->Close(true);
                    ChatError(GetLoc("AutoDiscard-NoSellPage"));

                    taskHelper.Abort();
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

        public static bool operator !=(DiscardItemsGroup lhs, DiscardItemsGroup rhs) { return !(lhs == rhs); }
    }

    private class Config : ModuleConfiguration
    {
        public List<DiscardItemsGroup> DiscardGroups = [];
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
                {
                    using (ImRaii.Group())
                    {
                        foreach (var item in group.Items.TakeLast(15))
                        {
                            var itemData = LuminaCache.GetRow<Item>(item);
                            if (itemData == null) continue;

                            var itemIcon = DService.Texture.GetFromGameIcon(new(itemData.Value.Icon)).GetWrapOrDefault();
                            if (itemIcon == null) continue;

                            ImGui.Image(itemIcon.ImGuiHandle, new(ImGui.GetTextLineHeightWithSpacing()));
                            ImGui.SameLine();
                        }
                    }
                }
            }
            else
                ImGui.Text(GetLoc("AutoDiscard-NoItemInGroupHelp"));
        }

        if (ImGui.IsItemClicked())
            ImGui.OpenPopup("ItemsEdit");

        using var popup = ImRaii.Popup("ItemsEdit");
        if (!popup) return;

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
                    var specificItemNullable = LuminaCache.GetRow<Item>(item);
                    if (specificItemNullable == null) continue;
                    var specificItem = specificItemNullable.Value;
                    var specificItemIcon = DService.Texture.GetFromGameIcon(new(specificItem.Icon)).GetWrapOrDefault();
                    if (specificItemIcon == null) continue;

                    if (!string.IsNullOrWhiteSpace(SelectedItemSearchInput) &&
                        !specificItem.Name.ExtractText().Contains(SelectedItemSearchInput, StringComparison.OrdinalIgnoreCase)) continue;

                    if (ImGuiOm.SelectableImageWithText(specificItemIcon.ImGuiHandle,
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
                    var itemIcon = DService.Texture.GetFromGameIcon(new(item.Icon)).GetWrapOrDefault();
                    if (itemIcon == null) continue;

                    if (ImGuiOm.SelectableImageWithText(itemIcon.ImGuiHandle,
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

        ImGui.BeginDisabled(TaskHelper.IsBusy);
        ImGui.SameLine();
        if (ImGuiOm.ButtonIcon($"Copy_{index}", FontAwesomeIcon.Copy, GetLoc("Copy")))
        {
            var newGroup = new DiscardItemsGroup(GenerateUniqueName(group.UniqueName))
            {
                Behaviour = group.Behaviour,
                Items = group.Items,
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

    #endregion
}
