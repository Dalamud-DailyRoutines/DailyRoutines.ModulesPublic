using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using OmenTools.Threading.TaskHelper;
using AgentReceiveEventDelegate = OmenTools.Interop.Game.Models.Native.AgentReceiveEventDelegate;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedFreeShop : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = Lang.Get("OptimizedFreeShopTitle"),
        Description         = Lang.Get("OptimizedFreeShopDescription"),
        Category            = ModuleCategory.UIOptimization,
        ModulesPrerequisite = ["AutoClaimItemIgnoringMismatchJobAndLevel"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private static readonly CompSig ReceiveEventSig =
        new("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 55 41 56 41 57 48 83 EC 50 4C 8B BC 24 ?? ?? ?? ??");
    private Hook<AgentReceiveEventDelegate>? ReceiveEventHook;

    private Config config = null!;

    private CheckboxNode?       isEnabledNode;
    private HorizontalFlexNode? batchClaimContainerNode;

    private TaskHelper? clickYesnoHelper;

    protected override void Init()
    {
        TaskHelper       ??= new();
        clickYesnoHelper ??= new();

        config = Config.Load(this) ?? new();

        ReceiveEventHook ??= ReceiveEventSig.GetHook<AgentReceiveEventDelegate>(ReceiveEventDetour);
        ReceiveEventHook.Enable();

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "FreeShop", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "FreeShop", OnAddon);
    }
    
    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
        OnAddon(AddonEvent.PreFinalize, null);

        clickYesnoHelper = null;
    }

    private AtkValue* ReceiveEventDetour(AgentInterface* agent, AtkValue* returnValues, AtkValue* values, uint valueCount, ulong eventKind)
    {
        if (config.IsEnabled && eventKind == 0 && values->Int == 0)
        {
            clickYesnoHelper.Abort();
            clickYesnoHelper.Enqueue(() => AddonSelectYesnoEvent.ClickYes());
        }

        return ReceiveEventHook.Original(agent, returnValues, values, valueCount, eventKind);
    }

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PostDraw:
                if (FreeShop == null) return;

                if (isEnabledNode == null)
                {
                    var checkboxNode = FreeShop->GetComponentByNodeId(2);
                    if (checkboxNode == null) return;

                    var textNode = (AtkTextNode*)checkboxNode->UldManager.SearchNodeById(2);
                    if (textNode == null) return;

                    textNode->ResizeNodeForCurrentText();

                    isEnabledNode = new()
                    {
                        Size      = new(160.0f, 28.0f),
                        Position  = new(56 + textNode->Width, 42),
                        IsVisible = true,
                        IsChecked = config.IsEnabled,
                        IsEnabled = true,
                        String    = Lang.Get("OptimizedFreeShop-FastClaim"),
                        OnClick = newState =>
                        {
                            config.IsEnabled = newState;
                            config.Save(this);
                        }
                    };
                    isEnabledNode.Label.TextFlags = (TextFlags)33;
                    isEnabledNode.AttachNode(FreeShop->RootNode);
                }

                if (batchClaimContainerNode == null)
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

                    batchClaimContainerNode = new()
                    {
                        Width          = 40f * itemIDs.Count,
                        Position       = new(160, 5),
                        IsVisible      = true,
                        AlignmentFlags = FlexFlags.FitContentHeight | FlexFlags.CenterHorizontally
                    };

                    foreach (var (classJobCategory, items) in itemIDs)
                    {
                        if (!LuminaGetter.TryGetRow(classJobCategory, out ClassJobCategory categoryData)) continue;
                        if (LuminaGetter.Get<ClassJob>()
                                        .FirstOrDefault(x => x.Name.ToString().Contains(categoryData.Name.ToString(), StringComparison.OrdinalIgnoreCase))
                            is not { RowId: > 0 } classJobData) continue;

                        var icon = classJobData.RowId + 62100;
                        var button = new IconButtonNode
                        {
                            Size        = new(36f),
                            IsVisible   = true,
                            IsEnabled   = true,
                            IconId      = icon,
                            OnClick     = () => BatchClaim(items),
                            TextTooltip = $"{Lang.Get("OptimizedFreeShop-BatchClaim")}: {classJobData.Name}"
                        };

                        batchClaimContainerNode.AddNode(button);
                        batchClaimContainerNode.AddDummy();
                    }

                    batchClaimContainerNode.AttachNode(FreeShop->RootNode);
                }


                break;
            case AddonEvent.PreFinalize:
                isEnabledNode?.Dispose();
                isEnabledNode = null;

                batchClaimContainerNode?.Dispose();
                batchClaimContainerNode = null;

                clickYesnoHelper?.Abort();
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

                TaskHelper.Enqueue(() => AgentId.FreeShop.SendEvent(0, 0, index));
                TaskHelper.DelayNext(10);
            }

            if (anythingNotInBag)
                TaskHelper.Enqueue(() => BatchClaim(itemData));
        }
    }
    
    private class Config : ModuleConfig
    {
        public bool IsEnabled = true;
    }
}
