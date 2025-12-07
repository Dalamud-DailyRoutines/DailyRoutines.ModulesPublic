using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using DailyRoutines.Managers;
using Dalamud.Interface.Components;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace DailyRoutines.ModulesPublic;

public class AutoRisingstoneSignIn : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "自动石之家签到",
        Description = "自动进行每日的石之家签到和签到奖励领取",
        Category    = ModuleCategories.Assist,
        Author      = ["Rorinnn"]
    };

    public override ModulePermission Permission { get; } = new() { CNOnly = true };

    private Config? ModuleConfig;
    private int?    RisingstonePort;
    private bool    IsRunning;
    private string  LastSignInResult = string.Empty;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        LastSignInResult = string.Empty;
        IsRunning        = false;

        // 尝试获取端口
        try
        {
            RisingstonePort = GetLauncherPort("XL.Risingstone");
        }
        catch
        {
        }

        // 模块初始化时强制签到一次
        TaskHelper ??= new() { TimeLimitMS = 30_000 };
        TaskHelper.EnqueueAsync(() => ExecuteSignIn(this));
        
        // 注册 Framework 更新事件，每分钟检查一次
        FrameworkManager.Reg(OnFrameworkUpdate, throttleMS: 60_000);
    }

    protected override void ConfigUI()
    {
        if (ModuleConfig == null) return;

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), "Risingstone 连接状态");

        ImGui.SameLine();
        if (RisingstonePort.HasValue)
            ImGui.TextColored(KnownColor.LightGreen.ToVector4(), "已连接");
        else
            ImGui.TextColored(KnownColor.Orange.ToVector4(), "未连接");

        ImGui.NewLine();

        if (ImGui.Checkbox("###SendChat", ref ModuleConfig.SendChat))
            SaveConfig(ModuleConfig);
        
        ImGui.SameLine();
        ImGui.Text("发送聊天消息");

        if (ImGui.Checkbox("###SendNotification", ref ModuleConfig.SendNotification))
            SaveConfig(ModuleConfig);
        
        ImGui.SameLine();
        ImGui.Text("发送通知");

        ImGui.NewLine();

        using (ImRaii.Disabled(IsRunning))
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.PaperPlane, "立即签到"))
                TaskHelper.EnqueueAsync(() => ExecuteSignIn(this));
        }

        if (IsRunning)
        {
            ImGui.SameLine();
            ImGui.TextColored(KnownColor.Yellow.ToVector4(), "处理中...");
        }

        if (!string.IsNullOrWhiteSpace(LastSignInResult))
        {
            ImGui.NewLine();
            ImGui.Text("上次签到结果:");
            ImGui.TextWrapped(LastSignInResult);
        }

        if (!ModuleConfig.LastSignInTime.HasValue) return;

        ImGui.NewLine();
        ImGui.Text($"上次签到时间: {ModuleConfig.LastSignInTime:yyyy-MM-dd HH:mm:ss}");
    }

    protected override void Uninit()
    {
        FrameworkManager.Unreg(OnFrameworkUpdate);
        TaskHelper?.Abort();
        RisingstonePort = null;
    }

    #region XIVLauncher API

    /// <summary>
    /// Framework 更新事件处理，每分钟检查一次是否需要签到
    /// </summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        // 检查是否需要签到（每天 0:01 之后且今天还未签到）
        if (ModuleConfig == null) return;
        
        var now = DateTime.Now;
        var today = now.Date;
        var lastSignIn = ModuleConfig.LastSignInTime?.Date;

        // 如果今天还没签到，且时间已经过了 0:01
        if (lastSignIn != today && now.TimeOfDay >= TimeSpan.FromMinutes(1))
            TaskHelper?.EnqueueAsync(() => ExecuteSignIn(this));
    }

    /// <summary>
    /// 从游戏启动参数获取 XIVLauncher 端口
    /// </summary>
    private static unsafe int GetLauncherPort(string paramName)
    {
        var key = $"{paramName}=";
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

    /// <summary>
    /// 通过 XIVLauncher API 执行签到
    /// </summary>
    private static async Task<SignInResult> ExecuteSignInViaXL(int? risingstonePort)
    {
        if (!risingstonePort.HasValue)
            return new SignInResult { Success = false, Message = "XIVLauncher Risingstone 服务未连接" };

        try
        {
            var apiUrl = $"http://127.0.0.1:{risingstonePort}/risingstone/";
            var rpcRequest = new RpcRequest { Method = "ExecuteSignIn", Params = Array.Empty<object>() };
            var jsonPayload = JsonSerializer.Serialize(rpcRequest);

            var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
            {
                Content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json")
            };

            var response = await HttpClientHelper.Get().SendAsync(request);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            var rpcResponse = JsonSerializer.Deserialize<RpcResponse>(content);

            if (rpcResponse?.Error != null)
                return new SignInResult { Success = false, Message = $"签到失败: {rpcResponse.Error}" };

            if (rpcResponse?.Result is JsonElement element)
            {
                var result = JsonSerializer.Deserialize<XLSignInResult>(element.GetRawText());
                if (result != null)
                {
                    return new SignInResult
                    {
                        Success = result.Success,
                        Message = result.Message,
                        LastSignInTime = result.LastSignInTime
                    };
                }
            }

            return new SignInResult { Success = false, Message = "签到失败: 响应为空" };
        }
        catch (Exception ex)
        {
            return new SignInResult { Success = false, Message = $"签到失败: {ex.Message}" };
        }
    }

    #endregion

    #region Sign In

    /// <summary>
    /// 执行签到的 TaskHelper 任务
    /// </summary>
    private async Task<bool?> ExecuteSignIn(AutoRisingstoneSignIn instance)
    {
        if (instance.IsRunning) return false;

        instance.IsRunning = true;

        try
        {
            var result = await ExecuteSignInViaXL(instance.RisingstonePort);
            instance.LastSignInResult = result.Message;

            if (result.Success && instance.ModuleConfig != null)
            {
                instance.ModuleConfig.LastSignInTime = result.LastSignInTime;
                instance.SaveConfig(instance.ModuleConfig);
            }

            if (instance.ModuleConfig?.SendChat == true)
                Chat($"[自动石之家签到] {result.Message}");
            if (instance.ModuleConfig?.SendNotification == true)
                NotificationInfo("自动石之家签到", result.Message);
        }
        catch (Exception ex)
        {
            instance.LastSignInResult = $"签到异常: {ex.Message}";

            if (instance.ModuleConfig?.SendChat == true)
                Chat($"[自动石之家签到] {instance.LastSignInResult}");
            if (instance.ModuleConfig?.SendNotification == true)
                NotificationInfo("自动石之家签到", instance.LastSignInResult);
        }
        finally
        {
            instance.IsRunning = false;
        }

        return true;
    }

    #endregion

    #region Models

    private class Config : ModuleConfiguration
    {
        public bool      SendChat        = true;
        public bool      SendNotification;
        public DateTime? LastSignInTime;
    }

    private class RpcRequest
    {
        [JsonPropertyName("Method")]
        public string Method { get; set; } = string.Empty;

        [JsonPropertyName("Params")]
        public object[] Params { get; set; } = Array.Empty<object>();
    }

    private class RpcResponse
    {
        [JsonPropertyName("Result")]
        public object? Result { get; set; }

        [JsonPropertyName("Error")]
        public string? Error { get; set; }
    }

    private class XLSignInResult
    {
        [JsonPropertyName("Success")]
        public bool Success { get; set; }

        [JsonPropertyName("Message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("LastSignInTime")]
        public DateTime? LastSignInTime { get; set; }
    }

    private class SignInResult
    {
        public bool      Success { get; set; }
        public string    Message { get; set; } = string.Empty;
        public DateTime? LastSignInTime { get; set; }
    }

    #endregion
}
