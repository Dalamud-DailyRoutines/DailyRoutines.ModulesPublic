using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using TimeAgo;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoSubmarineCollect : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = GetLoc("AutoSubmarineCollectTitle"),
        Description         = GetLoc("AutoSubmarineCollectDescription"),
        Category            = ModuleCategories.UIOperation,
        ModulesPrerequisite = ["AutoCutsceneSkip"],
        ModulesRecommend    = ["OptimizedInteraction"]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };
    
    private const string Command = "submarine";

    // 桶装青磷水和魔导机械修理材料
    private static readonly uint[] SubmarineItems = [10155, 10373];

    private static Lazy<List<string>> VoyageListTitleText =>
        new(() => LuminaGetter.GetRowOrDefault<HouFixCompanySubmarine>(2).Text.ToDalamudString().Payloads
                              .Where(x => x.Type == PayloadType.RawText)
                              .Select(text => SantisizeText((text as TextPayload).Text))
                              .Where(x => !string.IsNullOrWhiteSpace(x))
                              .ToList());
    
    private static readonly CompSig CurrentSubmarineIndexSig = new("48 8D 0D ?? ?? ?? ?? 80 A3");
    
    private static readonly CompSig                            SubmarineReturnTimeSig = new("40 53 48 83 EC ?? 48 8B D9 E8 ?? ?? ?? ?? 84 C0 74 ?? E8 ?? ?? ?? ?? 48 8B D3 48 8D 48 ?? 48 83 C4 ?? 5B E9 ?? ?? ?? ?? 48 83 C4 ?? 5B C3 CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC 40 53 48 83 EC ?? 48 8B D9 E8 ?? ?? ?? ?? 84 C0 74 ?? E8 ?? ?? ?? ?? 48 8B D3");
    private delegate        nint                               SubmarineReturnTimeDelegate(SubmarineReturnTimePacket* packet);
    private static          Hook<SubmarineReturnTimeDelegate>? SubmarineReturnTimeHook;
    
    private static DalamudLinkPayload? CollectSubmarinePayload;
    
    private static Config ModuleConfig = null!;

    private static          VerticalListNode?     ItemListLayout;
    private static readonly List<ItemDisplayNode> ItemRenderers = [];
    private static          TextButtonNode?       AutoCollectNode;

    private static bool IsJustLogin;
    
    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        TaskHelper ??= new() { TimeLimitMS = 30_000 };
        
        CollectSubmarinePayload ??= LinkPayloadManager.Register(OnClickCollectSubmarinePayload, out _);

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno",              OnAddonSelectYesno);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "AirShipExplorationResult", OnExplorationResult);
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "SelectString", OnAddonSelectString);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "SelectString", OnAddonSelectString);

        CommandManager.AddSubCommand(Command, new(OnCommand) { HelpMessage = GetLoc("AutoSubmarineCollect-CommandHelp") });
        
        LogMessageManager.Register(OnPreSendLogMessage);

        SubmarineReturnTimeHook ??= SubmarineReturnTimeSig.GetHook<SubmarineReturnTimeDelegate>(SubmarineReturnTimeDetour);
        SubmarineReturnTimeHook.Enable();

        FrameworkManager.Reg(OnUpdate, throttleMS: 5 * 60 * 1_000);
        GameState.Login += OnLogin;
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("Command")}:");
        using (ImRaii.PushIndent())
            ImGui.TextUnformatted($"/pdr {Command} → {GetLoc("AutoSubmarineCollect-CommandHelp")}");
        
        ImGui.NewLine();
        
        if (ImGui.Checkbox(GetLoc("AutoSubmarineCollect-NotifyWhenLogin"), ref ModuleConfig.NotifyWhenLogin))
            SaveConfig(ModuleConfig);

        using (ImRaii.ItemWidth(100f * GlobalFontScale))
        {
            if (ImGui.InputUInt($"{GetLoc("AutoSubmarineCollect-NotifyCount", ModuleConfig.NotifyCount)}###NotifyCountInput", ref ModuleConfig.NotifyCount))
                ModuleConfig.NotifyCount = Math.Clamp(ModuleConfig.NotifyCount, 0, 4);
            if (ImGui.IsItemDeactivatedAfterEdit())
                SaveConfig(ModuleConfig);
        
            if (ImGui.InputUInt($"{GetLoc("AutoSubmarineCollect-AutoCollectCount", ModuleConfig.AutoCollectCount)}###AutoCollectCount", 
                                ref ModuleConfig.AutoCollectCount))
                ModuleConfig.NotifyCount = Math.Clamp(ModuleConfig.NotifyCount, 0, 4);
            if (ImGui.IsItemDeactivatedAfterEdit())
                SaveConfig(ModuleConfig);
        }
    }
    
    // 潜艇收取
    private bool? EnqueueSubmarineCollect()
    {
        TaskHelper.Enqueue(() => !DService.Condition.Any(ConditionFlag.OccupiedInCutSceneEvent, ConditionFlag.WatchingCutscene78), "等待过场动画结束");
        TaskHelper.Enqueue(() => IsOnValidSubmarineList(), "等待潜水艇列表界面出现");
        TaskHelper.Enqueue(() =>
        {
            if (IsLackOfSubmarineItems() || !IsAnySubmarinesAvailable(out var submarineIndex))
            {
                TaskHelper.Abort();
                return;
            }

            TaskHelper.Enqueue(() => ClickSelectString(submarineIndex), $"收取 {submarineIndex} 号潜艇", null, null, 1);
            TaskHelper.DelayNext(2_000, "延迟 2 秒, 等待远航结果确认", false, 1);
            TaskHelper.Enqueue(EnqueueSubmarineStateCheck, "确认潜艇信息, 准备修理和再次出航", null, null, 1);
        }, "检测是否有潜艇待收取");

        return true;
    }

    private void EnqueueSubmarineStateCheck()
    {
        TaskHelper.Enqueue(() => IsAddonAndNodesReady(AirShipExplorationDetail), "等待出航信息确认界面出现");
        
        TaskHelper.Enqueue(() =>
        {
            if (!Throttler.Throttle("AutoSubmarineCollect-RepairSubmarine", 100)) return false;
            if (!IsAnySubmarinePartWaitForRepair(out var parts)) return true;

            var currentSubmarineIndex = *CurrentSubmarineIndexSig.GetStatic<int>();
            parts.ForEach(index => ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.RepairSubmarinePart, (uint)currentSubmarineIndex, (uint)index));
            return false;
        }, "检测并修理潜水艇部件");
        
        TaskHelper.Enqueue(() =>
        {
            if (!IsAddonAndNodesReady(AirShipExplorationDetail)) return false;

            Callback(AirShipExplorationDetail, true, 0);
            AirShipExplorationDetail->Close(true);

            return true;
        }, "再次确认出航");
        
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
            if (slot == null) continue; 
            if (slot->Condition > 0) continue;
            parts.Add(i);
        }
        
        return parts.Count > 0;
    }
    
    // 是否缺少潜艇相关物品
    private static bool IsLackOfSubmarineItems()
    {
        var manager = InventoryManager.Instance();
        var itemLacked = 0U;

        // 魔导机械修理材料
        if (manager->GetInventoryItemCount(10373) < 20) 
            itemLacked = 10373;
        // 桶装青磷水
        if (manager->GetInventoryItemCount(10155) < 15) 
            itemLacked = 10155;

        if (itemLacked != 0)
        {
            Chat(GetSLoc("AutoSubmarineCollect-LackSpecificItems", SeString.CreateItemLink(itemLacked)));
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
            var leftTimeSeconds = (returnTime - DateTime.Now.ToUniversalTime()).TotalSeconds;
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
        if (!IsAddonAndNodesReady(SelectString)) return false;

        var title = SelectString->AtkValues[2].String.ExtractText();
        if (string.IsNullOrEmpty(title) || !VoyageListTitleText.Value.All(title.Contains)) 
            return false;
        
        return true;
    }

    // 通知潜水艇完成情况
    private static void NotifyFinishCount(SubmarineReturnTimePacket* packet)
    {
        if (packet->GetAvailableCount() == 0)
        {
            IsJustLogin = false;
            return;
        }

        var maxCount      = packet->GetAvailableCount();
        var finishedCount = packet->GetFinishCount();
        if ((ModuleConfig.NotifyWhenLogin && IsJustLogin) ||
            (ModuleConfig.NotifyCount > 0 && finishedCount >= Math.Min(maxCount, ModuleConfig.NotifyCount)))
        {
            IsJustLogin = false;

            var messageBuilder = new SeStringBuilder();
            messageBuilder.AddText(GetLoc("AutoSubmarineCollect-Notification-SubmarineInfo", maxCount - finishedCount, finishedCount));

            messageBuilder.Add(NewLinePayload.Payload)
                          .AddText($"{GetLoc("AutoSubmarineCollect-Notification-LatestReturnTime")}: {packet->GetLatestReturnTime()}");
            if (finishedCount == maxCount)
                messageBuilder.AddText($" ({packet->GetLatestReturnTime().TimeAgo()})");

            if (finishedCount > 0)
                messageBuilder.Add(NewLinePayload.Payload)
                              .Add(RawPayload.LinkTerminator)
                              .Add(CollectSubmarinePayload)
                              .AddText("[")
                              .AddUiForeground(35)
                              .AddText($"{GetLoc("AutoSubmarineCollect-Payload-TeleportAndCollect")}")
                              .AddUiForegroundOff()
                              .AddText("]")
                              .Add(RawPayload.LinkTerminator);
            
            Chat(messageBuilder.Build());
        }
        
        if (ModuleConfig.AutoCollectCount > 0 && finishedCount >= Math.Min(maxCount, ModuleConfig.AutoCollectCount))
            ChatManager.SendMessage("/pdr submarine");
    }

    // 发包获取情报
    private static void SendRefreshSubmarineInfo()
    {
        if (!GameState.IsLoggedIn                || 
            OccupiedInEvent                      || 
            GameState.ContentFinderCondition > 0 ||
            GameState.HomeWorld != GameState.CurrentWorld) 
            return;

        ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.RefreshSubmarineInfo, 1);
    }
    
    #region Utilities

    private static bool TargetSystem_IsObjectInViewRange(nint targetSystem, nint targetGameObject)
    {
        if (targetGameObject == nint.Zero) return false;

        var objectCount = *(int*)(targetSystem + 328);
        if (objectCount <= 0) return false;

        var i = (nint*)(targetSystem + 336);
        for (var index = 0; index < objectCount; index++, i++)
        {
            if (*i == targetGameObject)
                return true;
        }

        return false;
    }

    private static string SantisizeText(string text)
    {
        char[] charsToReplace = ['(', '.', ')', ']', ':', '/'];
        foreach (var c in charsToReplace)
            text = text.Replace(c, ' ');
        return text.Trim();
    }

    #endregion

    #region Teleport

    // 传送
    private void EnqueueTeleportTasks()
    {
        TaskHelper.Abort();
        if (!FreeCompanyWorkshopInfo.TryGet(out var workshopInfo)) return;
        
        var housingManager = HousingManager.Instance();
        var tasks          = new List<Func<bool?>>();

        // 部队工房内, 不需要传
        if (housingManager->WorkshopTerritory != null                 &&
            housingManager->WorkshopTerritory->HouseId is var houseID &&
            houseID.TerritoryTypeId == workshopInfo.TerritoryType     &&
            houseID.PlotIndex       == workshopInfo.PlotIndex         &&
            houseID.WardIndex       == workshopInfo.WardIndex)
        { }
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

    private static bool? TeleportToHouseZone()
    {
        if (!Throttler.Throttle("AutoSubmarineCollect-TeleportToHouseZone")) return false;
        return Telepo.Instance()->Teleport(96, 0);
    }

    private static bool? TeleportToHouseEntry()
    {
        if (!Throttler.Throttle("AutoSubmarineCollect-TeleportToHouseEntry", 100)  ||
            !FreeCompanyWorkshopInfo.TryGet(out var workshopInfo)                  ||
            HousingManager.Instance()->OutdoorTerritory == null                    ||
            !(HousingManager.Instance()->OutdoorTerritory->HouseId is var houseID) ||
            houseID.WardIndex != workshopInfo.WardIndex                            ||
            GameState.Map     != workshopInfo.Map                                  ||
            DService.ObjectTable.LocalPlayer is not { } localPlayer)
            return false;

        // 没找到入口
        if (HousingManager.Instance()->OutdoorTerritory->HouseUnit.PlotIndex != workshopInfo.PlotIndex ||
            DService.ObjectTable
                    .Where(x => x is { ObjectKind: ObjectKind.EventObj, DataID: 2002737 })
                    .OrderBy(x => Vector2.DistanceSquared(x.Position.ToVector2(), workshopInfo.Position.ToVector2())).FirstOrDefault() is not { } entryObject)
        {
            MovementManager.TPSmart_InZone(workshopInfo.Position);
            return false;
        }

        // 在坐骑上
        if (IsOnMount)
        {
            if (DService.Condition[ConditionFlag.InFlight])
                ExecuteCommandManager.ExecuteCommandComplexLocation(
                    ExecuteCommandComplexFlag.Dismount,
                    localPlayer.Position,
                    CharaRotationToPacketRotation(localPlayer.Rotation));
            else
                ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.Dismount);
            
            return false;
        }
        
        // 距离房屋入口有点远
        if (LocalPlayerState.DistanceTo3D(entryObject.Position) > 2)
        {
            MovementManager.TPSmart_InZone(entryObject.Position);
            return false;
        }

        if (!IsScreenReady()) return false;
        
        return entryObject.TargetInteract();
    }

    private static bool? TeleportToRoomSelect()
    {
        if (!Throttler.Throttle("AutoSubmarineCollect-TeleportToRoomSelect", 100) ||
            !FreeCompanyWorkshopInfo.TryGet(out var workshopInfo)                 ||
            HousingManager.Instance()->IndoorTerritory == null                    ||
            !(HousingManager.Instance()->IndoorTerritory->HouseId is var houseID) ||
            houseID.WardIndex != workshopInfo.WardIndex                           ||
            houseID.PlotIndex != workshopInfo.PlotIndex                           ||
            DService.ObjectTable.LocalPlayer is null)
            return false;

        // 没找到入口
        if (!IsEventIDNearby(721074))
        {
            MovementManager.TPSmart_InZone(Vector3.Zero);
            return false;
        }

        if (!IsScreenReady()) return false;

        if (!OccupiedInEvent)
            new EventStartPackt(LocalPlayerState.EntityID, 721074).Send();
        
        return ClickSelectString(LuminaGetter.GetRowOrDefault<HousingPersonalRoomEntrance>(11).Text.ExtractText());
    }

    private static bool? TeleportToPanel()
    {
        if (!Throttler.Throttle("AutoSubmarineCollect-TeleportToPanel", 100)        ||
            !FreeCompanyWorkshopInfo.TryGet(out var workshopInfo)                   ||
            HousingManager.Instance()->WorkshopTerritory == null                    ||
            !(HousingManager.Instance()->WorkshopTerritory->HouseId is var houseID) ||
            houseID.WardIndex != workshopInfo.WardIndex                             ||
            houseID.PlotIndex != workshopInfo.PlotIndex                             ||
            DService.ObjectTable.LocalPlayer is null)
            return false;

        // 没找到入口
        if (!TryGetNearestEvent(x => x.EventId == 3276843,
                                                     _ => true,
                                                     default,
                                                     out _,
                                                     out var gameObjectID,
                                                     out _,
                                                     out _) ||
            DService.ObjectTable.SearchByID(gameObjectID) is not { } panelObject)
        {
            MovementManager.TPSmart_InZone(Vector3.Zero);
            return false;
        }
        
        // 距离面板有点远
        var realPanelPosition = LocalPlayerState.GetNearestPointToObject(panelObject);
        if (LocalPlayerState.DistanceToObject3D(panelObject, false) > 1)
        {
            MovementManager.TPSmart_InZone(MovementManager.TryDetectGround(realPanelPosition, out var result) ?? false ? result : realPanelPosition);
            return false;
        }

        if (!IsScreenReady()) return false;

        if (!OccupiedInEvent)
        {
            panelObject.TargetInteract();
            return false;
        }
        
        return ClickSelectString(LuminaGetter.GetRowOrDefault<CustomTalk>(721343).MainOption.ExtractText());
    }

    #endregion
    
    #region Events

    private void OnExplorationResult(AddonEvent type, AddonArgs args)
    {
        if (AirShipExplorationResult == null || !IsAddonAndNodesReady(AirShipExplorationResult)) return;

        Callback(AirShipExplorationResult, true, 1);
        if (TaskHelper.IsBusy) 
            AirShipExplorationResult->IsVisible = false;
    }

    private void OnAddonSelectString(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PreFinalize:
                ItemListLayout?.DetachNode();
                ItemListLayout = null;
                
                AutoCollectNode?.DetachNode();
                AutoCollectNode = null;
                
                ItemRenderers.ForEach(x => x?.DetachNode());
                ItemRenderers.Clear();
                break;
            case AddonEvent.PostDraw:
                if (SelectString == null) return;
                
                if (ItemListLayout == null && IsOnValidSubmarineList())
                {
                    var width = SelectString->RootNode->Width - 25;
                    
                    ItemListLayout = new()
                    {
                        IsVisible = true,
                        Position  = new(22, 15)
                    };
                    ItemListLayout.AttachNode(SelectString->RootNode);

                    AutoCollectNode = new()
                    {
                        IsVisible = true,
                        Position  = new(-10, 0),
                        Size      = new(width, 30),
                        SeString  = Info.Title,
                        OnClick   = () =>
                        {
                            TaskHelper.Abort();
                            EnqueueSubmarineCollect();
                        }
                    };

                    ItemListLayout.AddNode(AutoCollectNode);

                    ItemListLayout.AddDummy(5);

                    foreach (var itemID in SubmarineItems)
                    {
                        var row = new ItemDisplayNode(itemID, width)
                        {
                            IsVisible = true,
                            Size      = new(width, 38)
                        };
                        ItemListLayout.AddNode(row);
                        ItemRenderers.Add(row);
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
                    }

                    var listNode = SelectString->GetComponentListById(3);
                    if (listNode != null)
                        listNode->OwnerNode->SetYFloat(92 + 40);
                }

                if (ItemListLayout != null && Throttler.Throttle("AutoSubmarineCollect-UpdateCount"))
                {
                    foreach (var item in ItemRenderers)
                        item.UpdateItemCount();
                }

                break;
        }
    }

    private void OnAddonSelectYesno(AddonEvent type, AddonArgs args)
    {
        if (!TaskHelper.IsBusy) return;
        ClickSelectYesnoYes();
    }

    private static nint SubmarineReturnTimeDetour(SubmarineReturnTimePacket* submarineReturnTimePacket)
    {
        NotifyFinishCount(submarineReturnTimePacket);
        return SubmarineReturnTimeHook.Original(submarineReturnTimePacket);
    }

    // 登陆后就发一次包吧
    private static void OnLogin()
    {
        IsJustLogin = true;
        SendRefreshSubmarineInfo();
    }
    
    private static void OnClickCollectSubmarinePayload(uint arg1, SeString arg2) => 
        ChatManager.SendMessage("/pdr submarine");
    
    private static void OnUpdate(IFramework _) => 
        SendRefreshSubmarineInfo();

    private void OnCommand(string command, string arguments) => 
        EnqueueTeleportTasks();

    private static void OnPreSendLogMessage(ref bool isPrevented, ref uint logMessageID)
    {
        if (logMessageID != 4109) return;
        isPrevented = true;
    }

    #endregion

    protected override void Uninit()
    {
        LogMessageManager.Unregister(OnPreSendLogMessage);
        CommandManager.RemoveSubCommand(Command);

        DService.AddonLifecycle.UnregisterListener(OnExplorationResult);
        DService.AddonLifecycle.UnregisterListener(OnAddonSelectYesno);
        
        DService.AddonLifecycle.UnregisterListener(OnAddonSelectString);
        OnAddonSelectString(AddonEvent.PreFinalize, null);

        FrameworkManager.Unreg(OnUpdate);
        GameState.Login -= OnLogin;

        IsJustLogin = false;
        
        if (CollectSubmarinePayload != null)
            LinkPayloadManager.Unregister(CollectSubmarinePayload.CommandId);
        CollectSubmarinePayload = null;
    }

    private class Config : ModuleConfiguration
    {
        public bool NotifyWhenLogin = true;
        public uint NotifyCount     = 4;
        public uint AutoCollectCount;
    }

    private class ItemDisplayNode : HorizontalListNode
    {
        public uint           ItemID    { get; init; }
        public IconImageNode? IconNode  { get; private set; }
        public TextNode?      NameNode  { get; private set; }
        public TextNode?      CountNode { get; private set; }
        
        public ItemDisplayNode(uint itemID, float width)
        {
            ItemID = itemID;

            IconNode = new()
            {
                Size      = new(32),
                IsVisible = true,
                IconId    = LuminaWrapper.GetItemIconID(ItemID),
            };
            AddNode(IconNode);
            
            AddDummy(5);

            NameNode = new()
            {
                IsVisible        = true,
                Position         = new(0, 6),
                TextFlags        = TextFlags.AutoAdjustNodeSize | TextFlags.Edge,
                FontSize         = 14,
                TextOutlineColor = ColorHelper.GetColor(37),
                SeString         = LuminaWrapper.GetItemName(ItemID),
            };
            AddNode(NameNode);
            
            var itemCount   = LocalPlayerState.GetItemCount(ItemID);
            var textBuilder = new SeStringBuilder();
            if (itemCount <= 20)
                textBuilder.AddIcon(BitmapFontIcon.ExclamationRectangle)
                           .AddText(" ");
            textBuilder.AddText($"{itemCount}");
            
            CountNode = new()
            {
                IsVisible        = true,
                TextFlags        = TextFlags.AutoAdjustNodeSize | TextFlags.Edge | TextFlags.Emboss,
                FontType         = FontType.MiedingerMed,
                AlignmentType    = AlignmentType.TopRight,
                Position         = new(width - 20, 4),
                TextOutlineColor = ColorHelper.GetColor((uint)(itemCount > 20 ? 28 : 17)),
                FontSize         = 16,
                SeString         = textBuilder.Build().Encode()
            };
            CountNode.AttachNode(this);
        }

        public void UpdateItemCount()
        {
            var itemCount   = LocalPlayerState.GetItemCount(ItemID);
            var textBuilder = new SeStringBuilder();
            if (itemCount <= 20)
                textBuilder.AddIcon(BitmapFontIcon.ExclamationRectangle)
                           .AddText(" ");
            textBuilder.AddText($"{itemCount}");
            
            CountNode.SeString         = textBuilder.Build().Encode();
            CountNode.TextOutlineColor = ColorHelper.GetColor((uint)(itemCount > 20 ? 28 : 17));
        }

        protected override void Dispose(bool disposing, bool isNativeDestructor)
        {
            IconNode?.DetachNode();
            IconNode = null;
            
            NameNode?.DetachNode();
            NameNode = null;
            
            CountNode?.DetachNode();
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
            UnixSecondToDateTime(new List<int> { ReturnTime1, ReturnTime2, ReturnTime3, ReturnTime4 }.Where(x => x != 0).Max());
    }

    private record FreeCompanyWorkshopInfo
    {
        public Vector3 Position      { get; init; }
        public uint    TerritoryType { get; init; }
        public uint    Map           { get; init; }
        public uint    WardIndex     { get; init; }
        public uint    PlotIndex     { get; init; }
        
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

        public static bool TryGet([NotNullWhen(true)] out FreeCompanyWorkshopInfo? workshopInfo)
        {
            workshopInfo = null;
            
            var fcHouseID = HousingManager.GetOwnedHouseId(EstateType.FreeCompanyEstate);
            if (fcHouseID.Id == 0 || fcHouseID.WorldId != GameState.CurrentWorld) return false;

            workshopInfo = new(fcHouseID.TerritoryTypeId, fcHouseID.WardIndex, fcHouseID.PlotIndex);
            return true;
        }
    }
}
