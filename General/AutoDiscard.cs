using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using OmenTools.Dalamud.Attributes;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading.TaskHelper;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoDiscard : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoDiscardTitle"),
        Description = Lang.Get("AutoDiscardDescription"),
        Category    = ModuleCategory.General
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private Config                moduleConfig = null!;
    private LuminaSearcher<Item>? itemSearcher;

    private string     newGroupNameInput        = string.Empty;
    private string     editGroupNameInput       = string.Empty;
    private string     itemSearchInput          = string.Empty;
    private string     selectedItemSearchInput  = string.Empty;
    private string     addItemsByNameInput      = string.Empty;
    private List<Item> lastAddedItemsByName     = [];
    private uint       addItemsByCategoryInput  = 61;
    private List<Item> lastAddedItemsByCategory = [];

    protected override void Init()
    {
        moduleConfig =   Config.Load(this) ?? new();
        TaskHelper   ??= new() { TimeoutMS = 2_000 };

        var itemNames = LuminaGetter.Get<Item>()
                                    .Where
                                    (x => !string.IsNullOrEmpty(x.Name.ToString()) &&
                                          x.ItemSortCategory.RowId != 3            &&
                                          x.ItemSortCategory.RowId != 4
                                    )
                                    .GroupBy(x => x.Name.ToString())
                                    .Select(x => x.First())
                                    .ToList();
        itemSearcher ??= new(itemNames, [x => x.Name.ToString(), x => x.RowId.ToString()]);

        CommandManager.Instance().AddCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("AutoDiscard-CommandHelp") });

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreSetup, "SelectYesno", OnAddon);
    }
    
    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);

        CommandManager.Instance().RemoveCommand(COMMAND);
        itemSearcher = null;

        lastAddedItemsByName.Clear();
        lastAddedItemsByCategory.Clear();
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Command")}:");

        ImGui.SameLine();
        ImGui.TextUnformatted($"{COMMAND} → {Lang.Get("AutoDiscard-CommandHelp")}");

        ImGui.Spacing();

        DrawAddNewGroupButton();

        ImGui.SameLine();

        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.FileImport, Lang.Get("Import")))
        {
            var config = ImportFromClipboard<DiscardItemsGroup>();

            if (config != null)
            {
                moduleConfig.DiscardGroups.Add(config);
                moduleConfig.Save(this);
            }
        }

        var       tableSize = new Vector2(ImGui.GetContentRegionAvail().X - 8f * GlobalUIScale, 0);
        using var table     = ImRaii.Table("DiscardGroupTable", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg, tableSize);
        if (!table) return;

        var orderColumnWidth = ImGui.CalcTextSize((moduleConfig.DiscardGroups.Count + 1).ToString()).X + 24;
        ImGui.TableSetupColumn("Order",      ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, orderColumnWidth);
        ImGui.TableSetupColumn("UniqueName", ImGuiTableColumnFlags.None,                                        20f);
        ImGui.TableSetupColumn("Items",      ImGuiTableColumnFlags.None,                                        80f);
        ImGui.TableSetupColumn("Behaviour",  ImGuiTableColumnFlags.None,                                        30f);
        ImGui.TableSetupColumn("Operations", ImGuiTableColumnFlags.None,                                        30f);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);

        ImGui.TableNextColumn();

        ImGui.TableNextColumn();
        ImGuiOm.Text(Lang.Get("Name"));

        ImGui.TableNextColumn();
        ImGuiOm.Text(Lang.Get("AutoDiscard-ItemsOverview"));

        ImGui.TableNextColumn();
        ImGuiOm.Text(Lang.Get("Mode"));

        ImGui.TableNextColumn();
        ImGuiOm.Text(Lang.Get("Operation"));

        for (var i = 0; i < moduleConfig.DiscardGroups.Count; i++)
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
        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, Lang.Get("Add")))
            ImGui.OpenPopup("AddNewGroupPopup");

        using var popup = ImRaii.Popup("AddNewGroupPopup", ImGuiWindowFlags.AlwaysAutoResize);
        if (!popup) return;

        ImGui.SetNextItemWidth(300f * GlobalUIScale);
        ImGui.InputTextWithHint
        (
            "###NewGroupNameInput",
            Lang.Get("AutoDiscard-AddNewGroupInputNameHelp"),
            ref newGroupNameInput,
            100
        );

        if (ImGui.Button(Lang.Get("Confirm")))
        {
            var info = new DiscardItemsGroup(newGroupNameInput);

            if (!string.IsNullOrWhiteSpace(newGroupNameInput) && !moduleConfig.DiscardGroups.Contains(info))
            {
                moduleConfig.DiscardGroups.Add(info);
                moduleConfig.Save(this);

                newGroupNameInput = string.Empty;
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button(Lang.Get("Cancel")))
            ImGui.CloseCurrentPopup();
    }

    private void UniqueNameColumn(int index)
    {
        if (index < 0 || index > moduleConfig.DiscardGroups.Count) return;

        var       group = moduleConfig.DiscardGroups[index];
        using var id    = ImRaii.PushId(index);

        if (ImGuiOm.SelectableFillCell($"{group.UniqueName}"))
        {
            editGroupNameInput = group.UniqueName;
            ImGui.OpenPopup("EditGroupPopup");
        }

        using var popup = ImRaii.Popup("EditGroupPopup", ImGuiWindowFlags.AlwaysAutoResize);
        if (!popup) return;

        ImGui.SetNextItemWidth(300f * GlobalUIScale);
        ImGui.InputTextWithHint("###EditGroupNameInput", Lang.Get("AutoDiscard-AddNewGroupInputNameHelp"), ref editGroupNameInput, 100);

        if (ImGui.Button(Lang.Get("Confirm")))
        {
            if (!string.IsNullOrWhiteSpace(editGroupNameInput) &&
                !moduleConfig.DiscardGroups.Contains(new(editGroupNameInput)))
            {
                moduleConfig.DiscardGroups
                            .FirstOrDefault(x => x.UniqueName == group.UniqueName)
                            .UniqueName = editGroupNameInput;

                moduleConfig.Save(this);
                editGroupNameInput = string.Empty;

                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button(Lang.Get("Cancel")))
            ImGui.CloseCurrentPopup();
    }

    private void ItemsColumn(int index)
    {
        if (index < 0 || index > moduleConfig.DiscardGroups.Count) return;

        var       group = moduleConfig.DiscardGroups[index];
        using var id    = ImRaii.PushId(index);

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

                        var itemIcon = DService.Instance().Texture.GetFromGameIcon(new(itemData.Value.Icon)).GetWrapOrDefault();
                        if (itemIcon == null) continue;

                        ImGui.Image(itemIcon.Handle, new(ImGui.GetTextLineHeightWithSpacing()));
                        ImGui.SameLine();
                    }
                }
            }
            else
                ImGui.TextUnformatted(Lang.Get("AutoDiscard-NoItemInGroupHelp"));
        }

        if (ImGui.IsItemClicked())
            ImGui.OpenPopup("ItemsEditMenu");

        var popupToOpen = string.Empty;

        using (var popupMenu = ImRaii.Popup("ItemsEditMenu", ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (popupMenu)
            {
                ImGui.TextUnformatted(group.UniqueName);

                ImGui.Separator();
                ImGui.Spacing();

                if (ImGui.MenuItem(Lang.Get("AutoDiscard-AddItemsBatch")))
                {
                    ImGui.CloseCurrentPopup();
                    popupToOpen = "AddItemsBatch";
                }

                if (ImGui.MenuItem(Lang.Get("AutoDiscard-AddItemsManual")))
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
                ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("AutoDiscard-AddItemsByName"));

                using (ImRaii.PushIndent())
                {
                    using (ImRaii.Disabled(string.IsNullOrWhiteSpace(addItemsByNameInput)))
                    {
                        if (ImGui.Button($"{Lang.Get("Add")}##AddItemByName"))
                        {
                            lastAddedItemsByName = itemSearcher.Data
                                                               .Where(x => x.Name.ToString().Contains(addItemsByNameInput, StringComparison.OrdinalIgnoreCase))
                                                               .ToList();
                            lastAddedItemsByName.ForEach(x => group.Items.Add(x.RowId));
                            moduleConfig.Save(this);

                            NotifyHelper.Instance().NotificationSuccess(Lang.Get("AutoDiscard-Notification-ItemsAdded", lastAddedItemsByName.Count));
                        }
                    }

                    ImGui.SameLine();

                    using (ImRaii.Disabled(lastAddedItemsByName.Count == 0))
                    {
                        if (ImGui.Button($"{Lang.Get("Cancel")}##AddItemByName"))
                        {
                            lastAddedItemsByName.ForEach(x => group.Items.Remove(x.RowId));
                            moduleConfig.Save(this);

                            lastAddedItemsByName.Clear();
                        }
                    }

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(300f * GlobalUIScale);
                    ImGui.InputText("###AddByItemName", ref addItemsByNameInput, 128);
                }

                ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("AutoDiscard-AddItemsByCategory"));

                using (ImRaii.PushIndent())
                {
                    using (ImRaii.Disabled(!LuminaGetter.TryGetRow<ItemUICategory>(addItemsByCategoryInput, out _)))
                    {
                        if (ImGui.Button($"{Lang.Get("Add")}##AddItemByCategory"))
                        {
                            lastAddedItemsByCategory = itemSearcher.Data
                                                                   .Where(x => x.ItemUICategory.RowId == addItemsByCategoryInput)
                                                                   .ToList();
                            lastAddedItemsByCategory.ForEach(x => group.Items.Add(x.RowId));
                            moduleConfig.Save(this);

                            NotifyHelper.Instance().NotificationSuccess(Lang.Get("AutoDiscard-Notification-ItemsAdded", lastAddedItemsByCategory.Count));
                        }
                    }

                    ImGui.SameLine();

                    using (ImRaii.Disabled(lastAddedItemsByCategory.Count == 0))
                    {
                        if (ImGui.Button($"{Lang.Get("Cancel")}##AddItemByCategory"))
                        {
                            lastAddedItemsByCategory.ForEach(x => group.Items.Remove(x.RowId));
                            moduleConfig.Save(this);

                            lastAddedItemsByCategory.Clear();
                        }
                    }

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(300f * GlobalUIScale);

                    using (var combo = ImRaii.Combo
                           (
                               "###AddItemsByCategoryCombo",
                               LuminaGetter.TryGetRow<ItemUICategory>(addItemsByCategoryInput, out var uiCategory)
                                   ? uiCategory.Name.ToString()
                                   : string.Empty,
                               ImGuiComboFlags.HeightLarge
                           ))
                    {
                        if (combo)
                        {
                            foreach (var itemUiCategory in LuminaGetter.Get<ItemUICategory>())
                            {
                                var name = itemUiCategory.Name.ToString();
                                if (string.IsNullOrEmpty(name)) continue;
                                if (!ImageHelper.TryGetGameIcon((uint)itemUiCategory.Icon, out var icon)) continue;

                                if (ImGuiOm.SelectableImageWithText
                                    (
                                        icon.Handle,
                                        new(ImGui.GetTextLineHeightWithSpacing()),
                                        name,
                                        addItemsByCategoryInput == itemUiCategory.RowId
                                    ))
                                    addItemsByCategoryInput = itemUiCategory.RowId;
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
                var leftChildSize = new Vector2(300 * GlobalUIScale, 500 * GlobalUIScale);

                using (var leftChild = ImRaii.Child("SelectedItemChild", leftChildSize, true))
                {
                    if (leftChild)
                    {
                        ImGui.SetNextItemWidth(-1f);
                        ImGui.InputTextWithHint("###SelectedItemSearchInput", Lang.Get("PleaseSearch"), ref selectedItemSearchInput, 100);

                        ImGui.Separator();

                        foreach (var item in group.Items)
                        {
                            var specificItemNullable = LuminaGetter.GetRow<Item>(item);
                            if (specificItemNullable == null) continue;
                            var specificItem     = specificItemNullable.Value;
                            var specificItemIcon = DService.Instance().Texture.GetFromGameIcon(new(specificItem.Icon)).GetWrapOrDefault();
                            if (specificItemIcon == null) continue;

                            if (!string.IsNullOrWhiteSpace(selectedItemSearchInput) &&
                                !specificItem.Name.ToString().Contains(selectedItemSearchInput, StringComparison.OrdinalIgnoreCase)) continue;

                            if (ImGuiOm.SelectableImageWithText
                                (
                                    specificItemIcon.Handle,
                                    new(ImGui.GetTextLineHeightWithSpacing()),
                                    specificItem.Name.ToString(),
                                    false,
                                    ImGuiSelectableFlags.DontClosePopups
                                ))
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
                            ImGui.SetCursorPosY(ImGui.GetContentRegionAvail().Y / 2 - 24f);
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
                        if (ImGui.InputTextWithHint("###GameItemSearchInput", Lang.Get("PleaseSearch"), ref itemSearchInput, 100))
                            itemSearcher.Search(itemSearchInput);

                        ImGui.Separator();

                        foreach (var item in itemSearcher.SearchResult)
                        {
                            if (group.Items.Contains(item.RowId)) continue;

                            var itemIcon = DService.Instance().Texture.GetFromGameIcon(new(item.Icon)).GetWrapOrDefault();
                            if (itemIcon == null) continue;

                            if (ImGuiOm.SelectableImageWithText
                                (
                                    itemIcon.Handle,
                                    new(ImGui.GetTextLineHeightWithSpacing()),
                                    item.Name.ToString(),
                                    group.Items.Contains(item.RowId),
                                    ImGuiSelectableFlags.DontClosePopups
                                ))
                            {
                                if (!group.Items.Remove(item.RowId))
                                    group.Items.Add(item.RowId);

                                moduleConfig.Save(this);
                            }
                        }
                    }
                }
            }
        }
    }

    private void BehaviourColumn(int index)
    {
        if (index < 0 || index > moduleConfig.DiscardGroups.Count) return;

        var       group = moduleConfig.DiscardGroups[index];
        using var id    = ImRaii.PushId(index);

        foreach (var behaviourPair in DiscardBehaviourLoc)
        {
            if (ImGui.RadioButton(behaviourPair.Value, behaviourPair.Key == group.Behaviour))
            {
                group.Behaviour = behaviourPair.Key;
                moduleConfig.Save(this);
            }

            ImGui.SameLine();
        }
    }

    private void OperationColumn(int index)
    {
        if (index < 0 || index > moduleConfig.DiscardGroups.Count) return;

        var       group = moduleConfig.DiscardGroups[index];
        using var id    = ImRaii.PushId(index);

        using (ImRaii.Disabled(TaskHelper.IsBusy))
        {
            if (ImGuiOm.ButtonIcon($"Run_{index}", FontAwesomeIcon.Play, Lang.Get("Run")))
                group.Enqueue(TaskHelper);
        }

        ImGui.SameLine();
        if (ImGuiOm.ButtonIcon($"Stop_{index}", FontAwesomeIcon.Stop, Lang.Get("Stop")))
            TaskHelper.Abort();

        using (ImRaii.Disabled(TaskHelper.IsBusy))
        {
            ImGui.SameLine();

            if (ImGuiOm.ButtonIcon($"Copy_{index}", FontAwesomeIcon.Copy, Lang.Get("Copy")))
            {
                var newGroup = new DiscardItemsGroup(GenerateUniqueName(group.UniqueName))
                {
                    Behaviour = group.Behaviour,
                    Items     = group.Items
                };

                moduleConfig.DiscardGroups.Add(newGroup);
                moduleConfig.Save(this);
            }

            ImGui.SameLine();
            if (ImGuiOm.ButtonIcon($"Export_{index}", FontAwesomeIcon.FileExport, Lang.Get("Export")))
                ExportToClipboard(group);

            ImGui.SameLine();

            if (ImGuiOm.ButtonIcon($"Delete_{index}", FontAwesomeIcon.TrashAlt, Lang.Get("HoldCtrlToDelete")))
            {
                if (ImGui.IsKeyDown(ImGuiKey.LeftCtrl))
                {
                    moduleConfig.DiscardGroups.Remove(group);
                    moduleConfig.Save(this);
                }
            }
        }
    }

    #endregion

    private void OnCommand(string command, string arguments) =>
        EnqueueDiscardGroup(arguments.Trim());
    
    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        if (!TaskHelper.IsBusy) return;
        AddonSelectYesnoEvent.ClickYes();
    }

    public void EnqueueDiscardGroup(int index)
    {
        if (index < 0 || index >= moduleConfig.DiscardGroups.Count) return;
        var group = moduleConfig.DiscardGroups[index];
        if (group.Items.Count > 0)
            group.Enqueue(TaskHelper);
    }

    public void EnqueueDiscardGroup(string uniqueName)
    {
        var group = moduleConfig.DiscardGroups.FirstOrDefault(x => x.UniqueName == uniqueName && x.Items.Count > 0);
        if (group == null) return;

        group.Enqueue(TaskHelper);
    }

    private string GenerateUniqueName(string baseName)
    {
        var existingNames = moduleConfig.DiscardGroups.Select(x => x.UniqueName).ToHashSet();

        if (!existingNames.Contains(baseName))
            return baseName;

        var counter    = 0;
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
            counter  = int.Parse(numberPart) + 1;
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
    
    private enum DiscardBehaviour
    {
        Discard,
        Sell
    }

    private class DiscardItemsGroup : IEquatable<DiscardItemsGroup>
    {
        public DiscardItemsGroup() { }

        public DiscardItemsGroup(string name) => UniqueName = name;

        public string           UniqueName { get; set; } = null!;
        public HashSet<uint>    Items      { get; set; } = [];
        public DiscardBehaviour Behaviour  { get; set; } = DiscardBehaviour.Discard;

        public bool Equals(DiscardItemsGroup? other)
        {
            if (other is null || GetType() != other.GetType())
                return false;

            return UniqueName == other.UniqueName;
        }

        public void Enqueue(TaskHelper? taskHelper)
        {
            if (taskHelper == null) return;

            var isAny = false;

            foreach (var item in Items)
            {
                if (!Inventories.Player.TryGetItems(x => x.GetBaseItemId() == item, out var foundItem) || foundItem.Count <= 0) continue;

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
                        taskHelper.Enqueue
                        (() => AgentInventoryContext.Instance()->DiscardItem
                         (
                             itemInventory,
                             type,
                             slot,
                             AgentInventory.Instance()->GetActiveAddonID()
                         )
                        );
                        taskHelper.Enqueue(() => { AddonSelectYesnoEvent.ClickYes(); });
                    }
                    else
                    {
                        taskHelper.Enqueue(() => fItem.OpenContext());
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

        private bool ClickDiscardContextMenu(TaskHelper? taskHelper)
        {
            if (!ContextMenuAddon->IsAddonAndNodesReady()) return false;

            switch (Behaviour)
            {
                case DiscardBehaviour.Discard:
                    if (!AddonContextMenuEvent.Select(LuminaWrapper.GetAddonText(91)))
                    {
                        ContextMenuAddon->Close(true);
                        break;
                    }

                    taskHelper.Enqueue(() => AddonSelectYesnoEvent.ClickYes(), "ConfirmDiscard", weight: 1);
                    break;
                case DiscardBehaviour.Sell:
                    if (!AddonContextMenuEvent.Select(LuminaWrapper.GetAddonText(5480)) &&
                        !AddonContextMenuEvent.Select(LuminaWrapper.GetAddonText(93)))
                    {
                        ContextMenuAddon->Close(true);
                        NotifyHelper.Instance().ChatError(Lang.Get("AutoDiscard-NoSellPage"));

                        taskHelper.Abort();
                    }

                    break;
            }

            return true;
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

    private class Config : ModuleConfig
    {
        public List<DiscardItemsGroup> DiscardGroups = [];
    }

    #region IPC
    
    [IPCProvider("DailyRoutines.Modules.AutoDiscard.IsBusy")]
    private bool IsBusy() =>
        TaskHelper.IsBusy;

    #endregion

    #region 常量

    private const string COMMAND = "/pdrdiscard";

    private static readonly FrozenDictionary<DiscardBehaviour, string> DiscardBehaviourLoc = new Dictionary<DiscardBehaviour, string>
    {
        [DiscardBehaviour.Discard] = LuminaWrapper.GetAddonText(91),
        [DiscardBehaviour.Sell]    = LuminaWrapper.GetAddonText(93)
    }.ToFrozenDictionary();

    #endregion
}
