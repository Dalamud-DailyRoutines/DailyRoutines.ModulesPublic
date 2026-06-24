using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe class AutoMaterialize : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoMaterializeTitle"),
        Description = Lang.Get("AutoMaterializeDescription"),
        Category    = ModuleCategory.Interface
    };
    
    // 0 - 成功; 3 - 获取 InventoryType 或 InventorySlot 失败; 4 - 物品为空或不符合条件; 9 - 当前 Condition 不满足; 34 - 当前状态无法使用; 
    private static readonly CompSig ExtractMateriaSig = new("48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 41 0F BF F8 8B DA 48 8B F1 45 33 C0");
    private delegate        int     ExtractMateriaDelegate(nint a1, InventoryType type, uint slot);
    private                 Hook<ExtractMateriaDelegate>? ExtractMateriaHook;
    
    private TextNode?       lableNode;
    private TextButtonNode? startButtonNode;
    private TextButtonNode? stopButtonNode;

    protected override void Init()
    {
        TaskHelper = new()
        {
            ShowDebug       = true,
            TaskIntervalMS  = 500,
            RetryIntervalMS = 500
        };
        
        ExtractMateriaHook = ExtractMateriaSig.GetHook<ExtractMateriaDelegate>(ExtractMateriaDetour);
        ExtractMateriaHook.Enable();
        
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "Materialize",       OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "Materialize",       OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "MaterializeDialog", OnDialogAddon);
        if (MaterializeDialog != null)
            OnDialogAddon(AddonEvent.PostSetup, null);

        LogMessageManager.Instance().RegPost(OnLogMessage);
        UseActionManager.Instance().RegPreUseAction(OnPreUseAction);

        CommandManager.Instance().AddSubCommand(COMMAND, new((_, _) => Enqueue()) { HelpMessage = Lang.Get("AutoMaterializeTitle") });
    }

    protected override void Uninit()
    {
        CommandManager.Instance().RemoveSubCommand(COMMAND);
        LogMessageManager.Instance().Unreg(OnLogMessage);
        UseActionManager.Instance().Unreg(OnPreUseAction);
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon, OnDialogAddon);
        
        lableNode?.Dispose();
        lableNode = null;

        startButtonNode?.Dispose();
        startButtonNode = null;

        stopButtonNode?.Dispose();
        stopButtonNode = null;
    }

    protected override void ConfigUI() => 
        ImGuiOm.ConflictKeyText();

    #region 事件

    private void OnPreUseAction
    (
        ref bool                        isPrevented,
        ref ActionType                  actionType,
        ref uint                        actionID,
        ref ulong                       targetID,
        ref uint                        extraParam,
        ref ActionManager.UseActionMode queueState,
        ref uint                        comboRouteID
    )
    {
        // 精制魔晶石的技能
        if (actionType == ActionType.Action && actionID == 2469) return;
        TaskHelper.Abort();
    }

    private void OnLogMessage(uint logMessageID, LogMessageQueueItem item)
    {
        if (logMessageID != 744) return;
        Enqueue();
    }
    
    private int ExtractMateriaDetour(nint a1, InventoryType type, uint slot)
    {
        var original = ExtractMateriaHook.Original(a1, type, slot);
        Enqueue();
        return original;
    }

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PostDraw:
                if (Materialize == null) return;

                if (lableNode == null)
                {
                    lableNode = new()
                    {
                        IsVisible     = true,
                        Position      = new(135, 8),
                        Size          = new(150, 28),
                        String        = $"{Info.Title}",
                        FontSize      = 14,
                        AlignmentType = AlignmentType.Right,
                        TextFlags     = TextFlags.AutoAdjustNodeSize | TextFlags.Edge
                    };
                    lableNode.AttachNode(Materialize->RootNode);
                }

                if (startButtonNode == null)
                {
                    startButtonNode = new()
                    {
                        Position  = new(295, 10),
                        Size      = new(100, 28),
                        IsVisible = true,
                        String    = Lang.Get("Start"),
                        OnClick   = Enqueue
                    };
                    startButtonNode.AttachNode(Materialize->RootNode);
                }

                startButtonNode.IsEnabled = !TaskHelper.IsBusy;

                if (stopButtonNode == null)
                {
                    stopButtonNode = new()
                    {
                        Position  = new(400, 10),
                        Size      = new(100, 28),
                        IsVisible = true,
                        String    = Lang.Get("Stop"),
                        OnClick   = () => TaskHelper.Abort()
                    };
                    stopButtonNode.AttachNode(Materialize->RootNode);
                }

                break;
            case AddonEvent.PreFinalize:
                lableNode       = null;
                startButtonNode = null;
                stopButtonNode  = null;

                TaskHelper.Abort();
                break;
        }
    }

    private static void OnDialogAddon(AddonEvent type, AddonArgs args)
    {
        var addon = MaterializeDialog;
        if (addon == null) return;

        addon->Callback(0);
    }

    #endregion

    private void Enqueue()
    {
        if (TaskHelper.IsBusy) return;
        TaskHelper.Enqueue(EnqueueExtractMateria, "精炼装备");
    }

    private bool EnqueueExtractMateria()
    {
        if (TaskHelper.AbortByConflictKey(this)) 
            return true;
        
        if (Inventories.Player.IsFull() || ICondition.Instance()[ConditionFlag.InCombat])
        {
            TaskHelper.Abort();
            return true;
        }

        if (!Conditions.Instance()->HasPermission(133))
            return false;
        
        if (!Inventories.PlayerWithArmory.TryGetFirstItem
            (
                slot => slot.GetBaseItemId() is var itemID               &&
                        LuminaGetter.TryGetRow(itemID, out Item itemRow) &&
                        slot.SpiritbondOrCollectability >= 10_000        &&
                        itemRow.EquipSlotCategory.RowId > 0,
                out var inventorySlot
            ))
        {
            var finishMessage = Lang.Get("AutoMaterialize-Notice-ExtractFinish");
            NotifyHelper.Instance().NotificationInfo(finishMessage);
            NotifyHelper.Instance().Chat(finishMessage);
            
            TaskHelper.Abort();
            return true;
        }

        TaskHelper.Enqueue
        (
            () =>
            {
                var result = ExtractMateriaHook.Original(nint.Zero, inventorySlot->GetInventoryType(), inventorySlot->GetSlot());

                if (result == 0)
                {
                    var message = Lang.GetSe
                    (
                        "AutoMaterialize-Notice-ExtractNow",
                        SeString.CreateItemLink(inventorySlot->GetBaseItemId(), inventorySlot->IsHighQuality())
                    );
                    NotifyHelper.Instance().Chat(message);
                    NotifyHelper.ToastQuest
                    (
                        message,
                        new()
                        {
                            IconId = LuminaWrapper.GetItemIconID(inventorySlot->GetBaseItemId())
                        }
                    );

                    return true;
                }
                
                return false;
            },
            $"精炼魔晶石: {LuminaWrapper.GetItemName(inventorySlot->GetBaseItemId())} ({inventorySlot->GetBaseItemId()})"
        );
        
        TaskHelper.Enqueue
        (
            EnqueueExtractMateria,
            "进行下一轮魔晶石精炼"
        );
        
        return true;
    }
    
    #region 常量

    private const string COMMAND = "materialize";

    #endregion
}
