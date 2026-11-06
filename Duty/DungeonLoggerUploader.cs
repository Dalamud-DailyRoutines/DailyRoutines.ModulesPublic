using System.Net;
using System.Text;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.DutyState;
using Newtonsoft.Json;
using OmenTools.Dalamud;
using ContentsFinder = FFXIVClientStructs.FFXIV.Client.Game.UI.ContentsFinder;
using NotifyHelper = OmenTools.OmenService.NotifyHelper;

namespace DailyRoutines.ModulesPublic;

public class DungeonLoggerUploader : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "指导者任务记录上传助手",
        Description = "配置后，完成指导者任务时，自动记录并上传至“DungeonLogger”网站。",
        Category    = ModuleCategory.Duty,
        Author      = ["Middo"]
    };
    
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

            return field = HTTPClientHelper.Instance().Get(handler, "DungeonLoggerUploader.Client.Insecure");
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

        IClientState.Instance().TerritoryChanged += OnZoneChanged;
        IDutyState.Instance().DutyCompleted      += OnDutyCompleted;

        if (!string.IsNullOrEmpty(config.Username) &&
            !string.IsNullOrEmpty(config.Password))
            Task.Run(LoginAsync);
    }

    protected override void ConfigUI()
    {
        using var itemWidth = ImRaii.ItemWidth(300f * GlobalUIScale);

        var changed = false;
        
        ImGui.InputText("服务器地址", ref config.ServerURL, 256);
        if (ImGui.IsItemDeactivatedAfterEdit())
            changed = true;

        ImGui.InputText("用户名", ref config.Username, 128);
        if (ImGui.IsItemDeactivatedAfterEdit())
            changed = true;

        ImGui.InputText("密码", ref config.Password, 128);
        if (ImGui.IsItemDeactivatedAfterEdit())
            changed = true;

        ImGui.Spacing();

        if (ImGui.Button("测试登录"))
        {
            isLoggedIn = false;
            Task.Run(LoginAsync);
        }

        ImGui.SameLine(0, 4f * GlobalUIScale);
        if (isLoggedIn)
            ImGui.TextColored(KnownColor.LawnGreen.ToVector4(), "已登录");
        else
            ImGui.TextColored(KnownColor.Red.ToVector4(), "未登录");
        
        if (changed)
        {
            config.Save(this);
            isLoggedIn = false;
        }
    }

    private void OnZoneChanged
    (
        uint zone
    )
    {
        if (!isLoggedIn) return;

        if (GameState.TerritoryType          == 0 ||
            GameState.ContentFinderCondition == 0)
            return;

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

        NotifyHelper.Instance().Chat("开始了指导者任务，完成后将自动上传记录至“DungeonLogger”网站。");
    }

    private void OnDutyCompleted
    (
        IDutyStateEventArgs args
    )
    {
        if (!inDungeon) return;

        inDungeon = false;
        jobName   = LocalPlayerState.ClassJobData.Name.ToString();
        Task.Run(UploadDungeonRecordAsync);
    }

    private async Task LoginAsync()
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

            if (!response.IsSuccessStatusCode)
                throw new Exception($"登录时异常，错误码{response.StatusCode}。");

            var responseContent = await response.Content.ReadAsStringAsync();
            var result          = JsonConvert.DeserializeObject<DungeonLoggerResponse<AuthData>>(responseContent);

            if (result?.Code == 0)
            {
                isLoggedIn = true;
            }
            else
            {
                isLoggedIn = false;
                throw new Exception($"登录时异常，错误码{result?.Code}。");
            }
        }
        catch (Exception ex)
        {
            isLoggedIn = false;
            NotifyHelper.Instance().ChatError("无法登录至“DungeonLogger”网站。");
            DLog.Error("登录 DungeonLogger 失败。", ex);
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
                throw new Exception("未登录或登录失败。");

            var mazeResponse = await HTTPClient.GetAsync($"{config.ServerURL}/api/stat/maze");
            if (!mazeResponse.IsSuccessStatusCode)
                throw new Exception("网站返回副本数据异常。");

            var mazeContent = await mazeResponse.Content.ReadAsStringAsync();
            var mazeResult  = JsonConvert.DeserializeObject<DungeonLoggerResponse<List<StatMaze>>>(mazeContent);
            var maze        = mazeResult?.Data?.Find(m => m.Name.Equals(dungeonName));
            if (maze == null)
                throw new Exception($"网站无任务“{dungeonName}”的数据。");

            var profResponse = await HTTPClient.GetAsync($"{config.ServerURL}/api/stat/prof");
            if (!profResponse.IsSuccessStatusCode)
                throw new Exception("网站返回职业数据异常。");

            var profContent = await profResponse.Content.ReadAsStringAsync();
            var profResult  = JsonConvert.DeserializeObject<DungeonLoggerResponse<List<StatProf>>>(profContent);
            var prof        = profResult?.Data?.Find(p => p.NameCn.Equals(jobName));
            if (prof is null)
                throw new Exception($"网站无职业“{jobName}”的数据。");

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
                    NotifyHelper.Instance().Chat("成功上传了本次指导者任务的记录至“DungeonLogger”网站。");
                else
                    throw new Exception(result?.Msg ?? "未知错误");
            }
            else
                throw new Exception($"上传记录时异常，错误码{response.StatusCode}。");
        }
        catch (Exception ex)
        {
            NotifyHelper.Instance().ChatError("上传本次指导者任务的记录至“DungeonLogger”网站失败。");
            DLog.Error(ex.Message, ex);
        }
    }

    protected override void Uninit()
    {
        IClientState.Instance().TerritoryChanged -= OnZoneChanged;
        IDutyState.Instance().DutyCompleted      -= OnDutyCompleted;

        isLoggedIn = false;
    }

    private class Config : ModuleConfig
    {
        public string Password = string.Empty;
        public string Username = string.Empty;

        public string ServerURL = "https://dlog.luyulight.cn";
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
