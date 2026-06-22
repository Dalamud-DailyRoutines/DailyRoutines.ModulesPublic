using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.Agent;
using Dalamud.Game.Agent.AgentArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using AgentId = Dalamud.Game.Agent.AgentId;
using ContextMenu = KamiToolKit.ContextMenu.ContextMenu;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe class FastCustomDeliveriesInfo : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("FastCustomDeliveriesInfoTitle"),
        Description = Lang.Get("FastCustomDeliveriesInfoDescription"),
        Category    = ModuleCategory.Interface
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private ContextMenu? contextMenu;

    protected override void Init() =>
        DService.Instance().AgentLifecycle.RegisterListener(AgentEvent.PreReceiveEvent, AgentId.SatisfactionList, OnAgent);

    protected override void Uninit()
    {
        DService.Instance().AgentLifecycle.UnregisterListener(OnAgent);

        contextMenu?.Dispose();
        contextMenu = null;
    }

    private void ShowContextMenu(CustomDeliveryInfo? selectedInfo)
    {
        if (selectedInfo == null) return;

        contextMenu?.Dispose();
        contextMenu = new();

        contextMenu.AddItem
        (
            new()
            {
                Name    = Lang.Get("Teleport"),
                OnClick = () => MovementManager.Instance().TPSmart_BetweenZone(LuminaWrapper.GetZoneFromMap(selectedInfo.Map), selectedInfo.Position)
            }
        );

        contextMenu.AddItem
        (
            new()
            {
                Name = Lang.Get("FastCustomDeliveriesInfo-TeleportToZone"),
                OnClick = () =>
                {
                    switch (selectedInfo.Index)
                    {
                        case 6 or 7:
                            MovementManager.Instance().TPSmart_BetweenZone(LuminaWrapper.GetZoneFromMap(selectedInfo.Map));
                            break;
                        default:
                            AetheryteRecordManager.Instance().GetNearestAetheryte
                            (
                                LuminaWrapper.GetZoneFromMap(selectedInfo.Map),
                                selectedInfo.Position
                            )?.TeleportTo();
                            break;
                    }
                }
            }
        );

        contextMenu.AddItem
        (
            new()
            {
                Name = LuminaWrapper.GetAddonText(8887),
                OnClick = () => DService.Instance().Framework.RunOnTick
                (
                    () => AgentMap.Instance()->SetMapFlagAndOpen
                    (
                        selectedInfo.Map,
                        selectedInfo.Position,
                        selectedInfo.Name
                    ),
                    delayTicks: 1
                )
            }
        );

        contextMenu.Open();
    }

    private void OnAgent(AgentEvent type, AgentArgs args)
    {
        var formatted = args as AgentReceiveEventArgs;
        if (formatted.ValueCount < 2) return;

        var atkValues = (AtkValue*)formatted.AtkValues;
        if (atkValues == null) return;

        // 非右键
        var valueType = atkValues[0].Int;
        if (valueType != 1) return;

        var customDeliveryIndex = atkValues[1].UInt;
        if (customDeliveryIndex < 1) return;

        if (!Infos.TryGetValue(customDeliveryIndex, out var customDeliveryInfo))
            return;

        ShowContextMenu(customDeliveryInfo);
        formatted.PreventOriginal();
    }

    private record CustomDeliveryInfo
    (
        uint    Index,
        string  Name,
        uint    Map,
        Vector3 Position
    );

    #region 常量

    private static readonly FrozenDictionary<uint, CustomDeliveryInfo> Infos = new Dictionary<uint, CustomDeliveryInfo>
    {
        [12] = new(12, "缇索加", 855, new(90, -14, 59.7f)),
        [11] = new(11, "尼托维凯", 860, new(-355.7f, 19.6f, -108.7f)),
        [10] = new(10, "玛格拉特", 695, new(-52.8f, -29.5f, -61.5f)),
        [9]  = new(9, "安登", 494, new(-241f, 51f, 615.7f)),
        [8]  = new(8, "阿梅莉安丝", 693, new(223, 25, -193)),
        [7]  = new(7, "狄兰达尔伯爵", 574, new(-112, 0, -135)),
        [6]  = new(6, "艾尔·图", 574, new(110, -20, 0)),
        [5]  = new(5, "凯·希尔", 555, new(50, 83, -66)),
        [4]  = new(4, "亚德基拉", 257, new(-64, 206.5f, 22)),
        [3]  = new(3, "红", 371, new(345, -120, -302)),
        [2]  = new(2, "梅·娜格", 366, new(162, 13, -88)),
        [1]  = new(1, "熙洛·阿里亚珀", 257, new(-72, 206.5f, 28))
    }.ToFrozenDictionary();

    #endregion
}
