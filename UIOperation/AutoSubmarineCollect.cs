using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using OmenTools.Info.Algorithms;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Lumina.ExtraSheets;
using OmenTools.Interop.Game.Models;
using OmenTools.Interop.Game.Models.Packets.Upstream;
using OmenTools.OmenService;
using OmenTools.Threading;
using TimeAgo;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoSubmarineCollect : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = Lang.Get("AutoSubmarineCollectTitle"),
        Description         = Lang.Get("AutoSubmarineCollectDescription"),
        Category            = ModuleCategory.UIOperation,
        ModulesPrerequisite = ["AutoCutsceneSkip"],
        ModulesRecommend    = ["OptimizedInteraction"]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private static readonly CompSig CurrentSubmarineIndexSig = new("48 8D 0D ?? ?? ?? ?? 80 A3");

    private static readonly CompSig SubmarineReturnTimeSig = new
    (
        "40 53 48 83 EC ?? 48 8B D9 E8 ?? ?? ?? ?? 84 C0 74 ?? E8 ?? ?? ?? ?? 48 8B D3 48 8D 48 ?? 48 83 C4 ?? 5B E9 ?? ?? ?? ?? 48 83 C4 ?? 5B C3 CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC 40 53 48 83 EC ?? 48 8B D9 E8 ?? ?? ?? ?? 84 C0 74 ?? E8 ?? ?? ?? ?? 48 8B D3"
    );
    private delegate nint                               SubmarineReturnTimeDelegate(SubmarineReturnTimePacket* packet);
    private          Hook<SubmarineReturnTimeDelegate>? SubmarineReturnTimeHook;

    private Config config = null!;
    
    private DalamudLinkPayload? collectSubmarinePayload;
    
    private          VerticalListNode?     itemListLayout;
    private readonly List<ItemDisplayNode> itemRenderers = [];
    private          TextButtonNode?       autoCollectNode;

    private bool isJustLogin;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        TaskHelper ??= new() { TimeoutMS = 30_000 };

        collectSubmarinePayload ??= LinkPayloadManager.Instance().Reg(OnClickCollectSubmarinePayload, out _);

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno",              OnAddonSelectYesno);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "AirShipExplorationResult", OnExplorationResult);

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "SelectString", OnAddonSelectString);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "SelectString", OnAddonSelectString);

        CommandManager.Instance().AddSubCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("AutoSubmarineCollect-CommandHelp") });

        LogMessageManager.Instance().RegPre(OnPreSendLogMessage);

        SubmarineReturnTimeHook ??= SubmarineReturnTimeSig.GetHook<SubmarineReturnTimeDelegate>(SubmarineReturnTimeDetour);
        SubmarineReturnTimeHook.Enable();

        FrameworkManager.Instance().Reg(OnUpdate, 10 * 60 * 1_000);
        GameState.Instance().Login += OnLogin;
    }

    protected override void Uninit()
    {
        LogMessageManager.Instance().Unreg(OnPreSendLogMessage);
        CommandManager.Instance().RemoveSubCommand(COMMAND);

        DService.Instance().AddonLifecycle.UnregisterListener(OnExplorationResult);
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonSelectYesno);

        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonSelectString);
        OnAddonSelectString(AddonEvent.PreFinalize, null);

        FrameworkManager.Instance().Unreg(OnUpdate);
        GameState.Instance().Login -= OnLogin;

        isJustLogin = false;

        if (collectSubmarinePayload != null)
            LinkPayloadManager.Instance().Unreg(collectSubmarinePayload.CommandId);
        collectSubmarinePayload = null;
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Command")}:");
        using (ImRaii.PushIndent())
            ImGui.TextUnformatted($"/pdr {COMMAND} → {Lang.Get("AutoSubmarineCollect-CommandHelp")}");

        ImGui.NewLine();

        if (ImGui.Checkbox(Lang.Get("AutoSubmarineCollect-NotifyWhenLogin"), ref config.NotifyWhenLogin))
            config.Save(this);

        using (ImRaii.ItemWidth(100f * GlobalUIScale))
        {
            if (ImGui.InputUInt($"{Lang.Get("AutoSubmarineCollect-NotifyCount", config.NotifyCount)}###NotifyCountInput", ref config.NotifyCount))
                config.NotifyCount = Math.Clamp(config.NotifyCount, 0, 4);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            if (ImGui.InputUInt
                (
                    $"{Lang.Get("AutoSubmarineCollect-AutoCollectCount", config.AutoCollectCount)}###AutoCollectCount",
                    ref config.AutoCollectCount
                ))
                config.NotifyCount = Math.Clamp(config.NotifyCount, 0, 4);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);
        }
    }
    
    #region Teleport

    // 传送
    private void EnqueueTeleportTasks()
    {
        TaskHelper.Abort();
        if (!FreeCompanyWorkshopInfo.TryGet(out var workshopInfo)) return;

        var housingManager = HousingManager.Instance();
        var tasks          = new List<Func<bool>>();

        // 部队工房内, 不需要传
        if (housingManager->WorkshopTerritory != null                 &&
            housingManager->WorkshopTerritory->HouseId is var houseID &&
            houseID.TerritoryTypeId == workshopInfo.TerritoryType     &&
            houseID.PlotIndex       == workshopInfo.PlotIndex         &&
            houseID.WardIndex       == workshopInfo.WardIndex)
        {
        }
        // 部队工房门外, TP 过去选门
        else if (housingManager->IndoorTerritory != null                       &&
                 housingManager->IndoorTerritory->HouseId is var indoorHouseID &&
                 indoorHouseID.TerritoryTypeId == workshopInfo.TerritoryType   &&
                 indoorHouseID.PlotIndex       == workshopInfo.PlotIndex       &&
                 indoorHouseID.WardIndex       == workshopInfo.WardIndex)
            tasks.Add(TeleportToRoomSelect);
        // 房区内, TP 过去房屋入口
        else if (housingManager->OutdoorTerritory != null                        &&
                 housingManager->OutdoorTerritory->HouseId is var outdoorHouseID &&
                 outdoorHouseID.TerritoryTypeId == workshopInfo.TerritoryType    &&
                 GameState.Map                  == workshopInfo.Map              &&
                 outdoorHouseID.WardIndex       == workshopInfo.WardIndex)
        {
            tasks.Add(TeleportToHouseEntry);
            tasks.Add(TeleportToRoomSelect);
        }
        // 其他情况一律调用一次传送
        else
        {
            tasks.Add(TeleportToHouseZone);
            tasks.Add(TeleportToHouseEntry);
            tasks.Add(TeleportToRoomSelect);
        }

        tasks.Add(TeleportToPanel);
        tasks.Add(EnqueueSubmarineCollect);

        foreach (var task in tasks)
            TaskHelper.Enqueue(task);
    }

    private static bool TeleportToHouseZone()
    {
        if (!Throttler.Shared.Throttle("AutoSubmarineCollect-TeleportToHouseZone")) return false;
        return Telepo.Instance()->Teleport(96, 0);
    }

    private static bool TeleportToHouseEntry()
    {
        if (!Throttler.Shared.Throttle("AutoSubmarineCollect-TeleportToHouseEntry", 100) ||
            !FreeCompanyWorkshopInfo.TryGet(out var workshopInfo)                        ||
            HousingManager.Instance()->OutdoorTerritory == null                          ||
            !(HousingManager.Instance()->OutdoorTerritory->HouseId is var houseID)       ||
            houseID.WardIndex != workshopInfo.WardIndex                                  ||
            GameState.Map     != workshopInfo.Map                                        ||
            DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer)
            return false;

        // 没找到入口
        if (HousingManager.Instance()->OutdoorTerritory->HouseUnit.PlotIndex != workshopInfo.PlotIndex ||
            DService.Instance().ObjectTable
                    .Where(x => x is { ObjectKind: ObjectKind.EventObj, DataID: 2002737 })
                    .OrderBy(x => Vector2.DistanceSquared(x.Position.ToVector2(), workshopInfo.Position.ToVector2())).FirstOrDefault() is not { } entryObject)
        {
            MovementManager.Instance().TPSmart_InZone(workshopInfo.Position);
            return false;
        }

        var entryPos = entryObject.Position;
        if (RaycastHelper.TryGetGroundPosition(entryPos, out var groundPosition))
            entryPos = groundPosition;

        // 在坐骑上
        if (DService.Instance().Condition.IsOnMount)
        {
            if (DService.Instance().Condition[ConditionFlag.InFlight])
            {
                ExecuteCommandManager.Instance().ExecuteCommandComplexLocation
                (
                    ExecuteCommandComplexFlag.Dismount,
                    localPlayer.Position,
                    RotationHelper.CharaToPacket(localPlayer.Rotation)
                );
            }
            else
                ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.Dismount);

            return false;
        }

        // 距离房屋入口有点远
        if (LocalPlayerState.DistanceTo3D(entryPos) > 2)
        {
            MovementManager.Instance().TPSmart_InZone(entryPos);
            return false;
        }

        if (!UIModule.IsScreenReady()) return false;

        return entryObject.TargetInteract();
    }

    private static bool TeleportToRoomSelect()
    {
        if (!Throttler.Shared.Throttle("AutoSubmarineCollect-TeleportToRoomSelect", 100) ||
            !FreeCompanyWorkshopInfo.TryGet(out var workshopInfo)                        ||
            HousingManager.Instance()->IndoorTerritory == null                           ||
            !(HousingManager.Instance()->IndoorTerritory->HouseId is var houseID)        ||
            houseID.WardIndex != workshopInfo.WardIndex                                  ||
            houseID.PlotIndex != workshopInfo.PlotIndex                                  ||
            DService.Instance().ObjectTable.LocalPlayer is null)
            return false;

        // 没找到入口
        if (!EventFramework.Instance()->IsEventIDNearby(721074))
        {
            MovementManager.Instance().TPSmart_InZone(Vector3.Zero);
            return false;
        }

        if (!UIModule.IsScreenReady()) return false;

        if (!DService.Instance().Condition.IsOccupiedInEvent)
            new EventStartPackt(LocalPlayerState.EntityID, 721074).Send();

        return AddonSelectStringEvent.Select(LuminaGetter.GetRowOrDefault<HousingPersonalRoomEntrance>(11).Text.ToString());
    }

    private static bool TeleportToPanel()
    {
        if (!Throttler.Shared.Throttle("AutoSubmarineCollect-TeleportToPanel", 100) ||
            !FreeCompanyWorkshopInfo.TryGet(out var workshopInfo)                   ||
            HousingManager.Instance()->WorkshopTerritory == null                    ||
            !(HousingManager.Instance()->WorkshopTerritory->HouseId is var houseID) ||
            houseID.WardIndex != workshopInfo.WardIndex                             ||
            houseID.PlotIndex != workshopInfo.PlotIndex                             ||
            DService.Instance().ObjectTable.LocalPlayer is null)
            return false;

        // 没找到入口
        if (!EventFramework.Instance()->TryGetNearestEvent
            (
                x => x.EventId == 3276843,
                _ => true,
                default,
                out _,
                out var gameObjectID,
                out _,
                out _
            ) ||
            DService.Instance().ObjectTable.SearchByID(gameObjectID) is not { } panelObject)
        {
            MovementManager.Instance().TPSmart_InZone(Vector3.Zero);
            return false;
        }

        // 距离面板有点远
        var realPanelPosition = LocalPlayerState.GetNearestPointToObject(panelObject);

        if (LocalPlayerState.DistanceToObject3D(panelObject, false) > 1)
        {
            MovementManager.Instance().TPSmart_InZone
            (
                RaycastHelper.TryGetNearestVerticalHit(realPanelPosition, out var result) ? result.Point : realPanelPosition
            );
            return false;
        }

        if (!UIModule.IsScreenReady()) return false;

        if (!DService.Instance().Condition.IsOccupiedInEvent)
        {
            panelObject.TargetInteract();
            return false;
        }

        return AddonSelectStringEvent.Select(LuminaGetter.GetRowOrDefault<CustomTalk>(721343).MainOption.ToString());
    }

    #endregion

    #region Events

    private void OnExplorationResult(AddonEvent type, AddonArgs args)
    {
        if (AirShipExplorationResult == null || !AirShipExplorationResult->IsAddonAndNodesReady()) return;

        AirShipExplorationResult->Callback(1);
        if (TaskHelper.IsBusy)
            AirShipExplorationResult->IsVisible = false;
    }

    private void OnAddonSelectString(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PreFinalize:
                itemListLayout?.Dispose();
                itemListLayout = null;

                autoCollectNode?.Dispose();
                autoCollectNode = null;

                itemRenderers.ForEach(x => x?.Dispose());
                itemRenderers.Clear();
                break;
            case AddonEvent.PostDraw:
                if (SelectString == null) return;

                if (itemListLayout == null && IsOnValidSubmarineList())
                {
                    var width = SelectString->RootNode->Width - 25;

                    itemListLayout = new()
                    {
                        IsVisible = true,
                        Position  = new(22, 15)
                    };
                    itemListLayout.AttachNode(SelectString->RootNode);

                    autoCollectNode = new()
                    {
                        IsVisible = true,
                        Position  = new(-10, 0),
                        Size      = new(width - 15, 30),
                        String    = Info.Title,
                        OnClick = () =>
                        {
                            TaskHelper.Abort();
                            EnqueueSubmarineCollect();
                        }
                    };

                    itemListLayout.AddNode(autoCollectNode);

                    itemListLayout.AddDummy(5);

                    foreach (var itemID in SubmarineItems)
                    {
                        var row = new ItemDisplayNode(itemID, width)
                        {
                            IsVisible   = true,
                            Size        = new(width, 38),
                            ItemTooltip = itemID
                        };
                        row.AddNodeFlags(NodeFlags.HasCollision);

                        itemListLayout.AddNode(row);
                        itemRenderers.Add(row);
                    }

                    var textNode = SelectString->GetTextNodeById(2);
                    if (textNode != null)
                        textNode->ToggleVisibility(false);

                    SelectString->RootNode->SetHeight(231   + 40);
                    SelectString->WindowNode->SetHeight(231 + 40);

                    for (var i = 0; i < SelectString->WindowNode->Component->UldManager.NodeListCount; i++)
                    {
                        var node = SelectString->WindowNode->Component->UldManager.NodeList[i];
                        if (node == null) continue;

                        if (node->Height == 231)
                            node->SetHeight(231 + 40);
                        if (node->Height == 215)
                            node->SetHeight(215 + 40);
                    }

                    var listNode = SelectString->GetComponentListById(3);
                    if (listNode != null)
                        listNode->OwnerNode->SetYFloat(92 + 40);
                }

                if (itemListLayout != null && Throttler.Shared.Throttle("AutoSubmarineCollect-UpdateCount"))
                {
                    foreach (var item in itemRenderers)
                        item.UpdateItemCount();
                }

                break;
        }
    }

    private void OnAddonSelectYesno(AddonEvent type, AddonArgs args)
    {
        if (!TaskHelper.IsBusy) return;
        AddonSelectYesnoEvent.ClickYes();
    }

    private nint SubmarineReturnTimeDetour(SubmarineReturnTimePacket* submarineReturnTimePacket)
    {
        NotifyFinishCount(submarineReturnTimePacket);
        return SubmarineReturnTimeHook.Original(submarineReturnTimePacket);
    }

    // 登陆后就发一次包吧
    private void OnLogin()
    {
        isJustLogin = true;
        SendRefreshSubmarineInfo();
    }

    private static void OnClickCollectSubmarinePayload(uint arg1, SeString arg2) =>
        ChatManager.Instance().SendMessage("/pdr submarine");

    private static void OnUpdate(IFramework _) =>
        SendRefreshSubmarineInfo();

    private void OnCommand(string command, string arguments) =>
        EnqueueTeleportTasks();

    private static void OnPreSendLogMessage(ref bool isPrevented, ref uint logMessageID, ref LogMessageQueueItem values)
    {
        if (logMessageID != 4109) return;
        isPrevented = true;
    }

    #endregion

    // 潜艇收取
    private bool EnqueueSubmarineCollect()
    {
        TaskHelper.Enqueue(() => !DService.Instance().Condition.Any(ConditionFlag.OccupiedInCutSceneEvent, ConditionFlag.WatchingCutscene78), "等待过场动画结束");
        TaskHelper.Enqueue(IsOnValidSubmarineList,                                                                                            "等待潜水艇列表界面出现");
        TaskHelper.Enqueue
        (
            () =>
            {
                if (IsLackOfSubmarineItems() || !IsAnySubmarinesAvailable(out var submarineIndex))
                {
                    TaskHelper.Abort();
                    return;
                }

                TaskHelper.Enqueue(() => AddonSelectStringEvent.Select(submarineIndex), $"收取 {submarineIndex} 号潜艇", weight: 1);
                TaskHelper.DelayNext(2_000, "延迟 2 秒, 等待远航结果确认", 1);
                TaskHelper.Enqueue(EnqueueSubmarineStateCheck, "确认潜艇信息, 准备修理和再次出航", weight: 1);
            },
            "检测是否有潜艇待收取"
        );

        return true;
    }

    private void EnqueueSubmarineStateCheck()
    {
        TaskHelper.Enqueue(() => AirShipExplorationDetail->IsAddonAndNodesReady(), "等待出航信息确认界面出现");

        TaskHelper.Enqueue
        (
            () =>
            {
                if (!Throttler.Shared.Throttle("AutoSubmarineCollect-RepairSubmarine", 100)) return false;
                if (!IsAnySubmarinePartWaitForRepair(out var parts)) return true;

                var currentSubmarineIndex = *CurrentSubmarineIndexSig.GetStatic<int>();
                parts.ForEach
                    (index => ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.RepairSubmarinePart, (uint)currentSubmarineIndex, (uint)index));
                return false;
            },
            "检测并修理潜水艇部件"
        );

        TaskHelper.Enqueue
        (
            () =>
            {
                if (!AirShipExplorationDetail->IsAddonAndNodesReady()) return false;

                AirShipExplorationDetail->Callback(0);
                AirShipExplorationDetail->Close(true);

                return true;
            },
            "再次确认出航"
        );

        TaskHelper.DelayNext(2_000, "等待出航动画结束");
        TaskHelper.Enqueue(EnqueueSubmarineCollect, "开始新一轮");
    }

    // 是否有潜艇部件需要修理
    private static bool IsAnySubmarinePartWaitForRepair(out List<int> parts)
    {
        parts = [];

        var currentSubmarine = *CurrentSubmarineIndexSig.GetStatic<int>();
        if (currentSubmarine is < 0 or > 3) return false;

        var manager = InventoryManager.Instance();
        if (manager == null) return false;

        var container = manager->GetInventoryContainer(InventoryType.HousingInteriorPlacedItems2);
        if (container == null || !container->IsLoaded) return false;

        var offset = 5 * currentSubmarine;

        for (var i = 0; i < 4; i++)
        {
            var slot = container->GetInventorySlot(i + offset);
            if (slot            == null) continue;
            if (slot->Condition > 0) continue;
            parts.Add(i);
        }

        return parts.Count > 0;
    }

    // 是否缺少潜艇相关物品
    private static bool IsLackOfSubmarineItems()
    {
        var manager    = InventoryManager.Instance();
        var itemLacked = 0U;

        // 魔导机械修理材料
        if (manager->GetInventoryItemCount(10373) < 20)
            itemLacked = 10373;
        // 桶装青磷水
        if (manager->GetInventoryItemCount(10155) < 15)
            itemLacked = 10155;

        if (itemLacked != 0)
        {
            NotifyHelper.Instance().Chat(Lang.GetSe("AutoSubmarineCollect-LackSpecificItems", SeString.CreateItemLink(itemLacked)));
            return true;
        }

        return false;
    }

    // 是否存在待收潜艇
    private static bool IsAnySubmarinesAvailable(out int index)
    {
        index = -1;

        // 不在潜艇主界面
        if (!IsOnValidSubmarineList()) return false;

        var submarines = HousingManager.Instance()->WorkshopTerritory->Submersible.Data;

        for (var i = 0; i < submarines.Length; i++)
        {
            var submarine = submarines[i];
            // 潜艇无等级 → 不存在
            if (submarine.RankId == 0) continue;

            var returnTime      = submarine.GetReturnTime();
            var leftTimeSeconds = (returnTime - StandardTimeManager.Instance().Now.ToUniversalTime()).TotalSeconds;
            if (leftTimeSeconds > 0) continue;

            index = i;
            return true;
        }

        return false;
    }

    // 是否正在潜艇列表界面
    private static bool IsOnValidSubmarineList()
    {
        if (HousingManager.Instance()->WorkshopTerritory == null) return false;
        if (!SelectString->IsAddonAndNodesReady()) return false;

        var title = SelectString->AtkValues[2].String.ToString();
        if (string.IsNullOrEmpty(title) || !VoyageListTitleText.All(title.Contains))
            return false;

        return true;
    }

    // 通知潜水艇完成情况
    private void NotifyFinishCount(SubmarineReturnTimePacket* packet)
    {
        if (packet->GetAvailableCount() == 0)
        {
            isJustLogin = false;
            return;
        }

        var maxCount      = packet->GetAvailableCount();
        var finishedCount = packet->GetFinishCount();

        if (config.NotifyWhenLogin && isJustLogin ||
            config.NotifyCount > 0 && finishedCount >= Math.Min(maxCount, config.NotifyCount))
        {
            isJustLogin = false;

            var messageBuilder = new SeStringBuilder();
            messageBuilder.AddText(Lang.Get("AutoSubmarineCollect-Notification-SubmarineInfo", maxCount - finishedCount, finishedCount));

            messageBuilder.Add(NewLinePayload.Payload)
                          .AddText($"{Lang.Get("AutoSubmarineCollect-Notification-LatestReturnTime")}: {packet->GetLatestReturnTime()}");
            if (finishedCount == maxCount)
                messageBuilder.AddText($" ({packet->GetLatestReturnTime().TimeAgo()})");

            if (finishedCount > 0)
            {
                messageBuilder.Add(NewLinePayload.Payload)
                              .Add(RawPayload.LinkTerminator)
                              .Add(collectSubmarinePayload)
                              .AddText("[")
                              .AddUiForeground(35)
                              .AddText($"{Lang.Get("AutoSubmarineCollect-Payload-TeleportAndCollect")}")
                              .AddUiForegroundOff()
                              .AddText("]")
                              .Add(RawPayload.LinkTerminator);
            }

            // TODO: 改成 ReadOnlyString
            NotifyHelper.Instance().Chat(messageBuilder.Build().Encode());
        }

        if (config.AutoCollectCount > 0 && finishedCount >= Math.Min(maxCount, config.AutoCollectCount))
            ChatManager.Instance().SendMessage("/pdr submarine");
    }

    // 发包获取情报
    private static void SendRefreshSubmarineInfo()
    {
        if (!GameState.IsLoggedIn) return;
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.RequestSubmarine, 1);
    }

    private static string SantisizeText(string text)
    {
        char[] charsToReplace = ['(', '.', ')', ']', ':', '/'];
        foreach (var c in charsToReplace)
            text = text.Replace(c, ' ');
        return text.Trim();
    }
    
    private class Config : ModuleConfig
    {
        public uint AutoCollectCount;
        public uint NotifyCount     = 4;
        public bool NotifyWhenLogin = true;
    }

    private class ItemDisplayNode : HorizontalListNode
    {
        public ItemDisplayNode(uint itemID, float width)
        {
            ItemID = itemID;

            IconNode = new()
            {
                Size       = new(32),
                IsVisible  = true,
                IconId     = LuminaWrapper.GetItemIconID(ItemID),
                FitTexture = true
            };

            AddNode(IconNode);

            AddDummy(5);

            NameNode = new()
            {
                IsVisible = true,
                Position  = new(0, 6),
                TextFlags = TextFlags.AutoAdjustNodeSize,
                FontSize  = 14,
                String    = LuminaWrapper.GetItemName(ItemID)
            };

            AddNode(NameNode);

            var itemCount   = LocalPlayerState.GetItemCount(ItemID);
            var textBuilder = new SeStringBuilder();

            if (itemCount <= 20)
            {
                textBuilder.AddIcon(BitmapFontIcon.ExclamationRectangle)
                           .AddText(" ");
            }

            textBuilder.AddText($"{itemCount}");

            CountNode = new()
            {
                IsVisible        = true,
                TextFlags        = TextFlags.AutoAdjustNodeSize | TextFlags.Edge | TextFlags.Emboss,
                FontType         = FontType.MiedingerMed,
                AlignmentType    = AlignmentType.TopRight,
                Position         = new(width - 20, 4),
                TextColor        = ColorHelper.GetColor(50),
                TextOutlineColor = ColorHelper.GetColor((uint)(itemCount > 20 ? 28 : 17)),
                FontSize         = 16,
                String           = textBuilder.Build().Encode()
            };
            CountNode.AttachNode(this);
        }

        public uint           ItemID    { get; init; }
        public IconImageNode? IconNode  { get; private set; }
        public TextNode?      NameNode  { get; private set; }
        public TextNode?      CountNode { get; private set; }

        public void UpdateItemCount()
        {
            var itemCount   = LocalPlayerState.GetItemCount(ItemID);
            var textBuilder = new SeStringBuilder();

            if (itemCount <= 20)
            {
                textBuilder.AddIcon(BitmapFontIcon.ExclamationRectangle)
                           .AddText(" ");
            }

            textBuilder.AddText($"{itemCount}");

            CountNode.String           = textBuilder.Build().Encode();
            CountNode.TextOutlineColor = ColorHelper.GetColor((uint)(itemCount > 20 ? 28 : 17));
        }

        protected override void Dispose(bool disposing, bool isNativeDestructor)
        {
            IconNode?.Dispose();
            IconNode = null;

            NameNode?.Dispose();
            NameNode = null;

            CountNode?.Dispose();
            CountNode = null;

            base.Dispose(disposing, isNativeDestructor);
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct SubmarineReturnTimePacket
    {
        [FieldOffset(0)]
        public int ReturnTime1;

        [FieldOffset(36)]
        public int ReturnTime2;

        [FieldOffset(72)]
        public int ReturnTime3;

        [FieldOffset(108)]
        public int ReturnTime4;

        public int GetFinishCount() =>
            new List<int> { ReturnTime1, ReturnTime2, ReturnTime3, ReturnTime4 }.Count(x => x != 0 && x <= DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        public int GetAvailableCount() =>
            new List<int> { ReturnTime1, ReturnTime2, ReturnTime3, ReturnTime4 }.Count(x => x != 0);

        public DateTime GetLatestReturnTime() =>
            new List<int> { ReturnTime1, ReturnTime2, ReturnTime3, ReturnTime4 }
                .Where(x => x != 0)
                .Max()
                .ToUTCDateTimeFromUnixSeconds()
                .ToLocalTime();
    }

    private record FreeCompanyWorkshopInfo
    {
        public FreeCompanyWorkshopInfo(uint territoryType, uint wardIndex, uint plotIndex)
        {
            TerritoryType = territoryType;
            WardIndex     = wardIndex;
            PlotIndex     = plotIndex;

            var result = LuminaGetter
                         .GetSub<HousingMapMarkerInfo>()
                         .SelectMany(x => x)
                         .Where(x => x.Map.IsValid)
                         .Where(x => x.Map.Value.TerritoryType.RowId == TerritoryType)
                         .FirstOrDefault(x => x.SubrowId             == PlotIndex);

            Position = new(result.X, result.Y, result.Z);
            Map      = result.Map.RowId;
        }

        public Vector3 Position      { get; init; }
        public uint    TerritoryType { get; init; }
        public uint    Map           { get; init; }
        public uint    WardIndex     { get; init; }
        public uint    PlotIndex     { get; init; }

        public static bool TryGet([NotNullWhen(true)] out FreeCompanyWorkshopInfo? workshopInfo)
        {
            workshopInfo = null;

            var fcHouseID = HousingManager.GetOwnedHouseId(EstateType.FreeCompanyEstate);
            if (fcHouseID.Id == 0 || fcHouseID.WorldId != GameState.CurrentWorld) return false;

            workshopInfo = new(fcHouseID.TerritoryTypeId, fcHouseID.WardIndex, fcHouseID.PlotIndex);
            return true;
        }
    }
    
    #region 常量

    private const string COMMAND = "submarine";

    // 桶装青磷水和魔导机械修理材料
    private static readonly uint[] SubmarineItems = [10155, 10373];

    private static List<string> VoyageListTitleText { get; } =
    [
        ..LuminaGetter.GetRowOrDefault<HouFixCompanySubmarine>(2).Text.ToDalamudString().Payloads
                      .Where(x => x.Type == PayloadType.RawText)
                      .Select(text => SantisizeText((text as TextPayload).Text))
                      .Where(x => !string.IsNullOrWhiteSpace(x))
                      .ToList()
    ];

    #endregion
}
