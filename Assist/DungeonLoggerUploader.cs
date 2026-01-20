using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using Newtonsoft.Json;
using ContentsFinder = FFXIVClientStructs.FFXIV.Client.Game.UI.ContentsFinder;

namespace DailyRoutines.ModulesPublic;

public class DungeonLoggerUploader : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "随机任务：指导者任务记录上传助手",
        Description = "配置完成后, 当进入“随机任务：指导者”任务副本并完成时, 自动记录并上传相关记录数据至 DungeonLogger 网站",
        Category    = ModuleCategories.Assist,
        Author      = ["Middo"]
    };

    public override ModulePermission Permission { get; } = new() { CNOnly = true };

    private const byte MENTOR_ROULETTE_ID = 9;

    private static Config ModuleConfig;
    
    private static HttpClient?      HTTPClientInstance;
    private static CookieContainer? Cookies;
    
    private static bool   IsLoggedIn;
    private static string DungeonName = string.Empty;
    private static string JobName     = string.Empty;
    private static bool   InDungeon;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        EnsureHTTPClient();

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        DService.Instance().DutyState.DutyCompleted      += OnDutyCompleted;

        if (!string.IsNullOrEmpty(ModuleConfig.Username) && !string.IsNullOrEmpty(ModuleConfig.Password))
            Task.Run(() => LoginAsync());
    }

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(300f * GlobalFontScale);
        ImGui.InputText("服务器地址", ref ModuleConfig.ServerURL, 256);
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(ModuleConfig);
        
        ImGui.SetNextItemWidth(300f * GlobalFontScale);
        ImGui.InputText("用户名", ref ModuleConfig.Username, 128);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            SaveConfig(ModuleConfig);
            IsLoggedIn = false;
        }

        ImGui.SetNextItemWidth(300f * GlobalFontScale);
        ImGui.InputText("密码", ref ModuleConfig.Password, 128);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            SaveConfig(ModuleConfig);
            IsLoggedIn = false;
        }
        
        ImGui.Spacing();

        if (ImGui.Button("测试登录"))
            Task.Run(() => LoginAsync(true));
            
        ImGui.SameLine(0, 4f * GlobalFontScale);
        if (IsLoggedIn)
            ImGui.TextColored(KnownColor.LawnGreen.ToVector4(), "已登录");
        else
            ImGui.TextColored(KnownColor.Red.ToVector4(), "未登录");

        ImGui.NewLine();

        using (ImRaii.Group())
        {
            if (ImGui.Checkbox("发送聊天信息", ref ModuleConfig.SendChat))
                SaveConfig(ModuleConfig);
        }
    }

    private static void OnZoneChanged(ushort _)
    {
        if (!IsLoggedIn) return;

        if (GameState.TerritoryType == 0 || GameState.ContentFinderCondition == 0) return;

        unsafe
        {
            var contentsFinder = ContentsFinder.Instance();
            if (contentsFinder != null)
            {
                var queueInfo = contentsFinder->GetQueueInfo();
                if (queueInfo                          == null ||
                    queueInfo->QueuedContentRouletteId != MENTOR_ROULETTE_ID)
                    return;
            }
        }
        
        InDungeon   = true;
        DungeonName = GameState.ContentFinderConditionData.Name.ToString();
        JobName     = LocalPlayerState.ClassJobData.Name.ToString();

        if (ModuleConfig.SendChat)
            Chat("已进入 “随机任务：指导者” 任务, 完成后将自动上传记录至网站");
    }

    private static void OnDutyCompleted(object? sender, ushort e)
    {
        if (!InDungeon) return;

        InDungeon = false;
        Task.Run(UploadDungeonRecordAsync);
    }

    private static async Task LoginAsync(bool showNotification = false)
    {
        EnsureHTTPClient();
        if (HTTPClientInstance == null                  ||
            string.IsNullOrEmpty(ModuleConfig.Username) ||
            string.IsNullOrEmpty(ModuleConfig.Password))
            return;

        try
        {
            var loginData = new
            {
                username = ModuleConfig.Username,
                password = ModuleConfig.Password
            };

            var content  = new StringContent(JsonConvert.SerializeObject(loginData), Encoding.UTF8, "application/json");
            var response = await HTTPClientInstance.PostAsync($"{ModuleConfig.ServerURL}/api/login", content);

            if (!response.IsSuccessStatusCode) return;

            var responseContent = await response.Content.ReadAsStringAsync();
            var result          = JsonConvert.DeserializeObject<DungeonLoggerResponse<AuthData>>(responseContent);

            if (result?.Code == 0)
            {
                IsLoggedIn = true;
                if (showNotification)
                    NotificationSuccess("登录成功");
            }
            else
            {
                IsLoggedIn = false;
                if (showNotification)
                    NotificationError($"登录失败: {result?.Msg}");
            }
        }
        catch (Exception ex)
        {
            IsLoggedIn = false;
            NotificationError($"登录 DungeonLogger 异常: {ex.Message}");
            Error("登录 DungeonLogger 失败", ex);
        }
    }

    private static async Task UploadDungeonRecordAsync()
    {
        EnsureHTTPClient();
        if (HTTPClientInstance == null ||
            string.IsNullOrEmpty(DungeonName))
            return;

        try
        {
            await LoginAsync();

            if (!IsLoggedIn)
                throw new Exception("未登录或登录失败");

            var mazeResponse = await HTTPClientInstance.GetAsync($"{ModuleConfig.ServerURL}/api/stat/maze");
            if (!mazeResponse.IsSuccessStatusCode)
                throw new Exception($"网站返回副本数据异常 ({mazeResponse.StatusCode})");

            var mazeContent = await mazeResponse.Content.ReadAsStringAsync();
            var mazeResult  = JsonConvert.DeserializeObject<DungeonLoggerResponse<List<StatMaze>>>(mazeContent);
            var maze        = mazeResult?.Data?.Find(m => m.Name.Equals(DungeonName));
            if (maze == null)
                throw new Exception($"网站无对应副本数据 ({DungeonName})");

            var profResponse = await HTTPClientInstance.GetAsync($"{ModuleConfig.ServerURL}/api/stat/prof");
            if (!profResponse.IsSuccessStatusCode)
                throw new Exception($"网站返回职业数据异常 ({profResponse.StatusCode})");

            var profContent = await profResponse.Content.ReadAsStringAsync();
            var profResult  = JsonConvert.DeserializeObject<DungeonLoggerResponse<List<StatProf>>>(profContent);
            var prof        = profResult?.Data?.Find(p => p.NameCn.Equals(JobName));
            if (prof is null)
                throw new Exception($"网站无对应职业数据 ({JobName})");

            var uploadData = new
            {
                mazeId  = maze.ID,
                profKey = prof.Key
            };

            var content  = new StringContent(JsonConvert.SerializeObject(uploadData), Encoding.UTF8, "application/json");
            var response = await HTTPClientInstance.PostAsync($"{ModuleConfig.ServerURL}/api/record", content);
            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                var result          = JsonConvert.DeserializeObject<DungeonLoggerResponse<object>>(responseContent);

                if (result?.Code == 0)
                {
                    if (ModuleConfig.SendChat)
                        Chat("“随机任务：指导者” 记录上传成功");
                }
                else
                    throw new Exception($"网站无对应职业数据 ({result?.Msg ?? "未知错误"})");
            }
            else
                throw new Exception($"传输记录至网站时异常 ({response.StatusCode})");
        }
        catch (Exception ex)
        {
            if (ModuleConfig.SendChat)
                NotificationError($"“随机任务：指导者” 记录上传失败: {ex.Message}");
        }
    }
    
    private static void EnsureHTTPClient()
    {
        if (HTTPClientInstance is not null)
            return;

        Cookies = new CookieContainer();

        var handler = new HttpClientHandler
        {
            CookieContainer                           = Cookies,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        HTTPClientInstance = HTTPClientHelper.Get(handler, "DungeonLoggerUploader-Client-Insecure");
    }

    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        DService.Instance().DutyState.DutyCompleted      -= OnDutyCompleted;

        HTTPClientInstance = null;
        Cookies            = null;

        IsLoggedIn = false;
    }

    #region Response

    private class DungeonLoggerResponse<T>
    {
        [JsonProperty("code")]
        public int Code { get; set; }

        [JsonProperty("data")]
        public T? Data { get; set; }

        [JsonProperty("msg")]
        public string? Msg { get; set; }
    }

    private class AuthData
    {
        [JsonProperty("token")]
        public string? Token { get; set; }

        [JsonProperty("username")]
        public string? Username { get; set; }
    }

    private class StatMaze
    {
        [JsonProperty("id")]
        public int ID { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("type")]
        public string Type { get; set; } = string.Empty;

        [JsonProperty("level")]
        public int Level { get; set; }
    }

    private class StatProf
    {
        [JsonProperty("key")]
        public string Key { get; set; } = string.Empty;

        [JsonProperty("nameCn")]
        public string NameCn { get; set; } = string.Empty;

        [JsonProperty("type")]
        public string Type { get; set; } = string.Empty;
    }

    #endregion

    private class Config : ModuleConfiguration
    {
        public string ServerURL = "https://dlog.luyulight.cn";
        public string Username  = string.Empty;
        public string Password  = string.Empty;

        public bool SendChat = true;
    }
}
