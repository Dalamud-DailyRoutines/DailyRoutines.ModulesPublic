using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;

namespace DailyRoutines.Modules;

public unsafe class AutoRecordSubTimeLeft : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title       = GetLoc("AutoRecordSubTimeLeftTitle"),
        Description = GetLoc("AutoRecordSubTimeLeftDescription"),
        Category    = ModuleCategories.General,
        Author      = ["Due"]
    };

    public override ModulePermission Permission => new() { CNOnly = true };

    private static readonly CompSig AgentLobbyOnLoginSig = new("E8 ?? ?? ?? ?? 41 C6 46 08 01 E9 ?? ?? ?? ?? 83 FB 03");
    private delegate nint AgentLobbyOnLoginDelegate(AgentLobby* agent);
    private static Hook<AgentLobbyOnLoginDelegate>? AgentLobbyOnLoginHook;
    
    private static Config        ModuleConfig = null!;
    private static IDtrBarEntry? Entry;
    
    public override void Init()
    {
        ModuleConfig =   LoadConfig<Config>() ?? new();
        TaskHelper   ??= new();

        Entry         ??= DService.DtrBar.Get("DailyRoutines-GameTimeLeft");
        Entry.OnClick =   () => ChatHelper.Instance.SendMessage($"/pdr search {GetType().Name}");

        RefreshEntry();

        AgentLobbyOnLoginHook ??= AgentLobbyOnLoginSig.GetHook<AgentLobbyOnLoginDelegate>(AgentLobbyOnLoginDetour);
        AgentLobbyOnLoginHook.Enable();
        
        DService.ClientState.Login  += OnLogin;
        DService.ClientState.Logout += OnLogout;
    }

    public override void ConfigUI()
    {
        var contentID = DService.ClientState.LocalContentId;
        if (contentID == 0) return;
        
        if (!ModuleConfig.Infos.TryGetValue(contentID, out var info) || info.Record == DateTime.MinValue ||
            (info.LeftMonth == TimeSpan.MinValue && info.LeftTime == TimeSpan.MinValue))
        {
            ImGui.TextColored(Orange, GetLoc("AutoRecordSubTimeLeft-NoData"));
            return;
        }

        ImGui.TextColored(LightSkyBlue, $"{GetLoc("AutoRecordSubTimeLeft-LastRecordTime")}:");

        ImGui.SameLine();
        ImGui.Text($"{info.Record}");

        ImGui.TextColored(LightSkyBlue, $"{GetLoc("AutoRecordSubTimeLeft-MonthSub")} {GetLoc("AutoRecordSubTimeLeft-TimeTill")}:");

        ImGui.SameLine();
        ImGui.Text(FormatTimeSpan(info.LeftMonth, CultureInfo.CurrentCulture));
        
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("AutoRecordSubTimeLeft-TimeSub")} {GetLoc("AutoRecordSubTimeLeft-TimeTill")}:");

        ImGui.SameLine();
        ImGui.Text(FormatTimeSpan(info.LeftTime, CultureInfo.CurrentCulture));
    }

    public override void Uninit()
    {
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
            var contentID = DService.ClientState.LocalContentId;
            if (contentID == 0) return false;
            
            RefreshEntry(contentID);
            return true;
        });
    }

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
                ModuleConfig.Save(ModuleManager.GetModule<AutoRecordSubTimeLeft>());

                RefreshEntry(contentID);
            }
            catch (Exception ex)
            {
                Debug("更新订阅信息失败", ex);
            }
            
            return true;
        }, "更新订阅信息");
    }

    private static (int MonthTime, int PointTime) GetLeftTimeSecond(LobbySubscriptionInfo info)
    {
        var size = Marshal.SizeOf(info);
        var arr = new byte[size];
        var ptr = IntPtr.Zero;

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
        if (contentID == 0) contentID = DService.ClientState.LocalContentId;
        if (contentID == 0) return;
        
        if (!ModuleConfig.Infos.TryGetValue(contentID, out var info) || info.Record == DateTime.MinValue ||
            (info.LeftMonth == TimeSpan.MinValue && info.LeftTime == TimeSpan.MinValue))
            return;
        
        var isMonth = info.LeftMonth != TimeSpan.MinValue;
        var expireTime = info.Record + (isMonth ? info.LeftMonth : info.LeftTime);
        
        Entry.Text =
            $"{GetLoc($"AutoRecordSubTimeLeft-{(isMonth ? "Month" : "Time")}Sub")}: {expireTime:MM/dd HH:mm}";
        Entry.Tooltip = $"{GetLoc("AutoRecordSubTimeLeft-ExpireTime")}:\n{expireTime}\n" +
                        $"{GetLoc("AutoRecordSubTimeLeft-TimeTill")}:\n{FormatTimeSpan(isMonth ? info.LeftMonth : info.LeftTime, CultureInfo.CurrentCulture)}";
        Entry.Shown = true;
    }
    
    public static string FormatTimeSpan(TimeSpan timeSpan, CultureInfo culture) =>
        culture.TwoLetterISOLanguageName switch
        {
            "zh" => $"{timeSpan.Days} 天 {timeSpan.Hours} 小时 {timeSpan.Minutes} 分 {timeSpan.Seconds} 秒",
            _    => $"{timeSpan.Days} d {timeSpan.Hours} h {timeSpan.Minutes} m {timeSpan.Seconds} s"
        };

    private class Config : ModuleConfiguration
    {
        public Dictionary<ulong, (DateTime Record, TimeSpan LeftMonth, TimeSpan LeftTime)> Infos = [];
    }
}
