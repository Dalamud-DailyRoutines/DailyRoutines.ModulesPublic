using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoRecordSubTimeLeft : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "自动记录剩余游戏时间",
        Description = "登录时, 自动记录保存当前账号剩余的游戏时间, 并显示在服务器信息栏",
        Category    = ModuleCategories.General,
        Author      = ["Due"]
    };

    public override ModulePermission Permission { get; } = new() { CNOnly = true };

    private static readonly CompSig                          AgentLobbyOnLoginSig = new("E8 ?? ?? ?? ?? 41 C6 46 08 01 E9 ?? ?? ?? ?? 83 FB 03");
    private delegate        nint                             AgentLobbyOnLoginDelegate(AgentLobby* agent);
    private static          Hook<AgentLobbyOnLoginDelegate>? AgentLobbyOnLoginHook;
    
    private static Config        ModuleConfig = null!;
    private static IDtrBarEntry? Entry;

    protected override void Init()
    {
        ModuleConfig =   LoadConfig<Config>() ?? new();
        TaskHelper   ??= new();

        Entry         ??= DService.DtrBar.Get("DailyRoutines-GameTimeLeft");
        Entry.OnClick =   () => ChatHelper.SendMessage($"/pdr search {GetType().Name}");

        RefreshEntry();

        AgentLobbyOnLoginHook ??= AgentLobbyOnLoginSig.GetHook<AgentLobbyOnLoginDelegate>(AgentLobbyOnLoginDetour);
        AgentLobbyOnLoginHook.Enable();
        
        DService.ClientState.Login  += OnLogin;
        DService.ClientState.Logout += OnLogout;

        FrameworkManager.Register(OnUpdate, throttleMS: 5_000);
    }

    protected override void ConfigUI()
    {
        var contentID = LocalPlayerState.ContentID;
        if (contentID == 0) return;

        if (!ModuleConfig.Infos.TryGetValue(contentID, out var info) ||
            info.Record == DateTime.MinValue                         ||
            (info.LeftMonth == TimeSpan.MinValue && info.LeftTime == TimeSpan.MinValue))
        {
            ImGui.TextColored(Orange, "当前角色暂无数据, 请重新登录游戏以记录");
            return;
        }

        ImGui.TextColored(LightSkyBlue, "上次记录:");

        ImGui.SameLine();
        ImGui.Text($"{info.Record}");
        
        ImGui.NewLine();

        ImGui.TextColored(LightSkyBlue, "月卡剩余时间:");

        ImGui.SameLine();
        ImGui.Text(FormatTimeSpan(info.LeftMonth == TimeSpan.MinValue ? TimeSpan.Zero : info.LeftMonth));
        
        ImGui.TextColored(LightSkyBlue, "点卡剩余时间:");

        ImGui.SameLine();
        ImGui.Text(FormatTimeSpan(info.LeftTime));
    }

    protected override void Uninit()
    {
        FrameworkManager.Unregister(OnUpdate);
        
        Entry?.Remove();
        Entry = null;
        
        DService.ClientState.Login  -= OnLogin;
        DService.ClientState.Logout -= OnLogout;
        
        base.Uninit();
    }
    
    private void OnLogin()
    {
        TaskHelper.Enqueue(() =>
        {
            var contentID = LocalPlayerState.ContentID;
            if (contentID == 0) return false;
            
            RefreshEntry(contentID);
            return true;
        });
    }

    private static void OnUpdate(IFramework _) => RefreshEntry();

    private void OnLogout(int code, int type) => TaskHelper?.Abort();

    private nint AgentLobbyOnLoginDetour(AgentLobby* agent)
    {
        var ret = AgentLobbyOnLoginHook.Original(agent);
        UpdateSubInfo(agent);
        return ret;
    }

    private void UpdateSubInfo(AgentLobby* agent)
    {
        TaskHelper.Enqueue(() =>
        {
            try
            {
                var info = agent->LobbyData.LobbyUIClient.SubscriptionInfo;
                if (info == null) return false;

                var contentID = agent->LobbyData.ContentId;
                if (contentID == 0) return false;
                
                var timeInfo = GetLeftTimeSecond(*info);
                ModuleConfig.Infos[contentID]
                    = new(DateTime.Now,
                          timeInfo.MonthTime == 0 ? TimeSpan.MinValue : TimeSpan.FromSeconds(timeInfo.MonthTime),
                          timeInfo.PointTime == 0 ? TimeSpan.MinValue : TimeSpan.FromSeconds(timeInfo.PointTime));
                ModuleConfig.Save(this);

                RefreshEntry(contentID);
            }
            catch (Exception ex)
            {
                Warning("更新游戏点月卡订阅信息失败", ex);
                NotificationWarning(ex.Message, "更新游戏点月卡订阅信息失败");
            }
            
            return true;
        }, "更新订阅信息");
    }

    private static (int MonthTime, int PointTime) GetLeftTimeSecond(LobbySubscriptionInfo info)
    {
        var size = Marshal.SizeOf(info);
        var arr = new byte[size];
        var ptr = nint.Zero;

        try
        {
            ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(info, ptr, true);
            Marshal.Copy(ptr, arr, 0, size);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }

        var month = string.Join(string.Empty, arr.Skip(16).Take(3).Reverse().Select(x => x.ToString("X2")));
        var point = string.Join(string.Empty, arr.Skip(24).Take(3).Reverse().Select(x => x.ToString("X2")));
        return (Convert.ToInt32(month, 16), Convert.ToInt32(point, 16));
    }

    private static void RefreshEntry(ulong contentID = 0)
    {
        if (Entry == null) return;
        
        if (contentID == 0) 
            contentID = LocalPlayerState.ContentID;
        
        if (contentID == 0                                           ||
            DService.Condition[ConditionFlag.InCombat]               ||
            !ModuleConfig.Infos.TryGetValue(contentID, out var info) ||
            info.Record == DateTime.MinValue                         ||
            (info.LeftMonth == TimeSpan.MinValue && info.LeftTime == TimeSpan.MinValue))
        {
            Entry.Shown = false;
            return;
        }

        var isMonth = info.LeftMonth != TimeSpan.MinValue;
        var expireTime = info.Record + (isMonth ? info.LeftMonth : info.LeftTime);
        
        Entry.Text = $"{(isMonth ? "月卡" : "点卡")}: {expireTime:MM/dd HH:mm}";
        Entry.Tooltip = $"过期时间:\n{expireTime}\n" +
                        $"剩余时间:\n{FormatTimeSpan(expireTime - DateTime.Now)}";
        Entry.Shown = true;
    }
    
    public static string FormatTimeSpan(TimeSpan timeSpan) =>
        $"{timeSpan.Days} 天 {timeSpan.Hours} 小时 {timeSpan.Minutes} 分 {timeSpan.Seconds} 秒";

    private class Config : ModuleConfiguration
    {
        public Dictionary<ulong, (DateTime Record, TimeSpan LeftMonth, TimeSpan LeftTime)> Infos = [];
    }
}
