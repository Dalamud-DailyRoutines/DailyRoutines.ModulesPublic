/*
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using Newtonsoft.Json;

namespace DailyRoutines.ModulesPublic;

public class AutoRisingstoneCheckIn : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "自动石之家签到",
        Description = "自动登录石之家社区并每日签到领取奖励",
        Category    = ModuleCategories.General,
        Author      = ["Rorinnn"]
    };

    public override ModulePermission Permission { get; } = new() { CNOnly = true };

    private static Config? ModuleConfig;
    
    private static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FastOpRetryInterval  = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan WafRetryInterval     = TimeSpan.FromMinutes(10);
    
    private static int?      RisingstonePort;
    private static bool      IsRunning;
    private static string    LastSignInResult = string.Empty;
    private static DateTime? NextAttemptAfter;

    protected override void Init()
    {
        try
        {
            RisingstonePort = GetPortFromLauncherArgument("XL.Risingstone");
        }
        catch
        {
            RisingstonePort = null;
            
            Chat($"[{Info.Title}]\n连接 XIVLauncherCN 石之家服务失败");
            NotificationWarning($"[{Info.Title}]\n连接 XIVLauncherCN 石之家服务失败");
            
            return;
        }
        
        ModuleConfig = LoadConfig<Config>() ?? new();

        LastSignInResult = string.Empty;
        IsRunning        = false;
        NextAttemptAfter = DateTime.Now;

        TaskHelper ??= new() { TimeLimitMS = 30_000 };
        TaskHelper.EnqueueAsync(() => ExecuteCheckIn(this));
        
        FrameworkManager.Reg(OnUpdate, throttleMS: 60_000);
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox($"发送聊天消息###SendChat", ref ModuleConfig.SendChat))
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox("发送通知###SendNotification", ref ModuleConfig.SendNotification))
            SaveConfig(ModuleConfig);
        
        ImGui.NewLine();
        
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), "XIVLauncherCN 石之家服务连接状态:");

        ImGui.SameLine();
        if (RisingstonePort.HasValue)
            ImGui.TextColored(KnownColor.LightGreen.ToVector4(), "已连接");
        else
            ImGui.TextColored(KnownColor.Orange.ToVector4(), "未连接");
        
        ImGui.NewLine();

        using (ImRaii.Disabled(IsRunning))
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.PaperPlane, "立即签到"))
                TaskHelper.EnqueueAsync(() => ExecuteCheckIn(this));
        }

        if (IsRunning)
        {
            ImGui.SameLine();
            ImGui.TextColored(KnownColor.OrangeRed.ToVector4(), "处理中...");
        }

        if (!string.IsNullOrWhiteSpace(LastSignInResult) && ModuleConfig.LastSignInTime.HasValue)
        {
            ImGui.NewLine();
            
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), "上次签到信息");

            using (ImRaii.PushIndent())
            {
                ImGui.Text($"返回信息: {LastSignInResult}");
                ImGui.Text($"操作时间: {ModuleConfig.LastSignInTime:yyyy-MM-dd HH:mm:ss}");
            }
        }
    }

    protected override void Uninit()
    {
        FrameworkManager.Unreg(OnUpdate);
        
        RisingstonePort  = null;
        NextAttemptAfter = null;
    }

    private void OnUpdate(IFramework framework)
    {
        if (RisingstonePort is null or 0)
            return;
        
        var now        = DateTime.Now;
        var today      = now.Date;
        var lastSignIn = ModuleConfig.LastSignInTime?.Date;

        if (!TaskHelper.IsBusy && !IsRunning && lastSignIn != today && now.TimeOfDay >= TimeSpan.FromMinutes(1))
        {
            if (NextAttemptAfter is null || now >= NextAttemptAfter.Value)
                TaskHelper.EnqueueAsync(() => ExecuteCheckIn(this));
        }
    }
    
    private static async Task<bool?> ExecuteCheckIn(AutoRisingstoneCheckIn instance)
    {
        if (IsRunning) return false;
        IsRunning = true;

        try
        {
            if (RisingstonePort is null or 0)
            {
                LastSignInResult = "未连接 XIVLauncherCN 石之家服务";
                NextAttemptAfter = DateTime.Now + DefaultRetryInterval;
                return true;
            }

            var result = await ExecuteCheckInViaXL(RisingstonePort);
            LastSignInResult = result.Message;

            if (result.Success && ModuleConfig != null)
            {
                ModuleConfig.LastSignInTime = result.LastSignInTime;
                instance.SaveConfig(ModuleConfig);
            }

            // 已签到 / 操作太快 拦截通知
            var shouldNotify = result.Code != 10001 && result.Code != 10301;
            if (shouldNotify)
            {
                if (ModuleConfig.SendChat)
                    Chat($"[自动石之家签到] {(result.Success ? "签到成功" : "签到失败")}\n{result.Message}");
                if (ModuleConfig.SendNotification)
                    NotificationInfo(result.Message, $"[自动石之家签到] {(result.Success ? "签到成功" : "签到失败")}");
            }

            if (result is { Success: true, Code: 10000 or 10001 })
            {
                NextAttemptAfter = DateTime.Today.AddDays(1).AddMinutes(1);
                ModuleConfig.LastSignInTime = result.LastSignInTime ?? DateTime.Now;
                instance.SaveConfig(ModuleConfig);
                return true;
            }

            if (result.Code == 10301)
            {
                NextAttemptAfter = DateTime.Now + FastOpRetryInterval;
                ModuleConfig.LastSignInTime = DateTime.Now;
                instance.SaveConfig(ModuleConfig);
                return true;
            }

            if (result.Message.Contains("WAF", StringComparison.OrdinalIgnoreCase))
            {
                NextAttemptAfter = DateTime.Now + WafRetryInterval;
                return true;
            }

            NextAttemptAfter = DateTime.Now + DefaultRetryInterval;
        }
        catch (Exception ex)
        {
            var isSame = LastSignInResult == ex.Message;
            LastSignInResult = $"{ex.Message}";
            Error("自动石之家签到失败", ex);

            if (!isSame)
            {
                if (ModuleConfig.SendChat)
                    Chat($"[自动石之家签到] 签到失败\n{LastSignInResult}");
                if (ModuleConfig.SendNotification)
                    NotificationInfo(LastSignInResult, "[自动石之家签到] 签到失败");
            }

            NextAttemptAfter = DateTime.Now + DefaultRetryInterval;
        }
        finally
        {
            IsRunning = false;
        }

        return true;
    }
    
    private static async Task<CheckInResult> ExecuteCheckInViaXL(int? risingstonePort)
    {
        if (risingstonePort is null or 0)
            return new() { Success = false, Code = 0, Message = "连接 XIVLauncherCN 石之家服务失败" };

        var apiUrl      = $"http://127.0.0.1:{risingstonePort}/risingstone/";
        var rpcRequest  = new RpcRequest { Method = "ExecuteCheckIn", Params = [] };
        var jsonPayload = JsonConvert.SerializeObject(rpcRequest);

        var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
        {
            Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
        };

        var response = await HttpClientHelper.Get().SendAsync(request);
        response.EnsureSuccessStatusCode();

        var content     = await response.Content.ReadAsStringAsync();
        var rpcResponse = JsonConvert.DeserializeObject<RpcResponse>(content);

        if (rpcResponse?.Error != null)
            return new() { Success = false, Code = 0, Message = $"{rpcResponse.Error}" };

        if (rpcResponse?.Result != null)
        {
            var result = JsonConvert.DeserializeObject<XLCheckInResult>(rpcResponse.Result.ToString());
            if (result != null)
            {
                return new CheckInResult
                {
                    Success        = result.Success,
                    Code           = result.Code,
                    Message        = result.Message,
                    LastSignInTime = result.LastSignInTime
                };
            }
        }

        return new() { Success = false, Code = 0, Message = "响应为空" };
    }
    
    private static unsafe int GetPortFromLauncherArgument(string paramName)
    {
        var key        = $"{paramName}=";
        var gameWindow = GameWindow.Instance();
        for (var i = 0; i < gameWindow->ArgumentCount; i++)
        {
            var arg = gameWindow->ArgumentsSpan[i].ToString();
            if (arg.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            {
                var portString = arg[key.Length..];
                if (int.TryParse(portString, out var port))
                    return port;
            }
        }
        
        throw new Exception($"未能从游戏参数中获取 {paramName} 端口");
    }

    #region Models

    private class Config : ModuleConfiguration
    {
        public bool      SendChat         = true;
        public bool      SendNotification = true;
        public DateTime? LastSignInTime;
    }

    private class RpcRequest
    {
        [JsonPropertyName("Method")]
        public string Method { get; set; } = string.Empty;

        [JsonPropertyName("Params")]
        public object[] Params { get; set; } = [];
    }

    private class RpcResponse
    {
        [JsonPropertyName("Result")]
        public object? Result { get; set; }

        [JsonPropertyName("Error")]
        public string? Error { get; set; }
    }

    private class XLCheckInResult
    {
        [JsonPropertyName("Success")]
        public bool Success { get; set; }

        [JsonPropertyName("Code")]
        public int Code { get; set; }

        [JsonPropertyName("Message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("LastSignInTime")]
        public DateTime? LastSignInTime { get; set; }
    }

    private class CheckInResult
    {
        public bool      Success { get; set; }
        public int       Code    { get; set; }
        public string    Message { get; set; } = string.Empty;
        public DateTime? LastSignInTime { get; set; }
    }

    #endregion
}
*/
