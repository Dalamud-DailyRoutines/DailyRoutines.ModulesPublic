using System;
using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedFreeShop : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = GetLoc("OptimizedFreeShopTitle"),
        Description         = GetLoc("OptimizedFreeShopDescription"),
        Category            = ModuleCategories.UIOptimization,
        ModulesPrerequisite = ["AutoClaimItemIgnoringMismatchJobAndLevel"]
    };

    private static readonly CompSig ReceiveEventSig =
        new("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 55 41 56 41 57 48 83 EC 50 4C 8B BC 24 ?? ?? ?? ??");
    private static Hook<AgentReceiveEventDelegate>? ReceiveEventHook;

    private static Config ModuleConfig = null!;

    private static CheckboxNode? IsEnabledNode;

    private static HorizontalFlexNode? BatchClaimContainerNode;

    private static TaskHelper? ClickYesnoHelper;

    protected override void Init()
    {
        TaskHelper       ??= new();
        ClickYesnoHelper ??= new();
        
        ModuleConfig = LoadConfig<Config>() ?? new();

        ReceiveEventHook ??= ReceiveEventSig.GetHook<AgentReceiveEventDelegate>(ReceiveEventDetour);
        ReceiveEventHook.Enable();
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "FreeShop", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "FreeShop", OnAddon);
    }

    private static AtkValue* ReceiveEventDetour(AgentInterface* agent, AtkValue* returnValues, AtkValue* values, uint valueCount, ulong eventKind)
    {
        if (ModuleConfig.IsEnabled && eventKind == 0 && values->Int == 0)
        {
            ClickYesnoHelper.Abort();
            ClickYesnoHelper.Enqueue(() => ClickSelectYesnoYes());
        }

        return ReceiveEventHook.Original(agent, returnValues, values, valueCount, eventKind);
    }

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PostDraw:
                if (FreeShop == null) return;

                if (IsEnabledNode == null)
                {
                    var checkboxNode = FreeShop->GetComponentByNodeId(2);
                    if (checkboxNode == null) return;

                    var textNode = (AtkTextNode*)checkboxNode->UldManager.SearchNodeById(2);
                    if (textNode == null) return;
                    
                    textNode->ResizeNodeForCurrentText();
                    
                    IsEnabledNode = new()
                    {
                        Size      = new(160.0f, 28.0f),
                        Position  = new(56 + textNode->Width, 42),
                        IsVisible = true,
                        IsChecked = ModuleConfig.IsEnabled,
                        IsEnabled = true,
                        LabelText = GetLoc("OptimizedFreeShop-FastClaim"),
                        OnClick = newState =>
                        {
                            ModuleConfig.IsEnabled = newState;
                            ModuleConfig.Save(this);
                        },
                    };
                    IsEnabledNode.Label.TextFlags = (TextFlags)33;
                    Service.AddonController.AttachNode(IsEnabledNode, FreeShop->RootNode);
                }

                if (BatchClaimContainerNode == null)
                {
                    var itemCount = FreeShop->AtkValues[3].UInt;
                    var itemIDs   = new Dictionary<uint, List<(int Index, uint ID)>>();
                    for (var i = 0; i < itemCount; i++)
                    {
                        var itemID = FreeShop->AtkValues[65 + i].UInt;
                        if (!LuminaGetter.TryGetRow(itemID, out Item itemData)) continue;

                        itemIDs.TryAdd(itemData.ClassJobCategory.RowId, []);
                        itemIDs[itemData.ClassJobCategory.RowId].Add((i, itemID));
                    }

                    BatchClaimContainerNode = new()
                    {
                        Width          = 40f * itemIDs.Count,
                        Position       = new(160, 5),
                        IsVisible      = true,
                        AlignmentFlags = FlexFlags.FitContentHeight | FlexFlags.CenterHorizontally,
                    };

                    foreach (var (classJobCategory, items) in itemIDs)
                    {
                        if (!LuminaGetter.TryGetRow(classJobCategory, out ClassJobCategory categoryData)) continue;
                        if (LuminaGetter.Get<ClassJob>()
                                        .FirstOrDefault(x => x.Name.ExtractText().Contains(categoryData.Name.ExtractText(), StringComparison.OrdinalIgnoreCase)) 
                            is not { RowId: > 0 } classJobData) continue;

                        var icon = classJobData.RowId + 62100;
                        var button = new IconButtonNode
                        {
                            Size      = new(36f),
                            IsVisible = true,
                            IsEnabled = true,
                            IconId    = icon,
                            OnClick   = () => BatchClaim(items),
                            Tooltip   = $"{GetLoc("OptimizedFreeShop-BatchClaim")}: {classJobData.Name}",
                        };
                        
                        BatchClaimContainerNode.AddNode(button);
                        BatchClaimContainerNode.AddDummy();
                    }
                    
                    Service.AddonController.AttachNode(BatchClaimContainerNode, FreeShop->RootNode);
                }

                
                break;
            case AddonEvent.PreFinalize:
                Service.AddonController.DetachNode(IsEnabledNode);
                IsEnabledNode = null;
                
                Service.AddonController.DetachNode(BatchClaimContainerNode);
                BatchClaimContainerNode = null;
                
                ClickYesnoHelper?.Abort();
                break;
        }

        return;

        void BatchClaim(List<(int Index, uint ID)> itemData)
        {
            TaskHelper.Abort();

            var anythingNotInBag = false;
            foreach (var (index, itemID) in itemData)
            {
                if (LocalPlayerState.GetItemCount(itemID) > 0) continue;

                anythingNotInBag = true;
                                    
                TaskHelper.Enqueue(() => SendEvent(AgentId.FreeShop, 0, 0, index));
                TaskHelper.DelayNext(10);
            }

            if (anythingNotInBag)
                TaskHelper.Enqueue(() => BatchClaim(itemData));
        }
    }
    
    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        OnAddon(AddonEvent.PreFinalize, null);

        ClickYesnoHelper = null;
    }

    private class Config : ModuleConfiguration
    {
        public bool IsEnabled = true;
    }
}
