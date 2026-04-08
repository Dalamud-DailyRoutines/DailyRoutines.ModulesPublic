using System.Net;
using System.Text;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Newtonsoft.Json;
using OmenTools.Dalamud;
using OmenTools.OmenService;
using ContentsFinder = FFXIVClientStructs.FFXIV.Client.Game.UI.ContentsFinder;
using NotifyHelper = OmenTools.OmenService.NotifyHelper;

namespace DailyRoutines.ModulesPublic;

public class DungeonLoggerUploader : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "随机任务：指导者任务记录上传助手",
        Description = "配置完成后, 当进入“随机任务：指导者”任务副本并完成时, 自动记录并上传相关记录数据至 DungeonLogger 网站",
        Category    = ModuleCategory.Assist,
        Author      = ["Middo"]
    };

    public override ModulePermission Permission { get; } = new() { CNOnly = true };

    private HttpClient HTTPClient
    {
        get
        {
            if (field != null) return field;

            var handler = new HttpClientHandler
            {
                CookieContainer                           = new CookieContainer(),
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };

            return field = HTTPClientHelper.Instance().Get(handler, "DungeonLoggerUploader-Client-Insecure");
        }
    }

    private Config config;

    private bool   isLoggedIn;
    private string dungeonName = string.Empty;
    private string jobName     = string.Empty;
    private bool   inDungeon;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        DService.Instance().DutyState.DutyCompleted      += OnDutyCompleted;

        if (!string.IsNullOrEmpty(config.Username) && !string.IsNullOrEmpty(config.Password))
            Task.Run(() => LoginAsync());
    }

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(300f * GlobalUIScale);
        ImGui.InputText("服务器地址", ref config.ServerURL, 256);
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);

        ImGui.SetNextItemWidth(300f * GlobalUIScale);
        ImGui.InputText("用户名", ref config.Username, 128);

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.Save(this);
            isLoggedIn = false;
        }

        ImGui.SetNextItemWidth(300f * GlobalUIScale);
        ImGui.InputText("密码", ref config.Password, 128);

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.Save(this);
            isLoggedIn = false;
        }

        ImGui.Spacing();

        if (ImGui.Button("测试登录"))
            Task.Run(() => LoginAsync(true));

        ImGui.SameLine(0, 4f * GlobalUIScale);
        if (isLoggedIn)
            ImGui.TextColored(KnownColor.LawnGreen.ToVector4(), "已登录");
        else
            ImGui.TextColored(KnownColor.Red.ToVector4(), "未登录");

        ImGui.NewLine();

        using (ImRaii.Group())
        {
            if (ImGui.Checkbox("发送聊天信息", ref config.SendChat))
                config.Save(this);
        }
    }

    private void OnZoneChanged(ushort _)
    {
        if (!isLoggedIn) return;

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

        inDungeon   = true;
        dungeonName = GameState.ContentFinderConditionData.Name.ToString();
        jobName     = LocalPlayerState.ClassJobData.Name.ToString();

        if (config.SendChat)
            NotifyHelper.Instance().Chat("已进入 “随机任务：指导者” 任务, 完成后将自动上传记录至网站");
    }

    private void OnDutyCompleted(object? sender, ushort e)
    {
        if (!inDungeon) return;

        inDungeon = false;
        Task.Run(UploadDungeonRecordAsync);
    }

    private async Task LoginAsync(bool showNotification = false)
    {
        if (string.IsNullOrEmpty(config.Username) ||
            string.IsNullOrEmpty(config.Password))
            return;

        try
        {
            var loginData = new
            {
                username = config.Username,
                password = config.Password
            };

            var content  = new StringContent(JsonConvert.SerializeObject(loginData), Encoding.UTF8, "application/json");
            var response = await HTTPClient.PostAsync($"{config.ServerURL}/api/login", content);

            if (!response.IsSuccessStatusCode) return;

            var responseContent = await response.Content.ReadAsStringAsync();
            var result          = JsonConvert.DeserializeObject<DungeonLoggerResponse<AuthData>>(responseContent);

            if (result?.Code == 0)
            {
                isLoggedIn = true;
                if (showNotification)
                    NotifyHelper.Instance().NotificationSuccess("登录成功");
            }
            else
            {
                isLoggedIn = false;
                if (showNotification)
                    NotifyHelper.Instance().NotificationError($"登录失败: {result?.Msg}");
            }
        }
        catch (Exception ex)
        {
            isLoggedIn = false;
            NotifyHelper.Instance().NotificationError($"登录 DungeonLogger 异常: {ex.Message}");
            DLog.Error("登录 DungeonLogger 失败", ex);
        }
    }

    private async Task UploadDungeonRecordAsync()
    {
        if (string.IsNullOrEmpty(dungeonName))
            return;

        try
        {
            await LoginAsync();

            if (!isLoggedIn)
                throw new Exception("未登录或登录失败");

            var mazeResponse = await HTTPClient.GetAsync($"{config.ServerURL}/api/stat/maze");
            if (!mazeResponse.IsSuccessStatusCode)
                throw new Exception($"网站返回副本数据异常 ({mazeResponse.StatusCode})");

            var mazeContent = await mazeResponse.Content.ReadAsStringAsync();
            var mazeResult  = JsonConvert.DeserializeObject<DungeonLoggerResponse<List<StatMaze>>>(mazeContent);
            var maze        = mazeResult?.Data?.Find(m => m.Name.Equals(dungeonName));
            if (maze == null)
                throw new Exception($"网站无对应副本数据 ({dungeonName})");

            var profResponse = await HTTPClient.GetAsync($"{config.ServerURL}/api/stat/prof");
            if (!profResponse.IsSuccessStatusCode)
                throw new Exception($"网站返回职业数据异常 ({profResponse.StatusCode})");

            var profContent = await profResponse.Content.ReadAsStringAsync();
            var profResult  = JsonConvert.DeserializeObject<DungeonLoggerResponse<List<StatProf>>>(profContent);
            var prof        = profResult?.Data?.Find(p => p.NameCn.Equals(jobName));
            if (prof is null)
                throw new Exception($"网站无对应职业数据 ({jobName})");

            var uploadData = new
            {
                mazeId  = maze.ID,
                profKey = prof.Key
            };

            var content  = new StringContent(JsonConvert.SerializeObject(uploadData), Encoding.UTF8, "application/json");
            var response = await HTTPClient.PostAsync($"{config.ServerURL}/api/record", content);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                var result          = JsonConvert.DeserializeObject<DungeonLoggerResponse<object>>(responseContent);

                if (result?.Code == 0)
                {
                    if (config.SendChat)
                        NotifyHelper.Instance().Chat("“随机任务：指导者” 记录上传成功");
                }
                else
                    throw new Exception($"网站无对应职业数据 ({result?.Msg ?? "未知错误"})");
            }
            else
                throw new Exception($"传输记录至网站时异常 ({response.StatusCode})");
        }
        catch (Exception ex)
        {
            if (config.SendChat)
                NotifyHelper.Instance().NotificationError($"“随机任务：指导者” 记录上传失败: {ex.Message}");
        }
    }

    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        DService.Instance().DutyState.DutyCompleted      -= OnDutyCompleted;

        isLoggedIn = false;
    }

    private class Config : ModuleConfig
    {
        public string Password = string.Empty;

        public bool   SendChat  = true;
        public string ServerURL = "https://dlog.luyulight.cn";
        public string Username  = string.Empty;
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

    #region 常量

    private const byte MENTOR_ROULETTE_ID = 9;

    #endregion
}
