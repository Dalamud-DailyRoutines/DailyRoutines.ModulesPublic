using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.Interop.Game.Models.Packets.Upstream;
using OmenTools.OmenService;
using Action = Lumina.Excel.Sheets.Action;
using ActionKind = FFXIVClientStructs.FFXIV.Client.UI.Agent.ActionKind;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoTenChiJin : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoTenChiJinTitle"),
        Description = Lang.Get("AutoTenChiJinDescription"),
        Category    = ModuleCategory.Action
    };

    private static readonly CompSig                     IsSlotUsableSig = new("48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 0F B6 F2 48 8B D9 41 8B F8");
    private delegate        byte                        IsSlotUsableDelegate(RaptureHotbarModule.HotbarSlot* slot, RaptureHotbarModule.HotbarSlotType type, uint id);
    private                 Hook<IsSlotUsableDelegate>? IsSlotUsableHook;

    private Config                         cofig = null!;
    private AddonDRNinJutsuActionsPreview? addon;

    private readonly HashSet<uint> usedMudraActions = [];

    protected override void Init()
    {
        cofig =   Config.Load(this) ?? new();
        TaskHelper   ??= new() { TimeoutMS = 2_000 };

        addon ??= new()
        {
            InternalName = "DRNinJutsuActionsPreview",
            Title        = LuminaWrapper.GetActionName(2260),
            Size         = new(430f, 110f)
        };

        UseActionManager.Instance().RegPreUseAction(OnPreUseAction);
        GamePacketManager.Instance().RegPreSendPacket(OnPreSendActionPacket);

        IsSlotUsableHook ??= IsSlotUsableSig.GetHook<IsSlotUsableDelegate>(IsSlotUsableDetour);
        IsSlotUsableHook.Enable();
    }
    
    protected override void Uninit()
    {
        ResetRuntimeState();

        UseActionManager.Instance().Unreg(OnPreUseAction);
        GamePacketManager.Instance().Unreg(OnPreSendActionPacket);

        addon?.Dispose();
        addon = null;
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Mode")}:");

        using (ImRaii.PushIndent())
        {
            DrawModeOption(TenChiJinMode.Auto, Lang.Get("AutoMode"));

            ImGui.SameLine();
            DrawModeOption(TenChiJinMode.Manual, Lang.Get("ManualMode"));
        }
        
        if (cofig.Mode == TenChiJinMode.Auto)
        {
            ImGui.NewLine();
            
            if (ImGui.Button(Lang.Get("AutoTenChiJin-OpenNijutsuActionsAddon")))
                addon.Toggle();

            ImGui.NewLine();

            if (ImGui.Checkbox(Lang.Get("SendNotification"), ref cofig.SendNotification))
                cofig.Save(this);

            if (ImGui.Checkbox(Lang.Get("AutoTenChiJin-AutoCastNinJiTsu"), ref cofig.AutoCastNinJiTsu))
                cofig.Save(this);
        }
    }

    private void DrawModeOption(TenChiJinMode mode, string label)
    {
        if (!ImGui.RadioButton(label, cofig.Mode == mode))
            return;

        if (cofig.Mode == mode)
            return;

        cofig.Mode = mode;
        ResetRuntimeState();
        cofig.Save(this);
    }

    private byte IsSlotUsableDetour(RaptureHotbarModule.HotbarSlot* slot, RaptureHotbarModule.HotbarSlotType type, uint id)
    {
        if (cofig.Mode != TenChiJinMode.Auto || type != RaptureHotbarModule.HotbarSlotType.Action || !NinjutsuActions.Contains(id))
            return IsSlotUsableHook!.Original(slot, type, id);

        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null || localPlayer->ClassJob != 30)
            return 0;

        if (localPlayer->StatusManager.HasStatus(1186))
            return (byte)(TenChiJinSequence.ContainsKey(id) ? 1 : 0);

        var actionManager = ActionManager.Instance();
        var charges       = actionManager->GetCurrentCharges(2261);
        var cooldownLeft  = 21 - actionManager->GetRecastTimeElapsed(ActionType.Action, 2261);

        slot->CostType        = 3;
        slot->CostDisplayMode = 1;
        slot->CostValue       = (uint)(charges != 0 ? charges : cooldownLeft <= 1 ? 1 : cooldownLeft);

        return (byte)(charges > 0 || localPlayer->StatusManager.HasStatus(497) ? 1 : 0);
    }

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
        if (cofig.Mode != TenChiJinMode.Auto)
            return;

        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null || localPlayer->ClassJob != 30)
            return;

        if (actionType != ActionType.Action || !NinjutsuActions.Contains(actionID))
            return;

        if (TaskHelper.IsBusy)
        {
            if (cofig.AutoCastNinJiTsu)
                isPrevented = true;
            return;
        }

        var actionManager = ActionManager.Instance();
        if (actionManager == null)
            return;

        var actionStatus = actionManager->GetActionStatus(actionType, actionID);

        if (localPlayer->StatusManager.HasStatus(1186))
        {
            if (actionStatus is 579 or 582)
                EnqueueAutomaticSequence(actionID);
            return;
        }

        var ninjutsuCharges = actionManager->GetCurrentCharges(2261);
        var mudraReady      = actionManager->IsActionOffCooldown(ActionType.Action, 2261);
        var isKassatsu      = localPlayer->StatusManager.HasStatus(497);

        if (ninjutsuCharges == 0 && !mudraReady && !isKassatsu)
            return;

        if (actionStatus is 572 or 582)
            EnqueueAutomaticSequence(actionID);
    }

    private void OnPreSendActionPacket(ref bool isPrevented, int opcode, ref nint packet, ref bool isPrioritize)
    {
        if (cofig.Mode != TenChiJinMode.Manual || opcode != UpstreamOpcode.UseActionOpcode)
            return;

        if (LocalPlayerState.ClassJob != 30)
            return;

        var data = (UseActionPacket*)packet;

        if (MudraStartActions.Contains(data->ActionID))
        {
            usedMudraActions.Clear();
            usedMudraActions.Add(data->ActionID % 2259 / 2 + 18805);
            return;
        }

        if (MudraProcessActions.Contains(data->ActionID))
        {
            if (!usedMudraActions.Add(data->ActionID))
                isPrevented = true;
            return;
        }

        if (NinjutsuActions.Contains(data->ActionID))
            usedMudraActions.Clear();
    }

    private void EnqueueAutomaticSequence(uint actionID)
    {
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null)
            return;

        if (localPlayer->StatusManager.HasStatus(1186))
        {
            if (!TenChiJinSequence.TryGetValue(actionID, out var tenChiJinSequence))
                return;

            NotifyNinjutsu(actionID);
            TaskHelper.Abort();

            foreach (var ninjutsu in tenChiJinSequence)
            {
                TaskHelper.Enqueue
                (() =>
                    {
                        if (TargetManager.Target is not { } target)
                            return false;

                        if (ActionManager.Instance()->GetActionStatus(ActionType.Action, ninjutsu) != 0)
                            return false;

                        UseActionManager.Instance().UseActionLocation(ActionType.Action, ninjutsu, target.EntityID);
                        return true;
                    }
                );
            }

            return;
        }

        if (!NormalSequence.TryGetValue(actionID, out var sequence) || !LuminaGetter.TryGetRow<Action>(actionID, out var row))
            return;

        var hasKassatsuReplacement = Kassatsu.TryGetValue(actionID, out var kassatsuActionID);
        var isKassatsuActive       = localPlayer->StatusManager.HasStatus(497);

        NotifyNinjutsu(actionID);
        TaskHelper.Abort();

        foreach (var (mudraActionID, index) in sequence.Select((value, index) => (value, index)))
        {
            if (index == 0)
            {
                var kassatsuMudraActionID = mudraActionID % 2259 / 2 + 18805;
                var actualMudraActionID   = isKassatsuActive ? kassatsuMudraActionID : mudraActionID;

                TaskHelper.Enqueue(() => ActionManager.Instance()->GetActionStatus(ActionType.Action, actualMudraActionID) == 0);
                TaskHelper.Enqueue(() => SendMudraAction(localPlayer, actualMudraActionID));
            }
            else TaskHelper.Enqueue(() => SendMudraAction(localPlayer, mudraActionID));

            TaskHelper.DelayNext(300);
        }

        if (!cofig.AutoCastNinJiTsu)
            return;

        TaskHelper.Enqueue
        (() =>
            {
                var finalActionID = !isKassatsuActive || !hasKassatsuReplacement ? actionID : kassatsuActionID;
                return ActionManager.Instance()->GetAdjustedActionId(2260) == finalActionID;
            }
        );

        TaskHelper.Enqueue
        (() =>
            {
                var finalActionID = !isKassatsuActive || !hasKassatsuReplacement ? actionID : kassatsuActionID;
                return UseActionManager.Instance().UseActionLocation
                (
                    ActionType.Action,
                    finalActionID,
                    row.CanTargetHostile && TargetManager.Target is { } target
                        ? target.EntityID
                        : localPlayer->EntityId
                );
            }
        );
    }

    private void NotifyNinjutsu(uint actionID)
    {
        if (cofig.SendNotification)
            NotifyHelper.Instance().NotificationInfo(Lang.Get("AutoTenChiJin-Notification", LuminaWrapper.GetActionName(actionID)));
    }

    private static bool SendMudraAction(BattleChara* localPlayer, uint actionID)
    {
        new UseActionPacket(ActionType.Action, actionID, localPlayer->EntityId, localPlayer->Rotation).Send();
        ActionManager.Instance()->StartCooldown(ActionType.Action, actionID);
        return true;
    }

    private void ResetRuntimeState()
    {
        usedMudraActions.Clear();
        TaskHelper?.Abort();
    }
    
    private sealed class AddonDRNinJutsuActionsPreview : NativeAddon
    {
        protected override void OnSetup(AtkUnitBase* addon)
        {
            var flexGrid = new HorizontalFlexNode
            {
                Width          = 60 * NormalSequence.Count,
                AlignmentFlags = FlexFlags.FitContentHeight | FlexFlags.CenterVertically | FlexFlags.CenterHorizontally,
                Position       = new(20, 40),
                IsVisible      = true
            };

            foreach (var actionID in NormalSequence.Keys)
            {
                if (!LuminaGetter.TryGetRow<Action>(actionID, out var action))
                    continue;

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
                        Int2 = (int)actionID
                    },
                    IsClickable = false,
                    OnRollOver  = node => node.ShowTooltip(AtkTooltipManager.AtkTooltipType.Action, ActionKind.Action),
                    OnRollOut   = node => node.HideTooltip()
                };

                flexGrid.AddNode(dragDropNode);
                flexGrid.AddDummy();
            }

            flexGrid.AttachNode(this);
        }
    }

    private sealed class Config : ModuleConfig
    {
        public TenChiJinMode Mode             = TenChiJinMode.Auto;
        public bool          AutoCastNinJiTsu = true;
        public bool          SendNotification = true;
    }

    private enum TenChiJinMode
    {
        Auto,
        Manual
    }

    #region 预设数据

    private static readonly FrozenDictionary<uint, uint[]> NormalSequence = new Dictionary<uint, uint[]>()
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
    }.ToFrozenDictionary();

    private static readonly FrozenDictionary<uint, uint> Kassatsu = new Dictionary<uint, uint>()
    {
        // 火遁 → 劫火灭却之术
        [2266] = 16491,
        // 冰遁 → 冰晶乱流之术
        [2268] = 16492
    }.ToFrozenDictionary();

    private static readonly FrozenDictionary<uint, uint[]> TenChiJinSequence = new Dictionary<uint, uint[]>()
    {
        // 风遁 → 人地天
        [2269] = [18875, 18877, 18879],
        // 土遁 → 天人地
        [2270] = [18873, 18878, 18880],
        // 水遁 → 天地人
        [2271] = [18873, 18877, 18881]
    }.ToFrozenDictionary();

    private static readonly FrozenSet<uint> NinjutsuActions     = [2265, 2266, 2267, 2268, 2269, 2270, 2271, 16491, 16492];
    private static readonly FrozenSet<uint> MudraStartActions   = [2259, 2261, 2263];
    private static readonly FrozenSet<uint> MudraProcessActions = [18805, 18806, 18807];

    #endregion
}
