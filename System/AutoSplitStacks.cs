using DailyRoutines.Common.KamiToolKit.Addons.InputNumeric;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Text;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoSplitStacks : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoSplitStacksTitle"),
        Description = Lang.Get("AutoSplitStacksDescription"),
        Category    = ModuleCategory.System
    };

    private Config config = null!;

    private ItemSelectCombo    itemSelectCombo        = null!;
    private FastSplitItemStack fastSplitItemStackMenu = null!;
    private DRInputNumeric?    drInputNumeric;
    
    private int splitAmountInput = 1;
    
    protected override void Init()
    {
        itemSelectCombo = new
        (
            "Item",
            LuminaGetter.Get<Item>()
                        .Where
                        (x => x.FilterGroup != 16 &&
                              x.StackSize   > 1   &&
                              !string.IsNullOrEmpty(x.Name.ToString())
                        )
                        .GroupBy(x => x.Name.ToString())
                        .Select(x => x.First())
        );
        fastSplitItemStackMenu = new(this);

        TaskHelper = new();
        config     = Config.Load(this) ?? new();

        CommandManager.Instance().AddCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("AutoSplitStacks-CommandHelp") });
        ContextMenuManager.Instance().Reg(fastSplitItemStackMenu);
    }

    protected override void Uninit()
    {
        CommandManager.Instance().RemoveCommand(COMMAND);
        ContextMenuManager.Instance().Unreg(fastSplitItemStackMenu);

        drInputNumeric?.Dispose();
        drInputNumeric = null;
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightBlue.ToVector4(), $"{Lang.Get("Command")}:");

        ImGui.SameLine();
        ImGui.TextUnformatted($"{COMMAND} → {Lang.Get("AutoSplitStacks-CommandHelp")}");

        ImGui.Spacing();

        using var table = ImRaii.Table("SplitItem", 4);
        if (!table) return;
        ImGui.TableSetupColumn("勾选框", ImGuiTableColumnFlags.WidthFixed, ImGui.GetTextLineHeightWithSpacing());
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
                    ImGui.SetNextItemWidth(250f * GlobalUIScale);
                    itemSelectCombo.DrawRadio();

                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Amount")}:");

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(250f * GlobalUIScale);
                    if (ImGui.InputInt("###SplitAmountInput", ref splitAmountInput))
                        splitAmountInput = Math.Clamp(splitAmountInput, 1, 998);
                }

                var itemSize = ImGui.GetItemRectSize();

                ImGui.SameLine();

                using (ImRaii.Disabled(itemSelectCombo.SelectedID == 0))
                {
                    if (ImGuiOm.ButtonIconWithTextVertical
                        (
                            FontAwesomeIcon.Plus,
                            Lang.Get("Add"),
                            buttonSize: new(ImGui.CalcTextSize("四个汉字").X, itemSize.Y)
                        ))
                    {
                        var newGroup = new SplitGroup(itemSelectCombo.SelectedID, splitAmountInput);

                        if (!config.SplitGroups.Contains(newGroup))
                        {
                            config.SplitGroups.Add(newGroup);
                            config.Save(this);
                        }
                    }
                }
            }
        }

        ImGui.TableNextColumn();
        ImGuiOm.Text(Lang.Get("Item"));

        ImGui.TableNextColumn();
        ImGuiOm.Text(Lang.Get("AutoSplitStacks-SplitAmount"));

        foreach (var group in config.SplitGroups.ToList())
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var isEnabled = group.IsEnabled;

            if (ImGui.Checkbox($"###IsEnabled_{group.ItemID}", ref isEnabled))
            {
                var index = config.SplitGroups.IndexOf(group);
                config.SplitGroups[index].IsEnabled = isEnabled;
                config.Save(this);
            }

            ImGui.TableNextColumn();
            if (!LuminaGetter.TryGetRow<Item>(group.ItemID, out var item)) continue;
            var icon = ImageHelper.GetGameIcon(item.Icon);
            var name = item.Name.ToString();
            ImGuiOm.TextImage(name, icon.Handle, ScaledVector2(24f));

            ImGui.TableNextColumn();
            ImGuiOm.Selectable(group.Amount.ToString());

            if (ImGui.BeginPopupContextItem($"{group.ItemID}_AmountEdit"))
            {
                if (ImGui.IsWindowAppearing())
                    splitAmountInput = group.Amount;

                ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Amount")}:");

                ImGui.SameLine();
                ImGui.SetNextItemWidth(150f * GlobalUIScale);
                ImGui.InputInt($"###{group.ItemID}AmountEdit", ref splitAmountInput);

                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    var index = config.SplitGroups.IndexOf(group);
                    config.SplitGroups[index].Amount = splitAmountInput;
                    config.Save(this);
                }

                ImGui.EndPopup();
            }

            ImGui.TableNextColumn();
            if (ImGuiOm.ButtonIcon
                (
                    $"{group.ItemID}_Enqueue",
                    FontAwesomeIcon.Play,
                    Lang.Get("Execute")
                ))
                EnqueueSplitByInfo(group);

            ImGui.SameLine();

            if (ImGuiOm.ButtonIcon($"{group.ItemID}_Delete", FontAwesomeIcon.TrashAlt, Lang.Get("HoldCtrlToDelete")))
            {
                if (ImGui.IsKeyDown(ImGuiKey.LeftCtrl))
                {
                    config.SplitGroups.Remove(group);
                    config.Save(this);
                }
            }
        }
    }

    private void OnCommand
    (
        string command,
        string args
    )
    {
        args = args.Trim();
        if (string.IsNullOrWhiteSpace(args)) return;

        if (int.TryParse(args, out var itemID))
        {
            var group = config.SplitGroups.FirstOrDefault(x => x.ItemID == itemID);
            if (group == null) return;

            EnqueueSplitByInfo(group);
            return;
        }

        var item = LuminaGetter.Get<Item>()
                               .Where(x => x.Name.ToString().Contains(args, StringComparison.OrdinalIgnoreCase))
                               .MinBy(x => x.Name.ToString().Length);

        if (!item.Equals(null))
        {
            var group = config.SplitGroups.FirstOrDefault(x => x.ItemID == item.RowId);
            if (group == null) return;

            EnqueueSplitByInfo(group);
        }
    }

    private void EnqueueSplitByInfo
    (
        SplitGroup group
    ) =>
        EnqueueSplitByInfo(group.ItemID, group.Amount);

    private void EnqueueSplitByInfo
    (
        uint itemID,
        int  count
    ) =>
        TaskHelper.Enqueue(() => EnqueueSplit(itemID, count, 0));

    private bool EnqueueSplit
    (
        uint itemID,
        int  count,
        int  finishRound
    )
    {
        if (itemID == 0 || count == 0) return true;

        var manager = InventoryManager.Instance();

        if (Inventories.Player.IsFull())
        {
            var fullMessage = Lang.Get("AutoSplitStacks-Notification-FullInventory");
            NotifyHelper.ToastError(fullMessage);
            NotifyHelper.Chat
            (
                new XivChatEntry
                {
                    Type    = XivChatType.ErrorMessage,
                    Message = fullMessage
                }
            );
            
            return true;
        }

        foreach (var type in Inventories.Player)
        {
            var  container = manager->GetInventoryContainer(type);
            if (container == null) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null) continue;

                if (slot->GetBaseItemId() == itemID)
                {
                    if (slot->GetQuantity() > count)
                    {
                        manager->SplitItem(container->Type, (ushort)slot->Slot, count);
                        TaskHelper.Enqueue(() => EnqueueSplit(itemID, count, finishRound + 1));
                        return true;
                    }
                }
            }
        }
        
        if (finishRound == 0)
        {
            var noFoundMessage = Lang.Get("AutoSplitStacks-Notification-ItemNoFound");
            NotifyHelper.ToastError(noFoundMessage);
            NotifyHelper.Chat
            (
                new XivChatEntry
                {
                    Type    = XivChatType.ErrorMessage,
                    Message = noFoundMessage
                }
            );
        }
        else
        {
            var finishMessage = Lang.Get("AutoSplitStacks-Notification-Finished", finishRound, count);
            NotifyHelper.Toast(finishMessage);
            NotifyHelper.Instance().Chat(finishMessage);
        }
        
        return true;
    }

    private class Config : ModuleConfig
    {
        public List<SplitGroup> SplitGroups = [];
    }

    private class SplitGroup : IEquatable<SplitGroup>
    {
        public SplitGroup() { }

        public SplitGroup
        (
            uint itemID,
            int  amount
        )
        {
            ItemID = itemID;
            Amount = amount;
        }

        public bool IsEnabled { get; set; } = true;
        public uint ItemID    { get; set; }
        public int  Amount    { get; set; }

        public bool Equals
        (
            SplitGroup? other
        )
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;

            return ItemID == other.ItemID;
        }

        public override bool Equals
        (
            object? obj
        )
        {
            if (obj is null) return false;
            if (ReferenceEquals(this, obj)) return true;

            return obj.GetType() == GetType() && Equals((SplitGroup)obj);
        }

        public override int GetHashCode() => (int)ItemID;
    }

    private sealed class FastSplitItemStack
    (
        AutoSplitStacks module
    ) : ContextMenuEntry
    {
        public override string Identifier =>
            nameof(AutoSplitStacks);

        public override ContextMenuItem? Create
        (
            ContextMenuOpenedArgs args
        )
        {
            if (args.TargetInventoryItem is not { } item) 
                return null;

            var quantity = item.GetQuantity();
            if (quantity < 2) return null;

            return new()
            {
                Name = Lang.Get("AutoSplitStacks-ContextMenu-Split"),
                OnClicked = clickedArgs =>
                {
                    if (module.drInputNumeric != null &&
                        !AddonHelper.TryGetPtrByName("DRInputNumeric", out _))
                    {
                        try
                        {
                            module.drInputNumeric?.Dispose();
                            module.drInputNumeric = null;
                        }
                        catch
                        {
                            // 谁敢猜这个时候会发生什么
                        }
                    }

                    if (module.drInputNumeric != null)
                        return;
                    
                    module.drInputNumeric = DRInputNumeric.Open
                    (
                        new()
                        {
                            Prompt = Lang.Get("AutoSplitStacks-Popup-PleaseInput"),
                            Value  = 1,
                            Min    = 1,
                            Max    = (int)Math.Min(998, quantity),
                            Callback = (addon, result) =>
                            {
                                module.drInputNumeric = null;

                                if (result != DRInputNumericResult.Confirmed)
                                    return;

                                module.EnqueueSplitByInfo(args.TargetItemID, addon.Value);
                            },
                            Position = new
                            (
                                args.Addon->RootNode->GetNodeState().Center,
                                AddonPositionAlignment.TopCenter
                            ),
                        }
                    );
                }
            };
        }
    }

    #region 常量

    private const string COMMAND = "/pdrsplit";

    #endregion
}
