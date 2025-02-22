using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Status = Dalamud.Game.ClientState.Statuses.Status;


namespace DailyRoutines.Modules;

public class AutoUseManual : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoUseManual"),               //"自动使用生存学/工程学指南"
        Description = GetLoc("AutoUseManualDescribe"), //"当前职业为非满级生产，采集职业时，自动按优先级吃指南获得经验加成buff"
        Category = ModuleCategories.Action,
    };

    private static Config ModuleConfig = null!;
    private static DateTime LastTime = DateTime.MinValue;
    private const int CooldownSeconds = 10;
    

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        TaskHelper ??= new TaskHelper { TimeLimitMS = 60000 };
        
        DService.Framework.Update += OnFrameworkUpdate;
    }

    public static bool IsGatherJob()
    {
        var id = DService.ClientState.LocalPlayer.ClassJob.RowId;
        return id == 16 || id == 17 || id == 18;
    }
    
    public static bool IsArtJob()
    {
        var id = DService.ClientState.LocalPlayer.ClassJob.RowId;
        return id >= 8 && id <= 15;
    }

    public static bool IsMaxLevel()
    {
        var level = DService.ClientState.LocalPlayer.Level;
        return level == 100;
    }
    
    private static bool IsCooldownElapsed() => (DateTime.Now - LastTime).TotalSeconds >= CooldownSeconds;
    private void OnFrameworkUpdate(IFramework iFramework)
    {
        var id = DService.ClientState.LocalPlayer.ClassJob.RowId;
        
        if (IsValidState() && IsCooldownElapsed())
        {
            if (IsGatherJob() && !IsMaxLevel() && !HasGather())
            {
                GatherCheckAndUseItems();
            }

            if (IsArtJob() && !IsMaxLevel() && !HasArt())
            {
                ArtCheckAndUseItems();
            }
        }
    }
    
    private void GatherCheckAndUseItems()
    {
        uint[] itemIds = { 26553, 12668, 4635, 4633 };

        foreach (uint itemId in itemIds)
        {
            CheckAndUse(itemId);
        }
    }
    

    private void ArtCheckAndUseItems()
    {
        uint[] itemIds = { 26554, 12667, 4634, 4632 };

        foreach (uint itemId in itemIds)
        {
            CheckAndUse(itemId);
        }
    }
    
    private bool CheckAndUse(uint itemid)
    {
        if (GetItemCount(itemid, false) == 0)
            return false;
        UseItem(itemid);
        return true;
    }

    private bool UseItem(uint itemId)
    {
        TaskHelper.Abort();
        UseActionManager.UseActionLocation(ActionType.Item, itemId, 0xE0000000, default, 0xFFFF);
        LastTime = DateTime.Now; // 更新最后使用时间
        TaskHelper.DelayNext(3_000);
        return true;
    }
    

    public static unsafe uint GetItemCount(uint itemId, bool isHq = false)
    {
        // 获取 InventoryManager 的实例指针
        IntPtr inventoryManagerPtr = (IntPtr)InventoryManager.Instance();

        // 将指针转换为 InventoryManager 结构体
        InventoryManager inventoryManager = Marshal.PtrToStructure<InventoryManager>(inventoryManagerPtr);

        // 调用 GetInventoryItemCount 方法
        return (uint)inventoryManager.GetInventoryItemCount(itemId, isHq, true, true, (short)0);
    }
    

    public override void ConfigUI()
    {
        ImGui.Text(GetLoc("ManualNotice"));//"发送通知:"
        ImGui.SameLine();
        ImGui.Checkbox("##AutoCheckgysahl_greensUsageSendNotice", ref ModuleConfig.SendNotice);
        SaveConfig(ModuleConfig);
    }
    

    private static unsafe bool IsValidState() =>
        !BetweenAreas &&
        !OccupiedInEvent &&
        !IsCasting &&
        DService.ClientState.LocalPlayer != null &&
        IsScreenReady() &&
        ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, 2) == 0;

    private bool HasGather()
    {
        var me = DService.ClientState.LocalPlayer;
        if (DService.ClientState.LocalPlayer == null) return false;
        if (HasAura(me, 46, 0))
            return true;
        return false;
    }
    
    private bool HasArt()
    {
        var me = DService.ClientState.LocalPlayer;
        if (DService.ClientState.LocalPlayer == null) return false;
        if (HasAura(me, 45, 0))
            return true;
        return false;
    }

    public bool HasAura(IBattleChara battleCharacter, uint id, int timeLeft)
    {
        if (battleCharacter == null)
            return false;
        for (int index = 0; index < battleCharacter.StatusList.Length; ++index)
        {
            Status status = battleCharacter.StatusList[index];
            if (status != null && status.StatusId != 0U && (int)status.StatusId == (int)id)
            {
                if (timeLeft == 0 || (double)Math.Abs(status.RemainingTime) * 1000.0 >= (double)timeLeft)
                    return true;
            }
        }
        return false;
    }
    
    private class Config : ModuleConfiguration
    {
        
        public bool SendNotice = true;
    }
    
    public override void Uninit()
    {
        DService.Framework.Update -= OnFrameworkUpdate;

        base.Uninit();
    }
}
