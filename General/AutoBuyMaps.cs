using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Newtonsoft.Json;
using OmenTools.Helpers;
using DailyRoutines.Modules;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public readonly struct MapInfo
{
    public uint OriginalItemID { get; }
    public string ShortName { get; }
    public uint DecipheredKeyItemID { get; }

    public MapInfo(uint originalItemID, string shortName, uint decipheredKeyItemID)
    {
        OriginalItemID = originalItemID;
        ShortName = shortName;
        DecipheredKeyItemID = decipheredKeyItemID;
    }
    
    public string DisplayName
    {
        get
        {
            if (OriginalItemID == 0) 
                return "None";
            string itemName = $"Unknown Item {OriginalItemID}";
            if (LuminaGetter.TryGetRow<Item>(OriginalItemID, out var item))
                itemName = item.Name.ExtractText();
            return $"{ShortName} - {itemName}";
        }
    }
}

public unsafe class AutoBuyMaps : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoBuyMaps-Title"),
        Description = GetLoc("AutoBuyMaps-Description"),
        Category = ModuleCategories.General,
        Author = ["qingsiweisan"]
    };

    private enum PurchaseState
    {
        Idle,                    // 空闲状态
        Initializing,            // 初始化，分析库存
        WaitingForDecipher,      // 等待解读完成
        WaitingForSaddlebagMove, // 等待移动到鞍囊
        SearchingMarket,         // 搜索市场
        PurchasingItem,          // 购买中
        VerifyingPurchase,       // 验证购买结果
        PostPurchaseProcessing,  // 购买后处理
        Completed,               // 完成
        Failed                   // 失败
    }

    private class PurchaseContext
    {
        private readonly object lockObject = new object();
        private PurchaseState currentState = PurchaseState.Idle;
        
        public PurchaseState CurrentState
        {
            get 
            { 
                lock (lockObject) 
                    return currentState; 
            }
            set 
            { 
                lock (lockObject) 
                    currentState = value; 
            }
        }
        
        public uint TargetMapID { get; set; }
        public int CurrentMapNumber { get; set; } = 1;
        public int TotalNeeded { get; set; }
        public int PurchasedCount { get; set; }
        public DateTime LastStateChange { get; set; }
        public string? LastError { get; set; }
        
        // 缓存的库存信息
        public int InventoryCount { get; set; }
        public int SaddlebagCount { get; set; }
        public int DecipheredCount { get; set; }
        
        // 标记
        public bool NeedsDecipher { get; set; }
        public bool NeedsSaddlebagMove { get; set; }
        
        // 搜索相关
        public int SearchStartTime { get; set; }
        public int PurchaseVerifyStartTime { get; set; }
        public int CountBeforePurchase { get; set; }
        
        // 库存更新时间戳，避免重复更新
        public DateTime LastInventoryUpdate { get; set; }
        
        public void Reset()
        {
            lock (lockObject)
                currentState = PurchaseState.Idle;
            CurrentMapNumber = 1;
            PurchasedCount = 0;
            LastError = null;
            InventoryCount = 0;
            SaddlebagCount = 0;
            DecipheredCount = 0;
            NeedsDecipher = false;
            NeedsSaddlebagMove = false;
            LastInventoryUpdate = DateTime.MinValue;
        }
        
        public bool ShouldUpdateInventory()
        {
            return (DateTime.Now - LastInventoryUpdate).TotalMilliseconds > 500;
        }
    }

    private static readonly List<MapInfo> AvailableMaps = new()
    {
        new MapInfo(0, "", 0),
        new MapInfo(6688, "G1", 2001087),
        new MapInfo(6689, "G2", 2001088),
        new MapInfo(6690, "G3", 2001089),
        new MapInfo(6691, "G4", 2001090),
        new MapInfo(6692, "G5", 2001091),
        new MapInfo(12241, "G6", 2001762),
        new MapInfo(12242, "G7", 2001763),
        new MapInfo(12243, "G8", 2001764),
        new MapInfo(17835, "G9", 2002209),
        new MapInfo(17836, "G10", 2002210),
        new MapInfo(26744, "G11", 2002663),
        new MapInfo(26745, "G12", 2002664),
        new MapInfo(36611, "G13", 2003245),
        new MapInfo(36612, "G14", 2003246),
        new MapInfo(39591, "G15", 2003457),
        new MapInfo(43556, "G16", 2003562),
        new MapInfo(43557, "G17", 2003563),
        new MapInfo(46185, "G18", 2003785),
    };

    private static readonly Dictionary<string, uint> GradeMappings = GenerateGradeMappings();
    
    private static Dictionary<string, uint> GenerateGradeMappings()
    {
        var mappings = new Dictionary<string, uint>();
        
        for (int i = 1; i < AvailableMaps.Count; i++)
        {
            var map = AvailableMaps[i];
            var grade = $"G{i}";
            
            mappings[grade] = map.OriginalItemID;
            mappings[grade.ToLower()] = map.OriginalItemID;
            mappings[i.ToString()] = map.OriginalItemID;
        }
        
        return mappings;
    }

    private bool isBusy = false;
    private PurchaseContext purchaseContext = new();
    private uint? originalMapID = null;
    private int? originalCount = null;
    private int? originalMaxPrice = null;
    
    // 兼容旧代码的变量
    private int needToBuy = 0;
    private int purchasedCount = 0;
    private int purchaseStartTime = 0;

    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new Config();

        TaskHelper ??= new() { TimeLimitMS = 30_000 };

        CommandManager.AddSubCommand("buymaps", new Dalamud.Game.Command.CommandInfo(OnPdrCommand)
        {
            HelpMessage = GetLoc("AutoBuyMaps-CommandHelp"),
        });
    }

    protected override void Uninit()
    {
        CommandManager.RemoveSubCommand("buymaps");
        SaveConfig(ModuleConfig);
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(ImGuiColors.DalamudViolet.ToUint(), $"{GetLoc("Command")}:");
        ImGui.SameLine();
        ImGui.Text($"/pdr buymaps → {GetLoc("AutoBuyMaps-CommandHelp")}");
        
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        if (ImGui.BeginCombo("##Map", GetMapDisplayName(ModuleConfig.TargetMapID)))
        {
            foreach (var map in AvailableMaps)
            {
                if (ImGui.Selectable(map.DisplayName, ModuleConfig.TargetMapID == map.OriginalItemID))
                {
                    ModuleConfig.TargetMapID = map.OriginalItemID;
                    SaveConfig(ModuleConfig);
                }
            }
            ImGui.EndCombo();
        }
        
        ImGui.SameLine();
        ImGui.SetNextItemWidth(50f * GlobalFontScale);
        var targetCount = ModuleConfig.TargetCount;
        if (ImGui.InputInt("##Count", ref targetCount))
        {
            ModuleConfig.TargetCount = Math.Clamp(targetCount, 1, 10);
            SaveConfig(ModuleConfig);
        }
        
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80f * GlobalFontScale);
        var maxPrice = ModuleConfig.MaxPrice;
        if (ImGui.InputInt("##Price", ref maxPrice))
        {
            ModuleConfig.MaxPrice = Math.Max(0, maxPrice);
            SaveConfig(ModuleConfig);
        }
        
        ImGui.SameLine();
        using (ImRaii.Disabled(isBusy || ModuleConfig.TargetMapID == 0))
        {
            if (ImGui.Button(GetLoc("Start")))
                StartSmartPurchase();
        }
        
        ImGui.SameLine();
        using (ImRaii.Disabled(!isBusy))
        {
            if (ImGui.Button(GetLoc("Stop")))
                StopPurchase();
        }
    }

    private class MapStatusAnalysis
    {
        public int InventoryCount { get; set; }
        public int SaddlebagCount { get; set; }
        public int DecipheredCount { get; set; }
        public bool NeedsDecipher { get; set; }
        public int NeedsSaddlebag { get; set; }
        public int NeedsInventory { get; set; }
        public int TotalToBuy { get; set; }
    }

    private void StartSmartPurchase()
    {
        if (isBusy)
        {
            NotificationError(GetLoc("AutoBuyMaps-AlreadyBusy"));
            return;
        }

        var targetMapID = ModuleConfig.TargetMapID;
        if (targetMapID == 0) 
            return;

        // 初始化上下文
        purchaseContext.Reset();
        purchaseContext.TargetMapID = targetMapID;
        
        isBusy = true;
        
        // 开始状态机
        TransitionTo(PurchaseState.Initializing);
    }
    
    private void TransitionTo(PurchaseState newState)
    {
        var oldState = purchaseContext.CurrentState;
        purchaseContext.CurrentState = newState;
        purchaseContext.LastStateChange = DateTime.Now;
        
        // 避免递归调用，使用延迟执行
        TaskHelper.DelayNext(50);
        TaskHelper.Enqueue(() => 
        {
            ProcessStateMachine();
            return true;
        });
    }
    
    private void ProcessStateMachine()
    {
        if (!isBusy) 
            return;
        
        // 防止状态机运行过久
        var timeSinceLastChange = (DateTime.Now - purchaseContext.LastStateChange).TotalSeconds;
        if (timeSinceLastChange > 30 && purchaseContext.CurrentState != PurchaseState.Idle)
        {
            purchaseContext.LastError = $"State machine timeout in {purchaseContext.CurrentState}";
            TransitionTo(PurchaseState.Failed);
            return;
        }
        
        switch (purchaseContext.CurrentState)
        {
            case PurchaseState.Idle:
                break;
                
            case PurchaseState.Initializing:
                HandleInitializing();
                break;
                
            case PurchaseState.WaitingForDecipher:
                HandleDecipherWait();
                break;
                
            case PurchaseState.WaitingForSaddlebagMove:
                HandleSaddlebagMoveWait();
                break;
                
            case PurchaseState.SearchingMarket:
                HandleMarketSearch();
                break;
                
            case PurchaseState.PurchasingItem:
                HandlePurchasing();
                break;
                
            case PurchaseState.VerifyingPurchase:
                HandlePurchaseVerification();
                break;
                
            case PurchaseState.PostPurchaseProcessing:
                HandlePostPurchase();
                break;
                
            case PurchaseState.Completed:
                HandleCompletion();
                break;
                
            case PurchaseState.Failed:
                HandleFailure();
                break;
                
            default:
                purchaseContext.LastError = $"Unknown state: {purchaseContext.CurrentState}";
                TransitionTo(PurchaseState.Failed);
                break;
        }
    }
    
    private void HandleInitializing()
    {
        UpdateInventoryCounts();
        
        var analysis = AnalyzeMapStatus(purchaseContext.TargetMapID);
        purchaseContext.NeedsDecipher = analysis.NeedsDecipher;
        
        // 计算实际需要购买的总数（考虑游戏限制）
        var currentTotal = purchaseContext.DecipheredCount + purchaseContext.InventoryCount + purchaseContext.SaddlebagCount;
        var targetCount = ModuleConfig.TargetCount;
        purchaseContext.TotalNeeded = targetCount - currentTotal;
        
        // 记录初始状态日志
        DService.Log.Info($"AutoBuyMaps: Starting purchase - Target: {targetCount}, Current: {currentTotal} (Inv: {purchaseContext.InventoryCount}, Saddle: {purchaseContext.SaddlebagCount}, Deciphered: {purchaseContext.DecipheredCount})");
        
        bool needsMoveToSaddlebag = ModuleConfig.MoveToSaddlebag &&
                                   analysis.SaddlebagCount < 1 &&
                                   analysis.InventoryCount > 0 &&
                                   analysis.DecipheredCount > 0;
        
        purchaseContext.NeedsSaddlebagMove = needsMoveToSaddlebag;

        // 判断是否已达到目标（初步检查）
        if (currentTotal >= targetCount)
        {
            // 直接进入完成状态，让 HandleCompletion 做最终验证
            TransitionTo(PurchaseState.Completed);
            return;
        }

        // 决定下一个状态
        if (analysis.NeedsDecipher)
        {
            StartDecipherProcess();
            TransitionTo(PurchaseState.WaitingForDecipher);
        }
        else if (needsMoveToSaddlebag)
        {
            StartSaddlebagMove();
            TransitionTo(PurchaseState.WaitingForSaddlebagMove);
        }
        else if (analysis.InventoryCount == 0 && currentTotal < targetCount)
            // 只有背包没有地图时才能购买
            TransitionTo(PurchaseState.SearchingMarket);
        else
            TransitionTo(PurchaseState.Completed);
    }
    
    private void HandleDecipherWait()
    {
        // 这个状态由 EnqueueDecipherMap 的回调处理
        // 回调会调用 OnDecipherComplete
    }
    
    private void OnDecipherComplete()
    {
        if (!isBusy) 
            return;
        
        UpdateInventoryCounts();
        
        DService.Log.Info($"AutoBuyMaps: Decipher complete. Deciphered count: {purchaseContext.DecipheredCount}");
        
        var currentTotal = purchaseContext.DecipheredCount + purchaseContext.InventoryCount + purchaseContext.SaddlebagCount;
        if (currentTotal >= ModuleConfig.TargetCount)
            TransitionTo(PurchaseState.Completed);
        else if (ShouldMoveToSaddlebag())
        {
            StartSaddlebagMove();
            TransitionTo(PurchaseState.WaitingForSaddlebagMove);
        }
        else if (purchaseContext.InventoryCount == 0)  // 只有背包空时才能继续购买
        {
            purchaseContext.CurrentMapNumber++;
            TransitionTo(PurchaseState.SearchingMarket);
        }
        else
            TransitionTo(PurchaseState.Completed);
    }
    
    private void HandleSaddlebagMoveWait()
    {
        // 这个状态由 EnqueueMoveToSaddlebag 的回调处理
        // 回调会调用 OnSaddlebagMoveComplete
    }
    
    private void OnSaddlebagMoveComplete()
    {
        if (!isBusy) 
            return;
        
        UpdateInventoryCounts();
        
        DService.Log.Info($"AutoBuyMaps: Saddlebag move complete. Saddlebag count: {purchaseContext.SaddlebagCount}");
        
        var currentTotal = purchaseContext.DecipheredCount + purchaseContext.InventoryCount + purchaseContext.SaddlebagCount;
        if (currentTotal >= ModuleConfig.TargetCount)
            TransitionTo(PurchaseState.Completed);
        else if (purchaseContext.InventoryCount == 0)  // 背包空了，可以继续购买
        {
            purchaseContext.CurrentMapNumber++;
            TransitionTo(PurchaseState.SearchingMarket);
        }
        else
        {
            // 背包还有地图，需要继续处理
            if (ShouldDecipher())
            {
                StartDecipherProcess();
                TransitionTo(PurchaseState.WaitingForDecipher);
            }
            else
                TransitionTo(PurchaseState.Completed);
        }
    }
    
    private void HandleMarketSearch()
    {
        var targetMapID = purchaseContext.TargetMapID;
        
        // 检查是否还需要购买
        UpdateInventoryCounts();
        var currentTotal = purchaseContext.DecipheredCount + purchaseContext.InventoryCount + purchaseContext.SaddlebagCount;
        if (currentTotal >= ModuleConfig.TargetCount)
        {
            TransitionTo(PurchaseState.Completed);
            return;
        }
        
        // 重要：背包已有地图时不能购买（游戏限制）
        if (purchaseContext.InventoryCount > 0)
        {
            DService.Log.Debug($"AutoBuyMaps: Inventory has map, need to process first");
            
            // 如果背包有地图，需要先处理（解读或移动）
            if (ShouldDecipher())
            {
                StartDecipherProcess();
                TransitionTo(PurchaseState.WaitingForDecipher);
                return;
            }
            else if (ShouldMoveToSaddlebag())
            {
                StartSaddlebagMove();
                TransitionTo(PurchaseState.WaitingForSaddlebagMove);
                return;
            }
            else
            {
                // 背包有地图但不需要解读或移动，说明已达到目标
                TransitionTo(PurchaseState.Completed);
                return;
            }
        }
        
        // 检查背包空间
        var freeSlots = GetFreeInventorySlots();
        if (freeSlots < 1)
        {
            purchaseContext.LastError = "No inventory space available";
            NotificationError(GetLoc("AutoBuyMaps-NoInventorySpace"));
            TransitionTo(PurchaseState.Failed);
            return;
        }
        
        DService.Log.Debug($"AutoBuyMaps: Starting search for map #{purchaseContext.CurrentMapNumber}");
        
        // 延迟搜索（第2张及以后）
        if (purchaseContext.CurrentMapNumber > 1)
        {
            var waitTime = purchaseContext.CurrentMapNumber == 3 ? 5000 : 2000;
            TaskHelper.DelayNext(waitTime);
        }
        
        TaskHelper.Enqueue(() => 
        {
            StartMarketSearchInternal(targetMapID);
            return true;
        });
    }
    
    private void StartMarketSearchInternal(uint targetMapID)
    {
        try
        {
            var infoProxy = InfoProxyItemSearch.Instance();
            if (infoProxy == null)
            {
                purchaseContext.LastError = "InfoProxy not available";
                TransitionTo(PurchaseState.Failed);
                return;
            }

            infoProxy->EndRequest();
            infoProxy->SearchItemId = targetMapID;
            var requestResult = infoProxy->RequestData();
            
            if (!requestResult)
            {
                TaskHelper.DelayNext(1000);
                TaskHelper.Enqueue(() => 
                {
                    StartMarketSearchInternal(targetMapID);
                    return true;
                });
                return;
            }

            purchaseContext.SearchStartTime = Environment.TickCount;
            EnqueueMarketSearchPolling(TaskHelper, targetMapID, purchaseContext.SearchStartTime, ModuleConfig.PurchaseTimeoutMs, purchaseContext.CurrentMapNumber);
        }
        catch (Exception ex)
        {
            purchaseContext.LastError = $"Market search error: {ex.Message}";
            TransitionTo(PurchaseState.Failed);
        }
    }
    
    private void HandlePurchasing()
    {
        // 由 ProcessMarketSearchResult 处理
    }
    
    private void HandlePurchaseVerification()
    {
        // 由 EnqueuePurchaseVerificationPolling 处理
    }
    
    private void HandlePostPurchase()
    {
        UpdateInventoryCounts();
        
        DService.Log.Info($"AutoBuyMaps: Purchase #{purchaseContext.CurrentMapNumber} complete. " +
                         $"Current status - Inv: {purchaseContext.InventoryCount}, Saddle: {purchaseContext.SaddlebagCount}, Deciphered: {purchaseContext.DecipheredCount}");
        
        // 决定下一个状态
        if (ShouldDecipher())
        {
            DService.Log.Debug("AutoBuyMaps: Need to decipher first");
            StartDecipherProcess();
            TransitionTo(PurchaseState.WaitingForDecipher);
        }
        else if (ShouldMoveToSaddlebag())
        {
            DService.Log.Debug("AutoBuyMaps: Need to move to saddlebag");
            StartSaddlebagMove();
            TransitionTo(PurchaseState.WaitingForSaddlebagMove);
        }
        else if (NeedsMorePurchases())
        {
            purchaseContext.CurrentMapNumber++;
            DService.Log.Debug($"AutoBuyMaps: Continuing to purchase map #{purchaseContext.CurrentMapNumber}");
            TransitionTo(PurchaseState.SearchingMarket);
        }
        else
            TransitionTo(PurchaseState.Completed);
    }
    
    private void HandleCompletion()
    {
        // 最终验证：强制刷新库存并确认是否真正达到目标数量
        purchaseContext.LastInventoryUpdate = DateTime.MinValue; // 强制更新
        UpdateInventoryCounts();

        var actualTotal = purchaseContext.InventoryCount + purchaseContext.SaddlebagCount + purchaseContext.DecipheredCount;
        var targetCount = ModuleConfig.TargetCount;

        DService.Log.Info($"AutoBuyMaps: Final verification - Target: {targetCount}, Actual: {actualTotal} " +
                         $"(Inv: {purchaseContext.InventoryCount}, Saddle: {purchaseContext.SaddlebagCount}, Deciphered: {purchaseContext.DecipheredCount})");

        if (actualTotal >= targetCount)
        {
            // 真正达到目标，购买成功
            NotificationSuccess(string.Format(GetLoc("AutoBuyMaps-PurchaseComplete"),
                actualTotal, purchaseContext.InventoryCount, purchaseContext.SaddlebagCount, purchaseContext.DecipheredCount));
            StopPurchase();
        }
        else
        {
            // 数量不足，需要继续购买或失败
            var shortage = targetCount - actualTotal;

            DService.Log.Warning($"AutoBuyMaps: Completion check failed - shortage: {shortage}");

            // 检查是否还能继续购买
            if (purchaseContext.CurrentMapNumber > 10) // 防止无限循环
            {
                purchaseContext.LastError = $"Purchase incomplete after {purchaseContext.CurrentMapNumber} attempts. Target: {targetCount}, Actual: {actualTotal}";
                TransitionTo(PurchaseState.Failed);
                return;
            }

            // 检查背包状态，决定下一步
            if (purchaseContext.InventoryCount == 0)
            {
                // 背包空，可以继续购买
                DService.Log.Info($"AutoBuyMaps: Continuing purchase due to shortage - need {shortage} more");
                purchaseContext.CurrentMapNumber++;
                TransitionTo(PurchaseState.SearchingMarket);
            }
            else
            {
                // 背包有图，需要先处理
                if (ShouldDecipher())
                {
                    DService.Log.Info("AutoBuyMaps: Need to decipher before continuing");
                    StartDecipherProcess();
                    TransitionTo(PurchaseState.WaitingForDecipher);
                }
                else if (ShouldMoveToSaddlebag())
                {
                    DService.Log.Info("AutoBuyMaps: Need to move to saddlebag before continuing");
                    StartSaddlebagMove();
                    TransitionTo(PurchaseState.WaitingForSaddlebagMove);
                }
                else
                {
                    // 无法继续处理，可能是配置问题
                    purchaseContext.LastError = $"Cannot continue purchase - inventory blocked. Target: {targetCount}, Actual: {actualTotal}";
                    TransitionTo(PurchaseState.Failed);
                }
            }
        }
    }
    
    private void HandleFailure()
    {
        if (!string.IsNullOrEmpty(purchaseContext.LastError))
            NotificationError($"Purchase failed: {purchaseContext.LastError}");
        StopPurchase();
    }
    
    private void UpdateInventoryCounts()
    {
        // 避免频繁更新
        if (!purchaseContext.ShouldUpdateInventory())
            return;
            
        var targetMapID = purchaseContext.TargetMapID;
        purchaseContext.InventoryCount = CountItemInInventory(targetMapID);
        purchaseContext.SaddlebagCount = CountItemInSaddlebag(targetMapID);
        
        var mapInfo = AvailableMaps.FirstOrDefault(m => m.OriginalItemID == targetMapID);
        if (mapInfo.DecipheredKeyItemID != 0)
            purchaseContext.DecipheredCount = GetItemCount(mapInfo.DecipheredKeyItemID, InventoryType.KeyItems);
        else
            purchaseContext.DecipheredCount = 0;
            
        purchaseContext.LastInventoryUpdate = DateTime.Now;
    }
    
    private bool ShouldDecipher()
    {
        return ModuleConfig.AutoDecipherFirst &&
               purchaseContext.DecipheredCount == 0 &&
               purchaseContext.InventoryCount > 0;
    }
    
    private bool ShouldMoveToSaddlebag()
    {
        return ModuleConfig.MoveToSaddlebag &&
               purchaseContext.SaddlebagCount < 1 &&
               purchaseContext.InventoryCount > 0 &&
               purchaseContext.DecipheredCount > 0;
    }
    
    private bool NeedsMorePurchases()
    {
        UpdateInventoryCounts();
        var total = purchaseContext.InventoryCount +
                    purchaseContext.SaddlebagCount +
                    purchaseContext.DecipheredCount;
        
        var needMore = total < ModuleConfig.TargetCount &&
                      purchaseContext.InventoryCount == 0;  // 只有背包空时才能继续购买
                      
        DService.Log.Debug($"AutoBuyMaps: NeedsMorePurchases - Total: {total}, Target: {ModuleConfig.TargetCount}, InventoryEmpty: {purchaseContext.InventoryCount == 0}, Result: {needMore}");
        
        return needMore;
    }
    
    private void StartDecipherProcess()
    {
        TaskHelper.Enqueue(() => 
        {
            EnqueueDecipherMap(TaskHelper, purchaseContext.TargetMapID, () => 
            {
                OnDecipherComplete();
            });
            return true;
        });
    }
    
    private void StartSaddlebagMove()
    {
        TaskHelper.Enqueue(() => 
        {
            EnqueueMoveToSaddlebag(TaskHelper, purchaseContext.TargetMapID, () => 
            {
                OnSaddlebagMoveComplete();
            });
            return true;
        });
    }

    private MapStatusAnalysis AnalyzeMapStatus(uint targetMapID)
    {
        var analysis = new MapStatusAnalysis();

        analysis.InventoryCount = CountItemInInventory(targetMapID);
        analysis.SaddlebagCount = CountItemInSaddlebag(targetMapID);

        var mapInfo = AvailableMaps.FirstOrDefault(m => m.OriginalItemID == targetMapID);
        if (mapInfo.DecipheredKeyItemID != 0)
            analysis.DecipheredCount = GetItemCount(mapInfo.DecipheredKeyItemID, InventoryType.KeyItems);
        else
            analysis.DecipheredCount = 0;

        analysis.NeedsDecipher = ModuleConfig.AutoDecipherFirst &&
                                analysis.DecipheredCount == 0 &&
                                (analysis.InventoryCount > 0 || analysis.SaddlebagCount > 0);

        var currentTotal = analysis.DecipheredCount + analysis.InventoryCount + analysis.SaddlebagCount;

        if (currentTotal >= ModuleConfig.TargetCount)
        {
            analysis.NeedsInventory = 0;
            analysis.NeedsSaddlebag = 0;
            analysis.TotalToBuy = 0;
        }
        else
        {
            // 重要限制：每个位置（背包、鞍囊、钥匙栏）同时只能有1张
            // 计算还需要多少张
            var remaining = ModuleConfig.TargetCount - currentTotal;
            
            // 确定需要购买的数量
            // 只有当背包没有地图时才能购买1张到背包
            if (analysis.InventoryCount == 0)
            {
                analysis.NeedsInventory = 1;
                analysis.TotalToBuy = Math.Min(1, remaining); // 一次只能购买1张到背包
            }
            else
            {
                analysis.NeedsInventory = 0;
                analysis.TotalToBuy = 0; // 背包已有，不能再购买
            }
            
            analysis.NeedsSaddlebag = 0;
        }

        return analysis;
    }


    private void EnqueueMarketSearchPolling(TaskHelper taskHelper, uint targetMapID, int startTime, int maxWaitTime, int currentMapNumber)
    {
        taskHelper.DelayNext(200);

        taskHelper.Enqueue(() =>
        {
            if (!isBusy) return false;

            var elapsedTime = Environment.TickCount - startTime;

            if (elapsedTime > maxWaitTime)
            {
                StopPurchase();
                return false;
            }

            var infoProxy = InfoProxyItemSearch.Instance();
            if (infoProxy == null)
            {
                StopPurchase();
                return false;
            }

            if (infoProxy->SearchItemId == targetMapID && infoProxy->ListingCount > 0)
            {
                taskHelper.Enqueue(() =>
                {
                    ProcessMarketSearchResult(infoProxy, targetMapID, currentMapNumber);
                    return true;
                });
                return true;
            }
            else if (infoProxy->SearchItemId == targetMapID)
            {
                EnqueueMarketSearchPolling(taskHelper, targetMapID, startTime, maxWaitTime, currentMapNumber);
                return true;
            }
            else
            {
                taskHelper.Enqueue(() =>
                {
                    StartMarketSearchInternal(targetMapID);
                    return true;
                });
                return true;
            }
        });
    }

    private void ProcessMarketSearchResult(InfoProxyItemSearch* infoProxy, uint targetMapID, int currentMapNumber)
    {
        try
        {
            var allListings = infoProxy->Listings.ToArray();

            var validListings = allListings.Where(x => x.ItemId == targetMapID && x.UnitPrice > 0).ToArray();

            var cheapestListing = validListings
                                        .Where(x => ModuleConfig.MaxPrice == 0 || x.UnitPrice <= (ulong)ModuleConfig.MaxPrice)
                                        .OrderBy(x => x.UnitPrice)
                                        .FirstOrDefault();

            if (cheapestListing.ItemId == 0)
            {
                StopPurchase();
                return;
            }

            var countBeforePurchase = CountItemInInventory(targetMapID);

            var listingToBuy = cheapestListing;
            listingToBuy.Quantity = 1;

            TaskHelper.Enqueue(new System.Action(() => 
            {
                var purchaseListing = listingToBuy;
                try
                {
                    var purchaseResult = PurchaseItemAction(infoProxy, &purchaseListing);
                    if (!purchaseResult)
                    {
                        purchaseContext.LastError = "Failed to send purchase request";
                        TransitionTo(PurchaseState.Failed);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    purchaseContext.LastError = $"Purchase exception: {ex.Message}";
                    TransitionTo(PurchaseState.Failed);
                    return;
                }
            }));

            var purchaseStartTime = Environment.TickCount;
            var maxPurchaseWaitTime = 5000;

            EnqueuePurchaseVerificationPolling(TaskHelper, targetMapID, countBeforePurchase, currentMapNumber, purchaseStartTime, maxPurchaseWaitTime);
        }
        catch (Exception ex)
        {
            purchaseContext.LastError = $"Market processing error: {ex.Message}";
            TransitionTo(PurchaseState.Failed);
        }
    }

    private void EnqueuePurchaseVerificationPolling(TaskHelper taskHelper, uint targetMapID, int countBeforePurchase, int currentMapNumber, int startTime, int maxWaitTime)
    {
        taskHelper.DelayNext(100);

        taskHelper.Enqueue(() => 
        {
            if (!isBusy) 
                return false;

            var elapsedTime = Environment.TickCount - startTime;

            if (elapsedTime > maxWaitTime)
            {
                StopPurchase();
                return false;
            }

            var countAfterPurchase = CountItemInInventory(targetMapID);

            if (countAfterPurchase > countBeforePurchase)
            {
                purchaseContext.PurchasedCount = currentMapNumber;

                TaskHelper.Enqueue(() =>
                {
                    // 切换到购买后处理状态
                    TransitionTo(PurchaseState.PostPurchaseProcessing);
                    return true;
                });
                return true;
            }
            else
            {
                EnqueuePurchaseVerificationPolling(taskHelper, targetMapID, countBeforePurchase, currentMapNumber, startTime, maxWaitTime);
                return true;
            }
        });
    }

    private void StopPurchase()
    {
        if (!isBusy) 
            return;
        isBusy = false;
        needToBuy = 0;
        purchasedCount = 0;
        purchaseStartTime = 0;

        if (originalMapID.HasValue)
        {
            ModuleConfig.TargetMapID = originalMapID.Value;
            ModuleConfig.TargetCount = originalCount ?? ModuleConfig.TargetCount;
            ModuleConfig.MaxPrice = originalMaxPrice ?? ModuleConfig.MaxPrice;
            SaveConfig(ModuleConfig);

            originalMapID = null;
            originalCount = null;
            originalMaxPrice = null;
        }

        TaskHelper?.Abort();
    }

    private static bool PurchaseItemAction(InfoProxyItemSearch* infoProxy, MarketBoardListing* listing)
    {
        infoProxy->SetLastPurchasedItem(listing);
        return infoProxy->SendPurchaseRequestPacket();
    }

    private void EnqueueDecipherMap(TaskHelper taskHelper, uint mapItemID, System.Action? onComplete = null)
    {
        var mapInfo = AvailableMaps.FirstOrDefault(m => m.OriginalItemID == mapItemID);
        if (mapInfo.OriginalItemID == 0)
        {
            onComplete?.Invoke();
            return;
        }

        if (GetItemCount(mapInfo.DecipheredKeyItemID, InventoryType.KeyItems) > 0)
        {
            onComplete?.Invoke();
            return;
        }

        taskHelper.Enqueue(() =>
        {
            var (invType, slot) = FindItemInInventory(mapItemID);

            if (invType == InventoryType.Invalid)
            {
                var (saddlebagType, saddlebagSlot) = FindItemInSaddlebag(mapItemID);
                if (saddlebagType != InventoryType.Invalid)
                {
                    var moveResult = MoveItemToInventory(saddlebagType, saddlebagSlot);

                    if (!moveResult)
                    {
                        onComplete?.Invoke();
                        return true;
                    }

                    (invType, slot) = FindItemInInventory(mapItemID);
                }
            }

            if (invType == InventoryType.Invalid)
            {
                onComplete?.Invoke();
                return true;
            }

            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null)
            {
                onComplete?.Invoke();
                return true;
            }

            var itemSlot = inventoryManager->GetInventorySlot(invType, slot);
            if (itemSlot == null)
            {
                onComplete?.Invoke();
                return true;
            }

            HelpersOm.OpenInventoryItemContext(*itemSlot);
            return true;
        });

        taskHelper.DelayNext(300);

        taskHelper.Enqueue(() =>
        {
            if (!HelpersOm.IsAddonAndNodesReady(InfosOm.ContextMenu))
                return false;

            var result = HelpersOm.ClickContextMenu(LuminaWrapper.GetAddonText(8100));

            if (!result)
            {
                InfosOm.ContextMenu->Close(true);
                return false;
            }

            return true;
        });

        taskHelper.DelayNext(300);

        taskHelper.Enqueue(() =>
        {
            var result = HelpersOm.ClickSelectYesnoYes();
            if (result)
            {
                taskHelper.DelayNext(3000);
                taskHelper.Enqueue(() =>
                {
                    onComplete?.Invoke();
                    return true;
                });
            }
            return result;
        });
    }

    private void EnqueueMoveToSaddlebag(TaskHelper taskHelper, uint mapItemID, System.Action? onComplete = null)
    {
        taskHelper.Enqueue(() =>
        {
            var (invType, slot) = FindItemInInventory(mapItemID);
            if (invType == InventoryType.Invalid)
            {
                onComplete?.Invoke();
                return true;
            }

            var hasSpace = FindEmptySlot(InventoryType.SaddleBag1) != -1 ||
                          FindEmptySlot(InventoryType.SaddleBag2) != -1;
            if (!hasSpace)
            {
                onComplete?.Invoke();
                return true;
            }

            var result = OpenSaddlebag();
            if (!result)
            {
                onComplete?.Invoke();
                return true;
            }

            return true;
        });

        taskHelper.DelayNext(1000);

        taskHelper.Enqueue(() =>
        {
            var (invType, slot) = FindItemInInventory(mapItemID);
            if (invType == InventoryType.Invalid)
            {
                CloseSaddlebag();
                onComplete?.Invoke();
                return true;
            }

            var inventoryCountBefore = CountItemInInventory(mapItemID);
            var saddlebagCountBefore = CountItemInSaddlebag(mapItemID);

            var moveResult = MoveItem(invType, slot, InventoryType.SaddleBag1);
            if (!moveResult)
                moveResult = MoveItem(invType, slot, InventoryType.SaddleBag2);

            if (!moveResult)
            {
                CloseSaddlebag();
                onComplete?.Invoke();
                return true;
            }

            var inventoryCountAfter = CountItemInInventory(mapItemID);
            var saddlebagCountAfter = CountItemInSaddlebag(mapItemID);

            if (inventoryCountAfter < inventoryCountBefore && saddlebagCountAfter > saddlebagCountBefore)
            {

                taskHelper.DelayNext(1000);
                taskHelper.Enqueue(() =>
                {
                    CloseSaddlebag();
                    onComplete?.Invoke();
                    return true;
                });
            }
            else
            {
                CloseSaddlebag();

                taskHelper.DelayNext(1000);
                taskHelper.Enqueue(() =>
                {
                    onComplete?.Invoke();
                    return true;
                });
            }

            return true;
        });
    }

    private void OnPdrCommand(string command, string args)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(args))
                return;

            var parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return;

            string gradeInput = parts[0].Trim();
            if (!GradeMappings.TryGetValue(gradeInput, out uint mapItemID))
                return;

            if (!int.TryParse(parts[1], out int quantity) || quantity <= 0 || quantity > 10)
                return;

            int maxPrice = 0;
            if (parts.Length >= 3)
            {
                if (!int.TryParse(parts[2], out maxPrice) || maxPrice < 0)
                    return;
            }

            if (isBusy)
            {
                NotificationError(GetLoc("AutoBuyMaps-CommandBusy"));
                DService.Log.Warning($"AutoBuyMaps: Command rejected - already busy");
                return;
            }

            originalMapID = ModuleConfig.TargetMapID;
            originalCount = ModuleConfig.TargetCount;
            originalMaxPrice = ModuleConfig.MaxPrice;

            ModuleConfig.TargetMapID = mapItemID;
            ModuleConfig.TargetCount = quantity;
            ModuleConfig.MaxPrice = maxPrice;

            StartSmartPurchase();
        }
        catch (Exception ex)
        {
            // 命令解析错误，记录日志但不中断
            DService.Log.Warning($"AutoBuyMaps: Command parse error - {ex.Message}");
        }
    }


    private string GetMapDisplayName(uint mapID)
    {
        if (mapID == 0) return GetLoc("AutoBuyMaps-SelectMap");
        var map = AvailableMaps.FirstOrDefault(m => m.OriginalItemID == mapID);
        if (map.OriginalItemID != 0)
            return map.DisplayName;

        if (LuminaGetter.TryGetRow<Item>(mapID, out var item))
            return item.Name.ExtractText();
        return GetLoc("AutoBuyMaps-UnknownMap");
    }

    private unsafe int CountItemInContainers(uint itemID, params InventoryType[] containerTypes)
    {
        var manager = InventoryManager.Instance();
        if (manager == null) return 0;

        int count = 0;
        foreach (var type in containerTypes)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded) continue;

            for (int i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot != null && slot->ItemId == itemID)
                    count += slot->Quantity;
            }
        }
        return count;
    }
    
    private int CountItemInInventory(uint itemID) => CountItemInContainers(itemID, 
        InventoryType.Inventory1, InventoryType.Inventory2, 
        InventoryType.Inventory3, InventoryType.Inventory4);
    
    private int CountItemInSaddlebag(uint itemID) => CountItemInContainers(itemID,
        InventoryType.SaddleBag1, InventoryType.SaddleBag2);
    
    private unsafe int GetFreeInventorySlots()
    {
        var manager = InventoryManager.Instance();
        if (manager == null) return 0;

        int freeSlots = 0;
        InventoryType[] types = { InventoryType.Inventory1, InventoryType.Inventory2, 
                                   InventoryType.Inventory3, InventoryType.Inventory4 };

        foreach (var type in types)
        {
            var container = manager->GetInventoryContainer(type);
            if (container != null && container->IsLoaded)
            {
                for (int i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || slot->ItemId == 0)
                        freeSlots++;
                }
            }
        }
        return freeSlots;
    }

    private static unsafe int GetItemCount(uint itemID, params InventoryType[] containers)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null) return 0;

        int count = 0;
        foreach (var containerType in containers)
            count += inventoryManager->GetItemCountInContainer(itemID, containerType);

        return count;
    }

    private static unsafe (InventoryType type, int slot) FindItemInInventory(uint itemID)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null) return (InventoryType.Invalid, -1);

        InventoryType[] inventoryTypes =
        {
            InventoryType.Inventory1,
            InventoryType.Inventory2,
            InventoryType.Inventory3,
            InventoryType.Inventory4
        };

        foreach (var invType in inventoryTypes)
        {
            var container = inventoryManager->GetInventoryContainer(invType);
            if (container == null) continue;

            for (int i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot != null && slot->ItemId == itemID)
                    return (invType, i);
            }
        }

        return (InventoryType.Invalid, -1);
    }

    private static unsafe (InventoryType type, int slot) FindItemInSaddlebag(uint itemID)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null) return (InventoryType.Invalid, -1);

        InventoryType[] saddlebagTypes =
        {
            InventoryType.SaddleBag1,
            InventoryType.SaddleBag2
        };

        foreach (var invType in saddlebagTypes)
        {
            var container = inventoryManager->GetInventoryContainer(invType);
            if (container == null) continue;

            for (int i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot != null && slot->ItemId == itemID)
                    return (invType, i);
            }
        }

        return (InventoryType.Invalid, -1);
    }


    private static unsafe bool OpenSaddlebag()
    {
        try
        {
            DService.Command.ProcessCommand("/saddlebag");
            return true;
        }
        catch
        {
            try
            {
                var uiModule = UIModule.Instance();
                if (uiModule != null)
                {
                    uiModule->OpenInventory(1);
                    return true;
                }
            }
            catch (Exception ex)
            {
                DService.Log.Warning($"AutoBuyMaps: OpenSaddlebag fallback error - {ex.Message}");
            }
            return false;
        }
    }

    private static unsafe bool MoveItem(InventoryType fromType, int fromSlot, InventoryType toType)
    {
        try
        {
            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null) return false;

            var targetSlot = FindEmptySlot(toType);
            if (targetSlot == -1) return false;

            var result = inventoryManager->MoveItemSlot(fromType, (ushort)fromSlot, toType, (ushort)targetSlot, true);
            return result > 0;
        }
        catch (Exception ex)
        {
            DService.Log.Warning($"AutoBuyMaps: MoveItem error - {ex.Message}");
            return false;
        }
    }

    private static unsafe bool MoveItemToInventory(InventoryType fromType, int fromSlot)
    {
        InventoryType[] inventoryTypes =
        {
            InventoryType.Inventory1,
            InventoryType.Inventory2,
            InventoryType.Inventory3,
            InventoryType.Inventory4
        };

        foreach (var invType in inventoryTypes)
        {
            if (MoveItem(fromType, fromSlot, invType))
                return true;
        }

        return false;
    }

    private static unsafe int FindEmptySlot(InventoryType inventoryType)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null) return -1;

        var container = inventoryManager->GetInventoryContainer(inventoryType);
        if (container == null) return -1;

        for (int i = 0; i < container->Size; i++)
        {
            var slot = container->GetInventorySlot(i);
            if (slot != null && slot->ItemId == 0)
                return i;
        }

        return -1;
    }

    private static unsafe bool CloseSaddlebag()
    {
        try
        {
            var agentInventory = AgentInventory.Instance();
            if (agentInventory != null)
                agentInventory->Hide();

            return true;
        }
        catch (Exception ex)
        {
            DService.Log.Warning($"AutoBuyMaps: CloseSaddlebag error - {ex.Message}");
            return false;
        }
    }

    protected class Config : ModuleConfiguration
    {
        public uint TargetMapID = 0;
        public int TargetCount = 3;
        public int MaxPrice = 0;
        public bool AutoDecipherFirst = true;
        public bool MoveToSaddlebag = true;
        public bool MonitorPrice = false;
        public int CheckDelayMs = 1000;
        public int PurchaseTimeoutMs = 10000;
    }
}

internal static class ImGuiColorExtensions
{
    internal static uint ToUint(this Vector4 color)
    {
        return ImGui.ColorConvertFloat4ToU32(color);
    }
}
