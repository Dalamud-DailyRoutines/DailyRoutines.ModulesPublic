using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Interface;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoSplitStacks : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoSplitStacksTitle"),
        Description = GetLoc("AutoSplitStacksDescription"),
        Category    = ModuleCategories.General,
    };
    
    private const string Command = "/pdrsplit";
    private static readonly Vector2 CheckboxSize = ScaledVector2(20f);

    private static Config ModuleConfig = null!;

    private static LuminaSearcher<Item>? ItemSearcher;
    
    private static Item? SelectedItem;
    private static string ItemSearchInput = string.Empty;
    private static int SplitAmountInput = 1;

    private static readonly FastSplitItemStack FastSplitItemStackMenu = new();

    private static uint FastSplitItemID;

    private static bool IsNeedToOpen;

    protected override void Init()
    {
        TaskHelper   = new();
        ModuleConfig = LoadConfig<Config>() ?? new();

        ItemSearcher ??= new(LuminaGetter.Get<Item>()
                                        .Where(x => x.FilterGroup != 16 &&
                                                    x.StackSize   > 1   &&
                                                    !string.IsNullOrEmpty(x.Name.ExtractText()))
                                        .GroupBy(x => x.Name.ExtractText())
                                        .Select(x => x.First()),
                             [x => x.Name.ExtractText(), x => x.RowId.ToString()], x => x.Name.ExtractText());

        CommandManager.AddCommand(Command, new(OnCommand) { HelpMessage = GetLoc("AutoSplitStacks-CommandHelp") });
        DService.ContextMenu.OnMenuOpened += OnMenuOpened;

        DService.UiBuilder.Draw += OnDraw;
    }

    private void OnDraw()
    {
        var popupName = $"{Lang.Get("AutoSplitStacks-FastSplit")}###FastSplitPopup";
        
        if (IsNeedToOpen && !ImGui.IsPopupOpen(popupName))
            ImGui.OpenPopup(popupName);

        var isOpen = true;
        if (ImGui.BeginPopupModal(popupName, ref isOpen, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar))
        {
            ImGui.Text($"{Lang.Get("AutoSplitStacks-PleaseInputSplitAmount")}:");

            ImGui.SetNextItemWidth(150f * GlobalFontScale);
            if (ImGui.InputInt("###FastSplitAmountInput", ref SplitAmountInput))
                SplitAmountInput = Math.Clamp(SplitAmountInput, 1, 998);

            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("Confirm")))
            {
                EnqueueSplit(FastSplitItemID, SplitAmountInput);

                ImGui.CloseCurrentPopup();
                IsNeedToOpen = false;
            }

            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("Cancel")))
            {
                ImGui.CloseCurrentPopup();
                IsNeedToOpen = false;
            }

            ImGui.EndPopup();
        }
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightBlue.ToVector4(), $"{GetLoc("Command")}:");

        ImGui.SameLine();
        ImGui.Text($"{Command} → {GetLoc("AutoSplitStacks-CommandHelp")}");

        ImGui.Spacing();

        using var table = ImRaii.Table("SplitItem", 4);
        if (!table) return;
        ImGui.TableSetupColumn("勾选框", ImGuiTableColumnFlags.WidthFixed, CheckboxSize.X);
        ImGui.TableSetupColumn("名称",  ImGuiTableColumnFlags.None,       30);
        ImGui.TableSetupColumn("数量",  ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("四个汉字").X);
        ImGui.TableSetupColumn("操作",  ImGuiTableColumnFlags.None,       10);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableNextColumn();
        if (ImGuiOm.SelectableIconCentered("AddNewGroup", FontAwesomeIcon.Plus))
            ImGui.OpenPopup("AddNewGroupPopup");

        using (var popup = ImRaii.Popup("AddNewGroupPopup"))
        {
            if (popup)
            {
                using (ImRaii.Group())
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Item")}:");

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(250f * GlobalFontScale);
                    using (var combo = ImRaii.Combo("###ItemSelectCombo",
                                                    SelectedItem == null ? "" : SelectedItem.Value.Name.ExtractText(),
                               ImGuiComboFlags.HeightLarge))
                    {
                        if (combo)
                        {
                            ImGui.SetNextItemWidth(-1f);
                            ImGui.InputTextWithHint("###ItemSearchInput", Lang.Get("PleaseSearch"),
                                                    ref ItemSearchInput, 100);
                            if (ImGui.IsItemDeactivatedAfterEdit())
                                ItemSearcher.Search(ItemSearchInput);

                            foreach (var item in ItemSearcher.SearchResult)
                            {
                                var icon = ImageHelper.GetGameIcon(item.Icon).Handle;
                                if (ImGuiOm.SelectableImageWithText(icon, new(ImGui.GetTextLineHeightWithSpacing()),
                                                                    item.Name.ExtractText(), item.Equals(SelectedItem)))
                                    SelectedItem = item;
                            }
                        }
                    }

                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Amount")}:");

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(250f * GlobalFontScale);
                    if (ImGui.InputInt("###SplitAmountInput", ref SplitAmountInput))
                        SplitAmountInput = Math.Clamp(SplitAmountInput, 1, 998);
                }

                var itemSize = ImGui.GetItemRectSize();

                ImGui.SameLine();
                using (ImRaii.Disabled(SelectedItem == null))
                {
                    if (ImGuiOm.ButtonIconWithTextVertical(FontAwesomeIcon.Plus, Lang.Get("Add"),
                                                           buttonSize: new(ImGui.CalcTextSize("三个字").X, itemSize.Y)))
                    {
                        var newGroup = new SplitGroup(SelectedItem!.Value.RowId, SplitAmountInput);
                        if (!ModuleConfig.SplitGroups.Contains(newGroup))
                        {
                            ModuleConfig.SplitGroups.Add(newGroup);
                            SaveConfig(ModuleConfig);
                        }
                    }
                }
            }
        }

        ImGui.TableNextColumn();
        ImGuiOm.Text(Lang.Get("Item"));

        ImGui.TableNextColumn();
        ImGuiOm.Text(Lang.Get("AutoSplitStacks-SplitAmount"));

        foreach (var group in ModuleConfig.SplitGroups.ToList())
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var isEnabled = group.IsEnabled;
            if (ImGui.Checkbox($"###IsEnabled_{group.ItemID}", ref isEnabled))
            {
                var index = ModuleConfig.SplitGroups.IndexOf(group);
                ModuleConfig.SplitGroups[index].IsEnabled = isEnabled;
                SaveConfig(ModuleConfig);
            }

            ImGui.TableNextColumn();
            if (!LuminaGetter.TryGetRow<Item>(group.ItemID, out var item)) continue;
            var icon = ImageHelper.GetGameIcon(item.Icon);
            var name = item.Name.ExtractText();
            ImGuiOm.TextImage(name, icon.Handle, ScaledVector2(24f));

            ImGui.TableNextColumn();
            ImGuiOm.Selectable(group.Amount.ToString());

            if (ImGui.BeginPopupContextItem($"{group.ItemID}_AmountEdit"))
            {
                if (ImGui.IsWindowAppearing())
                    SplitAmountInput = group.Amount;

                ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Amount")}:");

                ImGui.SameLine();
                ImGui.SetNextItemWidth(150f * GlobalFontScale);
                ImGui.InputInt($"###{group.ItemID}AmountEdit", ref SplitAmountInput);
                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    var index = ModuleConfig.SplitGroups.IndexOf(group);
                    ModuleConfig.SplitGroups[index].Amount = SplitAmountInput;
                    SaveConfig(ModuleConfig);
                }

                ImGui.EndPopup();
            }

            ImGui.TableNextColumn();
            if (ImGuiOm.ButtonIcon($"{group.ItemID}_Enqueue", FontAwesomeIcon.Play,
                                   Lang.Get("Execute")))
                EnqueueSplit(group);

            ImGui.SameLine();
            if (ImGuiOm.ButtonIcon($"{group.ItemID}_Delete", FontAwesomeIcon.TrashAlt, GetLoc("HoldCtrlToDelete")))
            {
                if (ImGui.IsKeyDown(ImGuiKey.LeftCtrl))
                {
                    ModuleConfig.SplitGroups.Remove(group);
                    SaveConfig(ModuleConfig);
                }
            }
        }
    }
    
    private void OnCommand(string command, string args)
    {
        args = args.Trim();
        if (string.IsNullOrWhiteSpace(args)) return;

        if (int.TryParse(args, out var itemID))
        {
            var group = ModuleConfig.SplitGroups.FirstOrDefault(x => x.ItemID == itemID);
            if (group == null) return;

            EnqueueSplit(group);
            return;
        }

        var item = LuminaGetter.Get<Item>()
                              .Where(x => x.Name.ExtractText().Contains(args, StringComparison.OrdinalIgnoreCase))
                              .MinBy(x => x.Name.ExtractText().Length);
        if (!item.Equals(null))
        {
            var group = ModuleConfig.SplitGroups.FirstOrDefault(x => x.ItemID == item.RowId);
            if (group == null) return;

            EnqueueSplit(group);
        }
    }

    private static void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.Target is not MenuTargetInventory { TargetItem: not null } iTarget) return;
        if (iTarget.TargetItem.Value.Quantity <= 1) return;

        args.AddMenuItem(FastSplitItemStackMenu.Get());
    }

    private void EnqueueSplit(SplitGroup group)
        => EnqueueSplit(group.ItemID, group.Amount);

    private void EnqueueSplit(uint itemID, int amount)
    {
        TaskHelper.Enqueue(() => ClickItemToSplit(itemID, amount));
        TaskHelper.DelayNext(100, $"SplitRound_{itemID}_{amount}");
        TaskHelper.Enqueue(() => EnqueueSplit(itemID, amount));
    }

    private bool? ClickItemToSplit(uint itemID, int amount)
    {
        if (InputNumeric != null || itemID == 0 || amount == 0)
        {
            TaskHelper.Abort();
            return true;
        }

        var agent = AgentInventoryContext.Instance();
        var manager = InventoryManager.Instance();
        var agentInventory = AgentModule.Instance()->GetAgentByInternalId(AgentId.Inventory);
        var addon = RaptureAtkUnitManager.Instance()->GetAddonById((ushort)agentInventory->AddonId);

        if (agent == null || manager == null || agentInventory == null || addon == null || !addon->IsVisible)
        {
            addon->Open(1);
            return false;
        }

        if (IsInventoryFull(PlayerInventories))
        {
            TaskHelper.Abort();
            NotificationWarning(Lang.Get("AutoSplitStacks-Notification-FullInventory"));
            return true;
        }

        var foundTypes = PlayerInventories.Where(type => manager->GetInventoryContainer(type) != null   &&
                                                         manager->GetInventoryContainer(type)->IsLoaded &&
                                                         manager->GetItemCountInContainer(itemID, type) +
                                                         manager->GetItemCountInContainer(itemID, type, true) > amount)
                                          .ToList();
        if (foundTypes.Count <= 0)
        {
            TaskHelper.Abort();
            NotificationWarning(Lang.Get("AutoSplitStacks-Notification-ItemNoFound"));
            return true;
        }

        foreach (var type in foundTypes)
        {
            var container = manager->GetInventoryContainer(type);
            int? foundSlot = null;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot->ItemId == itemID)
                {
                    if (slot->GetQuantity() > amount)
                    {
                        foundSlot = i;
                        break;
                    }
                }
            }

            if (foundSlot == null) continue;

            agent->OpenForItemSlot(type, (int)foundSlot, 0, agentInventory->AddonId);
            EnqueueOperations(itemID, type, (int)foundSlot, amount);
            return true;
        }

        TaskHelper.Abort();
        NotificationWarning(Lang.Get("AutoSplitStacks-Notification-ItemNoFound"));
        return true;
    }

    private void EnqueueOperations(uint itemID, InventoryType foundType, int foundSlot, int amount)
    {
        TaskHelper.DelayNext(20, $"ContextMenu_{itemID}_{foundType}_{foundSlot}", false, 2);
        TaskHelper.Enqueue(() =>
        {
            ClickContextMenu(LuminaGetter.GetRow<Addon>(92)!.Value.Text.ExtractText());
            return true;
        }, null, null, null, 2);

        TaskHelper.DelayNext(20, $"InputNumeric_{itemID}_{foundType}_{foundSlot}", false, 2);
        TaskHelper.Enqueue(() =>
        {
            if (InputNumeric == null || !IsAddonAndNodesReady(InputNumeric)) return false;

            Callback(InputNumeric, true, amount);
            return true;
        }, null, null, null, 2);
    }

    protected override void Uninit()
    {
        DService.UiBuilder.Draw -= OnDraw;
        
        CommandManager.RemoveCommand(Command);
        DService.ContextMenu.OnMenuOpened -= OnMenuOpened;

        FastSplitItemID = 0;
        IsNeedToOpen    = false;

        base.Uninit();
    }

    private class Config : ModuleConfiguration
    {
        public List<SplitGroup> SplitGroups = [];
    }

    private class SplitGroup : IEquatable<SplitGroup>
    {
        public bool IsEnabled { get; set; } = true;
        public uint ItemID    { get; set; }
        public int  Amount    { get; set; }

        public SplitGroup() { }

        public SplitGroup(uint itemID, int amount)
        {
            ItemID = itemID;
            Amount = amount;
        }

        public bool Equals(SplitGroup? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;

            return ItemID == other.ItemID;
        }

        public override bool Equals(object? obj)
        {
            if (obj is null) return false;
            if (ReferenceEquals(this, obj)) return true;

            return obj.GetType() == GetType() && Equals((SplitGroup)obj);
        }

        public override int GetHashCode() => (int)ItemID;
    }

    private class FastSplitItemStack : MenuItemBase
    {
        public override string Name       { get; protected set; } = Lang.Get("AutoSplitStacks-FastSplit");
        public override string Identifier { get; protected set; } = nameof(AutoSplitStacks);

        protected override bool WithDRPrefix { get; set; } = true;

        protected override void   OnClicked(IMenuItemClickedArgs args)
        {
            if (args.Target is not MenuTargetInventory { TargetItem: not null } iTarget) return;
            if (iTarget.TargetItem.Value.Quantity <= 1) return;

            FastSplitItemID = iTarget.TargetItem.Value.ItemId;
            IsNeedToOpen    = true;
        }
    }
}
