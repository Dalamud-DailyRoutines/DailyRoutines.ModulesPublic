using System.Collections.Generic;
using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using Action = Lumina.Excel.Sheets.Action;
using ActionKind = FFXIVClientStructs.FFXIV.Client.UI.Agent.ActionKind;

namespace DailyRoutines.ModulesPublic;

// TODO: 整合成一个忍术模块
public unsafe class AutoTenChiJin : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title           = GetLoc("AutoTenChiJinTitle"),
        Description     = GetLoc("AutoTenChiJinDescription"),
        Category        = ModuleCategories.Action,
        ModulesConflict = ["AutoThrottleTenChiJin"]
    };
    
    private static readonly Dictionary<uint, List<uint>> NormalSequence = new()
    {
        // 风魔手里剑 → 天
        [2265] = [2259],
        // 火遁 → 地天
        [2266] = [2261, 18805],
        // 雷遁 → 天地
        [2267] = [2259, 18806],
        // 冰遁 → 天人
        [2268] = [2259, 18807],
        // 风遁 → 人地天
        [2269] = [2263, 18806, 18805],
        // 土遁 → 天人地
        [2270] = [2259, 18807, 18806],
        // 水遁 → 天地人
        [2271] = [2259, 18806, 18807]
    };

    private static readonly Dictionary<uint, uint> Kassatsu = new()
    {
        // 火遁 → 劫火灭却之术
        [2266] = 16491,
        // 冰遁 → 冰晶乱流之术
        [2268] = 16492,
    };
    
    private static readonly Dictionary<uint, List<uint>> TenChiJinSequence = new()
    {
        // 风遁 → 人地天
        [2269] = [18875, 18877, 18879],
        // 土遁 → 天人地
        [2270] = [18873, 18878, 18880],
        // 水遁 → 天地人
        [2271] = [18873, 18877, 18881]
    };

    private static readonly HashSet<uint> NinJiTsuActions = [2265, 2266, 2267, 2268, 2269, 2270, 2271, 16491, 16492];

    private static Config ModuleConfig = null!;

    private static readonly CompSig                     IsSlotUsableSig = new("48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 0F B6 F2 48 8B D9 41 8B F8");
    private delegate        byte                        IsSlotUsableDelegate(RaptureHotbarModule.HotbarSlot* slot, RaptureHotbarModule.HotbarSlotType type, uint id);
    private static          Hook<IsSlotUsableDelegate>? IsSlotUsableHook;

    private static AddonDRNinJutsuActionsPreview? Addon;

    protected override void Init()
    {
        ModuleConfig =   LoadConfig<Config>() ?? new();
        TaskHelper   ??= new() { TimeLimitMS = 2_000 };

        Addon ??= new()
        {
            InternalName = "DRNinJutsuActionsPreview",
            Title        = LuminaWrapper.GetActionName(2260),
            Size         = new(430f, 110f),
        };

        UseActionManager.RegPreUseAction(OnPreUseAction);

        IsSlotUsableHook ??= IsSlotUsableSig.GetHook<IsSlotUsableDelegate>(IsSlotUsableDetour);
        IsSlotUsableHook.Enable();
    }

    protected override void ConfigUI()
    {
        if (ImGui.Button(GetLoc("AutoTenChiJin-OpenNijutsuActionsAddon")))
            Addon.Toggle();
        
        ImGui.NewLine();
        
        if (ImGui.Checkbox(GetLoc("SendNotification"), ref ModuleConfig.SendNotification))
            SaveConfig(ModuleConfig);
        
        if (ImGui.Checkbox(GetLoc("AutoTenChiJin-AutoCastNinJiTsu"), ref ModuleConfig.AutoCastNinJiTsu))
            SaveConfig(ModuleConfig);
    }

    private static byte IsSlotUsableDetour(RaptureHotbarModule.HotbarSlot* slot, RaptureHotbarModule.HotbarSlotType type, uint id)
    {
        if (type != RaptureHotbarModule.HotbarSlotType.Action || !NinJiTsuActions.Contains(id)) 
            return IsSlotUsableHook.Original(slot, type, id);

        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null || localPlayer->ClassJob != 30) return 0;

        // 天地人
        if (localPlayer->StatusManager.HasStatus(1186))
            return (byte)(TenChiJinSequence.ContainsKey(id) ? 1 : 0);

        var charges      = ActionManager.Instance()->GetCurrentCharges(2261);
        // 稍微有点延迟所以加一秒
        var cooldownLeft = 21 - ActionManager.Instance()->GetRecastTimeElapsed(ActionType.Action, 2261);

        slot->CostType        = 3;
        slot->CostDisplayMode = 1;
        slot->CostValue       = (uint)(charges != 0 ? charges : cooldownLeft <= 1 ? 1 : cooldownLeft);

        return (byte)(charges > 0 || localPlayer->StatusManager.HasStatus(497) ? 1 : 0);
    }

    private void OnPreUseAction(
        ref bool                        isPrevented,
        ref ActionType                  actionType, ref uint actionID,     ref ulong targetID, ref uint extraParam,
        ref ActionManager.UseActionMode queueState, ref uint comboRouteID)
    {
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null || localPlayer->ClassJob != 30) return;
        
        if (actionType != ActionType.Action || !NinJiTsuActions.Contains(actionID)) return;
        if (TaskHelper.IsBusy)
        {
            // 避免搓兔子
            if (ModuleConfig.AutoCastNinJiTsu)
                isPrevented = true;
            return;
        }

        var manager = ActionManager.Instance();
        if (manager == null) return;
        
        var actionStatus = manager->GetActionStatus(actionType, actionID);

        // 天地人状态期间
        if (localPlayer->StatusManager.HasStatus(1186))
        {
            if (actionStatus is 579 or 582)
                EnqueueTenChiJin(actionID);
        }
        else
        {
            // 没层数了且没生杀
            var ninJiTsuCharges = manager->GetCurrentCharges(2261);
            var cooldown        = manager->IsActionOffCooldown(ActionType.Action, 2261);
            var isKassatsu      = localPlayer->StatusManager.HasStatus(497);
            if (ninJiTsuCharges == 0 && !cooldown && !isKassatsu) return;
            
            if (actionStatus is 572 or 582)
                EnqueueTenChiJin(actionID);
        }
    }

    private void EnqueueTenChiJin(uint actionID)
    {
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return;

        // 天地人状态期间
        if (localPlayer->StatusManager.HasStatus(1186))
        {
            if (!TenChiJinSequence.TryGetValue(actionID, out var sequence)) return;
            
            if (ModuleConfig.SendNotification) 
                NotificationInfo(GetLoc("AutoTenChiJin-Notification", LuminaWrapper.GetActionName(actionID)));
            TaskHelper.Abort();
            
            foreach (var ninJiTsu in sequence)
            {
                TaskHelper.Enqueue(() =>
                {
                    if (DService.Targets.Target is not { } target) return false;
                    if (ActionManager.Instance()->GetActionStatus(ActionType.Action, ninJiTsu) != 0) return false;
                    UseActionManager.UseActionLocation(ActionType.Action, ninJiTsu, target.EntityID);
                    return true;
                });
            }
        }
        else
        {
            if (!NormalSequence.TryGetValue(actionID, out var sequence) ||
                !LuminaGetter.TryGetRow<Action>(actionID, out var row)) return;

            var isNeedTransformed = Kassatsu.TryGetValue(actionID, out var kassatsu);
            var isKassatsu        = localPlayer->StatusManager.HasStatus(497);
            
            if (ModuleConfig.SendNotification) 
                NotificationInfo(GetLoc("AutoTenChiJin-Notification", LuminaWrapper.GetActionName(actionID)));
            TaskHelper.Abort();
            
            for (var i = 0; i < sequence.Count; i++)
            {
                var ninJiTsu = sequence[i];
                if (i == 0)
                {
                    var ninJiTsuKassatsu = (ninJiTsu % 2259 / 2) + 18805;
                    var finalAction      = isKassatsu ? ninJiTsuKassatsu : ninJiTsu;
                    
                    TaskHelper.Enqueue(() => ActionManager.Instance()->GetActionStatus(ActionType.Action, finalAction) == 0);
                    TaskHelper.Enqueue(() =>
                    {
                        new UseActionPacket(ActionType.Action, finalAction, localPlayer->EntityId, localPlayer->Rotation).Send();
                        ActionManager.Instance()->StartCooldown(ActionType.Action, finalAction);
                    });
                }
                else
                {
                    TaskHelper.Enqueue(() =>
                    {
                        new UseActionPacket(ActionType.Action, ninJiTsu, localPlayer->EntityId, localPlayer->Rotation).Send();
                        ActionManager.Instance()->StartCooldown(ActionType.Action, ninJiTsu);
                    });
                }
                
                TaskHelper.DelayNext(300);
            }
            
            if (ModuleConfig.AutoCastNinJiTsu)
            {
                // 等待忍术变成目标技能以防网卡
                TaskHelper.Enqueue(() =>
                {
                    // 处理生杀
                    var finalActionID = !isKassatsu || !isNeedTransformed ? actionID : kassatsu;
                    return ActionManager.Instance()->GetAdjustedActionId(2260) == finalActionID;
                });
                TaskHelper.Enqueue(() =>
                {
                    var finalActionID = !isKassatsu || !isNeedTransformed ? actionID : kassatsu;
                    return UseActionManager.UseActionLocation(ActionType.Action, finalActionID,
                                                              row.CanTargetHostile && DService.Targets.Target is { } target
                                                                  ? target.EntityID
                                                                  : localPlayer->EntityId);
                });
            }
        }
        
    }

    protected override void Uninit()
    {
        UseActionManager.Unreg(OnPreUseAction);
        
        Addon?.Dispose();
        Addon = null;
    }

    private class AddonDRNinJutsuActionsPreview : NativeAddon
    {
        protected override void OnSetup(AtkUnitBase* addon)
        {
            var flexGrid = new HorizontalFlexNode
            {
                Width          = 60 * NormalSequence.Count,
                AlignmentFlags = FlexFlags.FitContentHeight | FlexFlags.CenterVertically | FlexFlags.CenterHorizontally,
                Position       = new(20, 40),
                IsVisible      = true,
            };

            foreach (var actionID in NormalSequence.Keys)
            {
                if (!LuminaGetter.TryGetRow<Action>(actionID, out var action)) continue;

                var dragDropNode = new DragDropNode
                {
                    Size         = new(44f),
                    IsVisible    = true,
                    IconId       = action.Icon,
                    AcceptedType = DragDropType.Everything,
                    IsDraggable  = true,
                    Payload = new()
                    {
                        Type = DragDropType.Action,
                        Int2 = (int)actionID,
                    },
                    IsClickable = false,
                    OnRollOver  = node => node.ShowTooltip(AtkTooltipManager.AtkTooltipType.Action, ActionKind.Action),
                    OnRollOut   = node => node.HideTooltip(),
                };
                
                flexGrid.AddNode(dragDropNode);
                flexGrid.AddDummy();
            }
            
            flexGrid.AttachNode(this);
        }
    }

    private class Config : ModuleConfiguration
    {
        public bool SendNotification = true;
        public bool AutoCastNinJiTsu = true;
    }
}
