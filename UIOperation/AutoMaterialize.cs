using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoMaterialize : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoMaterializeTitle"),
        Description = GetLoc("AutoMaterializeDescription"),
        Category    = ModuleCategories.UIOperation,
    };

    // 0 - 成功; 3 - 获取 InventoryType 或 InventorySlot 失败; 4 - 物品为空或不符合条件; 34 - 当前状态无法使用; 
    private static readonly CompSig                       ExtractMateriaSig = new("E8 ?? ?? ?? ?? 83 7E 20 00 75 5A");
    private delegate        int                           ExtractMateriaDelegate(nint a1, InventoryType type, uint slot);
    private static          Hook<ExtractMateriaDelegate>? ExtractMateriaHook;

    private static readonly CompSig MaterializeControllerSig = new("48 8D 0D ?? ?? ?? ?? 8B D0 E8 ?? ?? ?? ?? 83 7E");

    private static TextNode       LableNode;
    private static TextButtonNode StartButtonNode;
    private static TextButtonNode StopButtonNode;
    
    private const string Command = "materialize";
    
    protected override void Init()
    {
        ExtractMateriaHook ??= ExtractMateriaSig.GetHook<ExtractMateriaDelegate>(ExtractMateriaDetour);
        ExtractMateriaHook.Enable();

        TaskHelper ??= new();

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "Materialize",       OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "Materialize",       OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "MaterializeDialog", OnDialogAddon);

        if (MaterializeDialog != null) 
            OnDialogAddon(AddonEvent.PostSetup, null);

        CommandManager.AddSubCommand(Command, new((_, _) => StartARoundAll()) { HelpMessage = GetLoc("AutoMaterializeTitle") });
    }

    protected override void ConfigUI() => ConflictKeyText();
    
    private void StartARoundAll()
    {
        if (TaskHelper.IsBusy) return;
        
        TaskHelper.Enqueue(StartARound, "开始精炼全部装备");
    }

    private bool? StartARound()
    {
        if (InterruptByConflictKey(TaskHelper, this)) return true;
        
        if (!Throttler.Throttle("AutoMaterialize-Execute")) return false;
        if (!IsEnvironmentValid()) return false;

        var manager = InventoryManager.Instance();
        foreach (var type in PlayerArmoryInventories)
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

                var itemName = itemData.Name.ExtractText();
                TaskHelper.Enqueue(() => ExtractMateria(type, (uint)i) == 0, $"开始精炼单件装备 {itemName}({slot->ItemId})");
                TaskHelper.Enqueue(() => Chat(GetSLoc("AutoMaterialize-Notice-ExtractNow", SeString.CreateItemLink(itemData, slot->IsHighQuality()))), 
                                   $"通知精制进度 {itemName}({slot->ItemId})");
                TaskHelper.DelayNext(1_000, $"等待精制完成 {itemName}({slot->ItemId})");
                TaskHelper.Enqueue(StartARound, $"开始下一轮精制 本轮: {itemName}({slot->ItemId})");
                return true;
            }
        }

        NotificationInfo(GetLoc("AutoMaterialize-Notice-ExtractFinish"));
        Chat(GetLoc("AutoMaterialize-Notice-ExtractFinish"));
        
        TaskHelper.Abort();
        return true;
    }

    private bool IsEnvironmentValid()
    {
        if (IsInventoryFull(PlayerInventories))
        {
            TaskHelper.Abort();
            return false;
        }

        if (DService.Condition[ConditionFlag.Mounted])
        {
            TaskHelper.Abort();
            NotificationError(GetLoc("AutoMaterialize-Notice-OnMount"));
            return false;
        }

        if (OccupiedInEvent) return false;
        if (DService.Condition[ConditionFlag.InCombat]) return false;

        return true;
    }

    private static int ExtractMateria(InventoryType type, uint slot) =>
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
                
                if (LableNode == null)
                {
                    LableNode = new()
                    {
                        IsVisible     = true,
                        Position      = new(135, 8),
                        Size          = new(150, 28),
                        Text          = $"{Info.Title}",
                        FontSize      = 14,
                        AlignmentType = AlignmentType.Right,
                        TextFlags     = TextFlags.AutoAdjustNodeSize | TextFlags.Edge
                    };
                    Service.AddonController.AttachNode(LableNode, Materialize->RootNode);
                }

                if (StartButtonNode == null)
                {
                    StartButtonNode = new()
                    {
                        Position  = new(295, 10),
                        Size      = new(100, 28),
                        IsVisible = true,
                        Label     = GetLoc("Start"),
                        OnClick   = StartARoundAll
                    };
                    Service.AddonController.AttachNode(StartButtonNode, Materialize->RootNode);
                }

                StartButtonNode.IsEnabled = !TaskHelper.IsBusy;
                
                if (StopButtonNode == null)
                {
                    StopButtonNode = new()
                    {
                        Position  = new(400, 10),
                        Size      = new(100, 28),
                        IsVisible = true,
                        Label     = GetLoc("Stop"),
                        OnClick   = () => TaskHelper.Abort()
                    };
                    Service.AddonController.AttachNode(StopButtonNode, Materialize->RootNode);
                }
                break;
            case AddonEvent.PreFinalize:
                Service.AddonController.DetachNode(LableNode);
                LableNode = null;
        
                Service.AddonController.DetachNode(StartButtonNode);
                StartButtonNode = null;
        
                Service.AddonController.DetachNode(StopButtonNode);
                StopButtonNode = null;
                
                TaskHelper.Abort();
                break;
        }
    }

    private static void OnDialogAddon(AddonEvent type, AddonArgs args)
    {
        var addon = MaterializeDialog;
        if (addon == null) return;

        Callback(addon, true, 0);
    }

    protected override void Uninit()
    {
        CommandManager.RemoveSubCommand(Command);
        
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        OnAddon(AddonEvent.PreFinalize, null);
        
        DService.AddonLifecycle.UnregisterListener(OnDialogAddon);

        base.Uninit();
    }
}
