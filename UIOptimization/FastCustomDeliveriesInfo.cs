using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Manager;
using DailyRoutines.Verification;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using AgentReceiveEventDelegate = OmenTools.Interop.Game.Models.Native.AgentReceiveEventDelegate;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace DailyRoutines.ModulesPublic;

public unsafe class FastCustomDeliveriesInfo : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("FastCustomDeliveriesInfoTitle"),
        Description = Lang.Get("FastCustomDeliveriesInfoDescription"),
        Category    = ModuleCategory.UIOptimization
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private static bool IsEligibleForTeleporting =>
        !(GameState.IsCN || GameState.IsTC) || AuthState.IsPremium;

    private Hook<AgentReceiveEventDelegate> AgentSatisfactionListReceiveEventHook;

    private CustomDeliveryInfo? selectedInfo;

    private bool isNeedToRefresh;
    
    protected override void Init()
    {
        Overlay    ??= new(this);
        TaskHelper ??= new() { TimeoutMS = 30_000 };
        
        AgentSatisfactionListReceiveEventHook ??= DService.Instance().Hook.HookFromAddress<AgentReceiveEventDelegate>
        (
            AgentModule.Instance()->GetAgentByInternalId(AgentId.SatisfactionList)->VirtualTable->GetVFuncByName("ReceiveEvent"),
            AgentSatisfactionListReceiveEventDetour
        );
        AgentSatisfactionListReceiveEventHook.Enable();
    }

    protected override void OverlayUI()
    {
        if (selectedInfo == null)
        {
            Overlay.IsOpen = false;
            return;
        }

        using var font = FontManager.Instance().UIFont.Push();

        if (ImGui.IsWindowAppearing() || isNeedToRefresh)
        {
            isNeedToRefresh = false;
            ImGui.SetWindowPos(ImGui.GetMousePos());
        }

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), LuminaWrapper.GetAddonText(8813));

        using (ImRaii.PushIndent())
        {
            using (FontManager.Instance().UIFont120.Push())
                ImGui.TextUnformatted(selectedInfo?.GetRow().Npc.Value.Singular.ToString());
        }

        ImGui.Separator();
        ImGui.Spacing();

        var isNeedToClose = false;

        using (ImRaii.Disabled
               (
                   !IsEligibleForTeleporting &&
                   Sheets.SpeedDetectionZones.ContainsKey(selectedInfo?.Zone ?? 0)
               ))
        {
            if (ImGui.MenuItem(Lang.Get("Teleport")))
            {
                switch (selectedInfo?.Index)
                {
                    // 天穹街
                    case 6 or 7:
                        var posCopy = selectedInfo?.Position ?? default;
                        EnqueueFirmament();
                        TaskHelper.Enqueue(() => MovementManager.Instance().TPSmart_InZone(posCopy, false, true));
                        break;
                    default:
                        MovementManager.Instance().TPSmart_BetweenZone(selectedInfo?.Zone ?? 0, selectedInfo?.Position ?? default);
                        break;
                }

                isNeedToClose = true;
            }
        }

        if (ImGui.MenuItem(Lang.Get("FastCustomDeliveriesInfo-TeleportToZone")))
        {
            switch (selectedInfo?.Index)
            {
                // 天穹街
                case 6 or 7:
                    EnqueueFirmament();
                    break;
                default:
                    MovementManager.Instance().TeleportNearestAetheryte
                    (
                        selectedInfo?.Position ?? default,
                        selectedInfo?.Zone     ?? 0,
                        true
                    );
                    break;
            }

            isNeedToClose = true;
        }

        if (ImGui.MenuItem(LuminaWrapper.GetAddonText(66)))
        {
            var instance = AgentMap.Instance();

            var zoneID = (uint)selectedInfo?.Zone!;
            var mapID  = LuminaGetter.GetRow<TerritoryType>(zoneID)!.Value.Map.RowId;

            instance->SetFlagMapMarker(zoneID, mapID, selectedInfo?.Position ?? default);
            instance->OpenMap(mapID, zoneID, selectedInfo?.Name ?? string.Empty);

            isNeedToClose = true;
        }

        if (ImGui.MenuItem(LuminaWrapper.GetAddonText(1219)) | isNeedToClose)
        {
            Overlay.IsOpen = false;
            selectedInfo   = null;
        }
    }

    private AtkValue* AgentSatisfactionListReceiveEventDetour
    (
        AgentInterface* agent,
        AtkValue*       returnValues,
        AtkValue*       values,
        uint            valueCount,
        ulong           eventKind
    )
    {
        if (agent == null || values == null || valueCount < 1)
            return InvokeOriginal();

        // 非右键
        var valueType = values[0].Int;
        if (valueType != 1)
            return InvokeOriginal();

        var customDeliveryIndex = values[1].UInt;
        if (customDeliveryIndex < 1)
            return InvokeOriginal();

        if (!Infos.TryGetValue(customDeliveryIndex, out var customDeliveryInfo))
            return InvokeOriginal();

        selectedInfo    = customDeliveryInfo;
        isNeedToRefresh = true;
        Overlay.IsOpen  = true;

        var defaultValue = new AtkValue { Type = ValueType.Bool, Bool = false };
        return &defaultValue;

        AtkValue* InvokeOriginal() =>
            AgentSatisfactionListReceiveEventHook.Original(agent, returnValues, values, valueCount, eventKind);
    }

    // 进入天穹街
    private void EnqueueFirmament()
    {
        // 不在天穹街 → 先去伊修加德基础层
        TaskHelper.Enqueue(MovementManager.Instance().TeleportFirmament);
        TaskHelper.Enqueue
        (() => GameState.TerritoryType == 886                        &&
               UIModule.IsScreenReady()                              &&
               !DService.Instance().Condition[ConditionFlag.Jumping] &&
               !MovementManager.Instance().IsManagerBusy
        );
    }

    private record CustomDeliveryInfo
    (
        uint    Index,
        string  Name,
        uint    Zone,
        Vector3 Position
    )
    {
        public SatisfactionNpc GetRow() => 
            LuminaGetter.GetRow<SatisfactionNpc>(Index).GetValueOrDefault();
    }
    
    #region 常量
    
    private static readonly FrozenDictionary<uint, CustomDeliveryInfo> Infos = new Dictionary<uint, CustomDeliveryInfo>
    {
        [11] = new(11, "尼托维凯", 1190, new(-355.7f, 19.6f, -108.7f)),
        [10] = new(10, "玛格拉特", 956, new(-52.8f, -29.5f, -61.5f)),
        [9]  = new(9, "安登", 816, new(-241f, 51f, 615.7f)),
        [8]  = new(8, "阿梅莉安丝", 962, new(223, 25, -193)),
        [7]  = new(7, "狄兰达尔伯爵", 886, new(-112, 0, -135)),
        [6]  = new(6, "艾尔·图", 886, new(110, -20, 0)),
        [5]  = new(5, "凯·希尔", 820, new(50, 83, -66)),
        [4]  = new(4, "亚德基拉", 478, new(-64, 206.5f, 22)),
        [3]  = new(3, "红", 613, new(345, -120, -302)),
        [2]  = new(2, "梅·娜格", 635, new(162, 13, -88)),
        [1]  = new(1, "熙洛·阿里亚珀", 478, new(-72, 206.5f, 28))
    }.ToFrozenDictionary();
    
    #endregion
}
