using System.Collections.Generic;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoMateriaTransmutation : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = GetLoc("AutoMateriaTransmutationTitle"),
        Description         = GetLoc("AutoMateriaTransmutationDescription"),
        Category            = ModuleCategories.UIOperation,
        ModulesPrerequisite = ["AutoCutsceneSkip", "AutoTalkSkip"]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private static Config ModuleConfig = null!;

    private static string ItemSearchInput = string.Empty;

    private static TextButtonNode? OperateButtonNode;

    protected override void Init()
    {
        TaskHelper ??= new() { TimeLimitMS = 15_000 };
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "TradeMultiple", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "TradeMultiple", OnAddon);
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        OnAddon(AddonEvent.PreFinalize, null);
    }
    
    protected override void ConfigUI()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("AutoMateriaTransmutation-BlacklistMateria")}");

        using var indent = ImRaii.PushIndent();
        ImGui.SetNextItemWidth(300f * GlobalFontScale);
        if (MultiSelectCombo(PresetSheet.Materias, ref ModuleConfig.BlacklistedItems, ref ItemSearchInput,
                             [
                                 ("物品", ImGuiTableColumnFlags.WidthStretch, 0)
                             ],
                             [
                                 x => () =>
                                 {
                                     var itemIcon = DService.Texture.GetFromGameIcon(new(x.Icon)).GetWrapOrDefault();
                                     if (itemIcon == null) return;

                                     if (ImGuiOm.SelectableImageWithText(
                                             itemIcon.Handle, new(ImGui.GetTextLineHeightWithSpacing()), $"{x.Name.ExtractText()}",
                                             ModuleConfig.BlacklistedItems.Contains(x.RowId),
                                             ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.DontClosePopups))
                                     {
                                         if (!ModuleConfig.BlacklistedItems.Remove(x.RowId))
                                         {
                                             ModuleConfig.BlacklistedItems.Add(x.RowId);
                                             SaveConfig(ModuleConfig);
                                         }
                                     }
                                 },
                             ],
                             [
                                 x => x.Name.ExtractText(),
                                 x => x.RowId.ToString()
                             ],
                             true))
            ModuleConfig.Save(this);
    }
    
    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PostDraw:
                var addon = InfosOm.TradeMultiple;
                if (addon == null) return;
                
                if (OperateButtonNode == null)
                {
                    addon->RootNode->SetWidth(400);
                    addon->WindowNode->SetWidth(400);
                    
                    for (var i = 0; i < addon->WindowNode->Component->UldManager.NodeListCount; i++)
                    {
                        var node = addon->WindowNode->Component->UldManager.NodeList[i];
                        if (node == null) continue;

                        if (node->Width == 286)
                            node->SetWidth(386);
                        
                        if (node->Width == 300)
                            node->SetWidth(400);
                        
                        if (node->X == 267)
                            node->SetPositionFloat(367, 6);
                    }

                    var selectedTextNode = addon->GetTextNodeById(2);
                    if (selectedTextNode != null)
                        selectedTextNode->SetWidth(378);
                    
                    var infoTextNode = addon->GetTextNodeById(3);
                    if (infoTextNode != null)
                        infoTextNode->SetWidth(378);

                    var listNode = addon->GetComponentListById(4);
                    if (listNode != null)
                    {
                        listNode->OwnerNode->SetWidth(378);
                        for (var i = 0; i < listNode->UldManager.NodeListCount; i++)
                        {
                            var node = listNode->UldManager.NodeList[i];
                            if (node == null) continue;
                            
                            if (node->Width == 278)
                                node->SetWidth(378);
                        }

                        for (var i = 0; i < listNode->GetItemCount(); i++)
                        {
                            var listItem = listNode->GetItemRenderer(i);
                            if (listItem == null) continue;

                            var textNode0 = listItem->UldManager.SearchNodeById(2);
                            if (textNode0 != null)
                                textNode0->SetWidth(322);
                            
                            var textNode1 = listItem->UldManager.SearchNodeById(3);
                            if (textNode1 != null)
                            {
                                textNode1->SetWidth(41);
                                textNode1->SetPositionFloat(311, 0);
                            }
                        }
                    }
                    
                    var anotherButton = addon->GetComponentButtonById(5);
                    if (anotherButton != null)
                        anotherButton->OwnerNode->SetPositionFloat(220, 166);

                    OperateButtonNode = new()
                    {
                        Size      = new(150, 28),
                        Position  = new(40, 166),
                        IsVisible = true,
                        SeString  = GetLoc("Start"),
                        OnClick = () =>
                        {
                            if (TaskHelper.IsBusy)
                                TaskHelper.Abort();
                            else
                                Enqueue();
                        },
                        IsEnabled = true,
                    };
                    OperateButtonNode.AttachNode(addon->RootNode);
                }

                OperateButtonNode.String = GetLoc(TaskHelper.IsBusy ? "Stop" : "AutoMateriaTransmutation-BatchTransmutate");

                break;
            case AddonEvent.PreFinalize:
                OperateButtonNode?.DetachNode();
                OperateButtonNode = null;
                break;
        }
    }

    private void Enqueue()
    {
        TaskHelper.Enqueue(() =>
        {
            if (!Throttler.Throttle("AutoMateriaTransmutation-SelectMaterias", 100)) return false;

            var agent = AgentTradeMultiple.Instance();
            if (agent == null) return false;

            if (agent->IsAllMateriaSelected()) return true;

            // 没魔晶石可合成了
            if (!TryFindFirstMateriaSlot(out var type, out var slot))
            {
                TaskHelper.Abort();
                return true;
            }

            var leftCount = 5 - agent->GetCurrentSelectedMateriaCount();
            var item = InventoryManager.Instance()->GetInventorySlot(type, slot);
            if (item == null) return false;

            agent->AddMateria(type, slot, item->Quantity >= leftCount ? leftCount : (uint)item->Quantity);
            return agent->IsAllMateriaSelected();
        });

        TaskHelper.Enqueue(() =>
        {
            if (!Throttler.Throttle("AutoMateriaTransmutation-Start", 100)) return false;

            var agent = AgentTradeMultiple.Instance();
            if (agent == null) return false;

            // 执行到这一步但发现没有选完魔晶石
            if (!agent->IsAllMateriaSelected())
            {
                TaskHelper.Abort();
                
                Enqueue();
                return true;
            }

            agent->StartTransmutation();
            return true;
        });

        TaskHelper.DelayNext(250);
        TaskHelper.Enqueue(Enqueue);
    }

    private static bool TryFindFirstMateriaSlot(out InventoryType inventoryType, out ushort inventorySlot)
    {
        inventoryType = InventoryType.Inventory1;
        inventorySlot = 0;

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null) return false;

        var agent = AgentTradeMultiple.Instance();
        if (agent == null) return false;

        foreach (var type in PlayerInventories)
        {
            var container = inventoryManager->GetInventoryContainer(type);
            if (container == null) return false;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0 || ModuleConfig.BlacklistedItems.Contains(slot->ItemId)) continue;

                var data = LuminaGetter.GetRow<Item>(slot->ItemId);
                if (data is not { FilterGroup: 13 }) continue;

                var isItemInSelected = agent->IsMateriaSelected(slot);
                if (isItemInSelected) continue;

                inventoryType = type;
                inventorySlot = (ushort)i;
                return true;
            }
        }

        return false;
    }
    
    private class Config : ModuleConfiguration
    {
        public HashSet<uint> BlacklistedItems = [];
    }
}
