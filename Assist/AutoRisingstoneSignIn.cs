using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DailyRoutines.Abstracts;
using Dalamud.Interface.Components;
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

    private static Config ModuleConfig = null!;
    private static CancellationTokenSource? CancellationTokenSource;
    private static AutoRisingstoneSignIn? Instance;

    private static bool IsRunning;
    private static string LastSignInResult = string.Empty;
    private static string ConnectionStatus = string.Empty;

    // XIVLauncher 本地 API 客户端
    private static HttpClient? xlApiClient;
    private static int? risingstonePort;

    protected override void Init()
    {
        try
        {
            Instance = this;
            ModuleConfig = LoadConfig<Config>() ?? new();

            LastSignInResult = string.Empty;
            ConnectionStatus = string.Empty;
            IsRunning = false;

            TaskHelper ??= new();
            
            InitializeHttpClient();

            // 模块初始化时尝试签到
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var result = await ExecuteSignInViaXL();
                    if (result.Success)
                    {
                        ModuleConfig.LastSignInTime = result.LastSignInTime;
                        Instance?.SaveConfig(ModuleConfig);
                        
                        if (ModuleConfig.SendChat)
                            Chat($"[自动石之家签到] {result.Message}");
                        if (ModuleConfig.SendNotification)
                            NotificationInfo($"[自动石之家签到] {result.Message}");
                    }
                    else
                        ConnectionStatus = result.Message;
                }
                catch (Exception ex)
                {
                    ConnectionStatus = $"初始化失败: {ex.Message}";
                }
            });

            ScheduleDailySignIn();
        }
        catch
        {
        }
    }

    protected override void ConfigUI()
    {
        // XIVLauncher 连接状态
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), "Risingstone 连接状态");
        ImGui.SameLine();
        
        if (risingstonePort.HasValue)
            ImGui.TextColored(KnownColor.LightGreen.ToVector4(), "已连接");
        else
            ImGui.TextColored(KnownColor.Orange.ToVector4(), "未连接");

        ImGui.NewLine();

        // 通知选项
        ImGui.AlignTextToFramePadding();
        if (ImGui.Checkbox("###SendChat", ref ModuleConfig.SendChat))
            Instance?.SaveConfig(ModuleConfig);
        ImGui.SameLine();
        ImGui.Text("发送聊天消息");

        ImGui.AlignTextToFramePadding();
        if (ImGui.Checkbox("###SendNotification", ref ModuleConfig.SendNotification))
            Instance?.SaveConfig(ModuleConfig);
        ImGui.SameLine();
        ImGui.Text("发送通知");

        ImGui.NewLine();

        using (ImRaii.Disabled(IsRunning))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.PaperPlane, "立即签到"))
            {
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        IsRunning = true;
                        
                        var result = await ExecuteSignInViaXL();
                        LastSignInResult = result.Message;
                        
                        if (result.Success)
                        {
                            ModuleConfig.LastSignInTime = result.LastSignInTime;
                            Instance?.SaveConfig(ModuleConfig);
                            
                            if (ModuleConfig.SendChat)
                                Chat($"[自动石之家签到] {result.Message}");
                            if (ModuleConfig.SendNotification)
                                NotificationInfo($"[自动石之家签到] {result.Message}");
                        }
                    }
                    finally
                    {
                        IsRunning = false;
                    }
                });
            }
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
        CancellationTokenSource?.Cancel();
        CancellationTokenSource?.Dispose();
        CancellationTokenSource = null;
        
        xlApiClient?.Dispose();
        xlApiClient = null;
        
        risingstonePort = null;
        Instance = null;
    }

    #region XIVLauncher API

    /// <summary>
    /// 初始化 HttpClient
    /// </summary>
    private static void InitializeHttpClient()
    {
        // XIVLauncher API 客户端
        xlApiClient = new HttpClient();
        xlApiClient.DefaultRequestHeaders.TryAddWithoutValidation("Content-Type", "application/json");
        
        // 尝试获取端口
        try
        {
            risingstonePort = GetLauncherPort("XL.Risingstone");
            ConnectionStatus = $"已连接到 XIVLauncher (Risingstone: {risingstonePort})";
        }
        catch
        {
            ConnectionStatus = "未检测到 XIVLauncherCN，请确保使用 XIVLauncherCN 启动游戏";
        }
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
    private static async Task<SignInResult> ExecuteSignInViaXL()
    {
        if (xlApiClient == null || !risingstonePort.HasValue)
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
            
            var response = await xlApiClient.SendAsync(request);
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

    #region Scheduler

    /// <summary>
    /// 安排每日自动签到任务
    /// </summary>
    private static void ScheduleDailySignIn()
    {
        CancellationTokenSource?.Cancel();
        CancellationTokenSource?.Dispose();
        CancellationTokenSource = new();

        var token = CancellationTokenSource.Token;

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var now = DateTime.Now;
                    var today = now.Date;

                    // 如果今天还没签到，立即签到
                    if (!ModuleConfig.LastSignInTime.HasValue || ModuleConfig.LastSignInTime.Value.Date < today)
                    {
                        var result = await ExecuteSignInViaXL();
                        LastSignInResult = result.Message;
                        
                        if (result.Success)
                        {
                            ModuleConfig.LastSignInTime = result.LastSignInTime;
                            Instance?.SaveConfig(ModuleConfig);
                            
                            if (ModuleConfig.SendChat)
                                Chat($"[自动石之家签到] {result.Message}");
                            if (ModuleConfig.SendNotification)
                                NotificationInfo($"[自动石之家签到] {result.Message}");
                        }
                    }

                    // 计算下次签到时间（明天凌晨 0:01）
                    var nextSignIn = today.AddDays(1).AddMinutes(01);
                    var delay = nextSignIn - now;

                    if (delay.TotalMilliseconds > 0)
                        await System.Threading.Tasks.Task.Delay(delay, token);
                    else
                        await System.Threading.Tasks.Task.Delay(TimeSpan.FromHours(1), token);
                }
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    #endregion

    #region Models

    private class Config : ModuleConfiguration
    {
        public bool SendChat = true;
        public bool SendNotification = true;
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
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public DateTime? LastSignInTime { get; set; }
    }

    #endregion
}
