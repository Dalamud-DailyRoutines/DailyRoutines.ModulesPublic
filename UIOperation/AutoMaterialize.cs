using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoMaterialize : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoMaterializeTitle"),
        Description = Lang.Get("AutoMaterializeDescription"),
        Category    = ModuleCategory.UIOperation
    };
    
    // 0 - 成功; 3 - 获取 InventoryType 或 InventorySlot 失败; 4 - 物品为空或不符合条件; 34 - 当前状态无法使用; 
    private static readonly CompSig ExtractMateriaSig = new("48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 41 0F BF F8 8B DA 48 8B F1 45 33 C0");
    private delegate        int     ExtractMateriaDelegate(nint a1, InventoryType type, uint slot);
    private                 Hook<ExtractMateriaDelegate>? ExtractMateriaHook;

    private static readonly CompSig MaterializeControllerSig = new("48 8D 0D ?? ?? ?? ?? 8B D0 E8 ?? ?? ?? ?? 83 7F");

    private TextNode?       lableNode;
    private TextButtonNode? startButtonNode;
    private TextButtonNode? stopButtonNode;

    protected override void Init()
    {
        TaskHelper ??= new();
        
        ExtractMateriaHook ??= ExtractMateriaSig.GetHook<ExtractMateriaDelegate>(ExtractMateriaDetour);
        ExtractMateriaHook.Enable();
        
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "Materialize",       OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "Materialize",       OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "MaterializeDialog", OnDialogAddon);

        if (MaterializeDialog != null)
            OnDialogAddon(AddonEvent.PostSetup, null);

        CommandManager.Instance().AddSubCommand(COMMAND, new((_, _) => StartARoundAll()) { HelpMessage = Lang.Get("AutoMaterializeTitle") });
    }
    
    protected override void Uninit()
    {
        CommandManager.Instance().RemoveSubCommand(COMMAND);

        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
        OnAddon(AddonEvent.PreFinalize, null);

        DService.Instance().AddonLifecycle.UnregisterListener(OnDialogAddon);
    }

    protected override void ConfigUI() => ImGuiOm.ConflictKeyText();

    private void StartARoundAll()
    {
        if (TaskHelper.IsBusy) return;

        TaskHelper.Enqueue(StartARound, "开始精炼全部装备");
    }

    private bool StartARound()
    {
        if (TaskHelper.AbortByConflictKey(this)) return true;

        if (!Throttler.Shared.Throttle("AutoMaterialize-Execute")) return false;
        if (!IsEnvironmentValid()) return false;

        var manager = InventoryManager.Instance();

        foreach (var type in Inventories.PlayerWithArmory)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0) continue;
                if (slot->SpiritbondOrCollectability < 10_000) continue;
                if (!LuminaGetter.TryGetRow<Item>(slot->ItemId, out var itemData) ||
                    itemData.EquipSlotCategory.Value.RowId == 0)
                    continue;

                var itemName = itemData.Name.ToString();
                TaskHelper.Enqueue(() => ExtractMateria(type, (uint)i) == 0, $"开始精炼单件装备 {itemName}({slot->ItemId})");
                TaskHelper.Enqueue
                (
                    () => NotifyHelper.Instance().Chat(Lang.GetSe("AutoMaterialize-Notice-ExtractNow", SeString.CreateItemLink(itemData, slot->IsHighQuality()))),
                    $"通知精制进度 {itemName}({slot->ItemId})"
                );
                TaskHelper.DelayNext(1_000, $"等待精制完成 {itemName}({slot->ItemId})");
                TaskHelper.Enqueue(StartARound, $"开始下一轮精制 本轮: {itemName}({slot->ItemId})");
                return true;
            }
        }

        NotifyHelper.Instance().NotificationInfo(Lang.Get("AutoMaterialize-Notice-ExtractFinish"));
        NotifyHelper.Instance().Chat(Lang.Get("AutoMaterialize-Notice-ExtractFinish"));

        TaskHelper.Abort();
        return true;
    }

    private bool IsEnvironmentValid()
    {
        if (Inventories.Player.IsFull())
        {
            TaskHelper.Abort();
            return false;
        }

        if (DService.Instance().Condition[ConditionFlag.Mounted])
        {
            TaskHelper.Abort();
            NotifyHelper.Instance().NotificationError(Lang.Get("AutoMaterialize-Notice-OnMount"));
            return false;
        }

        if (DService.Instance().Condition.IsOccupiedInEvent) return false;
        if (DService.Instance().Condition[ConditionFlag.InCombat]) return false;

        return true;
    }

    private int ExtractMateria(InventoryType type, uint slot) =>
        ExtractMateriaHook.Original(MaterializeControllerSig.GetStatic(), type, slot);

    private int ExtractMateriaDetour(nint a1, InventoryType type, uint slot)
    {
        var original = ExtractMateriaHook.Original(a1, type, slot);

        if (!TaskHelper.IsBusy)
            StartARoundAll();

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
                        OnClick   = StartARoundAll
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
                lableNode?.Dispose();
                lableNode = null;

                startButtonNode?.Dispose();
                startButtonNode = null;

                stopButtonNode?.Dispose();
                stopButtonNode = null;

                TaskHelper?.Abort();
                break;
        }
    }

    private static void OnDialogAddon(AddonEvent type, AddonArgs args)
    {
        var addon = MaterializeDialog;
        if (addon == null) return;

        addon->Callback(0);
    }
    
    #region 常量

    private const string COMMAND = "materialize";

    #endregion
}
