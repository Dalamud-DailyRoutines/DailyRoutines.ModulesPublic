using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using Dalamud.Utility;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using ContentsFinder = FFXIVClientStructs.FFXIV.Client.Game.UI.ContentsFinder;

namespace DailyRoutines.ModulesPublic;

public class DungeonLoggerUploader : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "指导者任务记录上传助手",
        Description = "自动记录并上传指导者任务（导随）的通关记录到 DungeonLogger 服务器",
        Category    = ModuleCategories.Assist,
        Author      = ["Middo"]
    };

    public override ModulePermission Permission { get; } = new() { CNOnly = true };

    private static Config            ModuleConfig;

    private const  byte              MentorRouletteID = 9;

    private static HttpClient?       HttpClientInstance;
    private static CookieContainer?  Cookies;
    private static bool              IsLoggedIn;

    private static string            DungeonName      = string.Empty;
    private static string            JobName          = string.Empty;
    private static bool              InDungeon;
    private static byte              QueuedRouletteID;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        Cookies            = new CookieContainer();
        HttpClientInstance = HttpClientHelper.Get(new HttpClientHandler { CookieContainer = Cookies }, "DungeonLoggerUploader-Client");

        DService.ClientState.TerritoryChanged += OnTerritoryChanged;
        DService.DutyState.DutyCompleted      += OnDutyCompleted;

        if (!string.IsNullOrEmpty(ModuleConfig.Username) && !string.IsNullOrEmpty(ModuleConfig.Password))
            Task.Run(() => LoginAsync(true));
    }

    protected override void ConfigUI()
    {
        using (ImRaii.Group())
        {
            ImGui.TextColored(KnownColor.GrayText.ToVector4(), "服务器设置");

            ImGui.SetNextItemWidth(300 * GlobalFontScale);
            ImGui.InputText("服务器地址", ref ModuleConfig.ServerUrl, 256);
            if (ImGui.IsItemDeactivatedAfterEdit())
                SaveConfig(ModuleConfig);
            ImGui.SameLine();
            if (ImGui.Button("进入网站"))
                Util.OpenLink(ModuleConfig.ServerUrl);
        }

        ImGui.Spacing();

        using (ImRaii.Group())
        {
            ImGui.TextColored(KnownColor.GrayText.ToVector4(), "账户设置");

            ImGui.SetNextItemWidth(200 * GlobalFontScale);
            ImGui.InputText("用户名", ref ModuleConfig.Username, 128);
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                SaveConfig(ModuleConfig);
                IsLoggedIn = false;
            }

            ImGui.SetNextItemWidth(200 * GlobalFontScale);
            ImGui.InputText("密码", ref ModuleConfig.Password, 128, ImGuiInputTextFlags.Password);
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                SaveConfig(ModuleConfig);
                IsLoggedIn = false;
            }

            if (ImGui.Button("测试登录"))
                Task.Run(() => LoginAsync(true));
            ImGui.SameLine();
            if (IsLoggedIn)
                ImGui.TextColored(KnownColor.Green.ToVector4(), "登录成功");
            else
                ImGui.TextColored(KnownColor.Red.ToVector4(), "未登录");
        }

        ImGui.NewLine();

        using (ImRaii.Group())
        {
            if (ImGui.Checkbox("自动上传", ref ModuleConfig.AutoUpload))
                SaveConfig(ModuleConfig);
            ImGui.SameLine();
            if (ImGui.Checkbox("弹出通知", ref ModuleConfig.ShowNotification))
                SaveConfig(ModuleConfig);
        }
    }

    private void OnTerritoryChanged(ushort territoryID)
    {
        if (!IsLoggedIn) return;

        if (!LuminaGetter.TryGetRow<TerritoryType>(territoryID, out var territory)) return;

        var contentFinderCondition = territory.ContentFinderCondition;
        if (contentFinderCondition.RowId == 0) return;

        unsafe
        {
            var contentsFinder = ContentsFinder.Instance();
            if (contentsFinder is not null)
            {
                var queueInfo = contentsFinder->GetQueueInfo();
                if (queueInfo is not null)
                    QueuedRouletteID = queueInfo->QueuedContentRouletteId;
            }
        }

        if (QueuedRouletteID != MentorRouletteID) return; // 只记录指导者任务（导随）

        InDungeon   = true;
        DungeonName = contentFinderCondition.Value.Name.ExtractText();
        JobName     = LocalPlayerState.ClassJobData.Name.ExtractText();

        if (ModuleConfig.ShowNotification)
            NotificationInfo("进入指导者任务副本，完成后将进行记录。");
    }

    private void OnDutyCompleted(object? sender, ushort e)
    {
        if (!InDungeon) return;

        InDungeon = false;
        if (ModuleConfig.AutoUpload)
            Task.Run(UploadDungeonRecordAsync);
    }

    private static async Task LoginAsync(bool showNotification = false)
    {
        if (HttpClientInstance is null || string.IsNullOrEmpty(ModuleConfig.Username) || string.IsNullOrEmpty(ModuleConfig.Password))
            return;

        try
        {
            var loginData = new
            {
                username = ModuleConfig.Username,
                password = ModuleConfig.Password
            };

            var content  = new StringContent(JsonConvert.SerializeObject(loginData), Encoding.UTF8, "application/json");
            var response = await HttpClientInstance.PostAsync($"{ModuleConfig.ServerUrl}/api/login", content);

            if (!response.IsSuccessStatusCode) return;

            var responseContent = await response.Content.ReadAsStringAsync();
            var result          = JsonConvert.DeserializeObject<DungeonLoggerResponse<AuthData>>(responseContent);

            if (result?.Code == 0)
            {
                IsLoggedIn = true;
                if (showNotification && ModuleConfig.ShowNotification)
                    NotificationSuccess("DungeonLogger 登录成功");
            }
            else
            {
                IsLoggedIn = false;
                if (showNotification && ModuleConfig.ShowNotification)
                    NotificationError($"DungeonLogger 登录失败: {result?.Msg}");
            }
        }
        catch (Exception ex)
        {
            IsLoggedIn = false;
            if (showNotification && ModuleConfig.ShowNotification)
                NotificationError($"DungeonLogger 登录异常: {ex.Message}");
        }
    }

    private static async Task UploadDungeonRecordAsync()
    {
        if (HttpClientInstance is null || string.IsNullOrEmpty(DungeonName)) return;

        try
        {
            await LoginAsync();

            if (!IsLoggedIn)
            {
                if (ModuleConfig.ShowNotification)
                    NotificationError("副本记录上传失败: 未登录或登录失败");
                return;
            }

            var mazeResponse = await HttpClientInstance.GetAsync($"{ModuleConfig.ServerUrl}/api/stat/maze"); // 获取副本列表，通过名称匹配找到 mazeId
            if (!mazeResponse.IsSuccessStatusCode)
            {
                if (ModuleConfig.ShowNotification)
                    NotificationError($"副本记录上传失败: HTTP {mazeResponse.StatusCode}");
                return;
            }

            var mazeContent = await mazeResponse.Content.ReadAsStringAsync();
            var mazeResult  = JsonConvert.DeserializeObject<DungeonLoggerResponse<List<StatMaze>>>(mazeContent);
            var maze        = mazeResult?.Data?.Find(m => m.Name.Equals(DungeonName));

            if (maze is null)
            {
                if (ModuleConfig.ShowNotification)
                    NotificationError($"副本记录上传失败: 未找到副本: {DungeonName}");
                return;
            }

            var profResponse = await HttpClientInstance.GetAsync($"{ModuleConfig.ServerUrl}/api/stat/prof"); // 获取职业列表，通过名称匹配找到 profKey
            if (!profResponse.IsSuccessStatusCode)
            {
                if (ModuleConfig.ShowNotification)
                    NotificationError($"副本记录上传失败: HTTP {profResponse.StatusCode}");
                return;
            }

            var profContent = await profResponse.Content.ReadAsStringAsync();
            var profResult  = JsonConvert.DeserializeObject<DungeonLoggerResponse<List<StatProf>>>(profContent);
            var prof        = profResult?.Data?.Find(p => p.NameCn.Equals(JobName));

            if (prof is null)
            {
                if (ModuleConfig.ShowNotification)
                    NotificationError($"副本记录上传失败: 未找到职业: {JobName}");
                return;
            }

            var uploadData = new // 上传记录
            {
                mazeId  = maze.ID,
                profKey = prof.Key
            };

            var content  = new StringContent(JsonConvert.SerializeObject(uploadData), Encoding.UTF8, "application/json");
            var response = await HttpClientInstance.PostAsync($"{ModuleConfig.ServerUrl}/api/record", content);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                var result          = JsonConvert.DeserializeObject<DungeonLoggerResponse<object>>(responseContent);

                if (result?.Code == 0)
                {
                    if (ModuleConfig.ShowNotification)
                        NotificationSuccess("副本记录上传成功");
                }
                else
                {
                    if (ModuleConfig.ShowNotification)
                        NotificationError($"副本记录上传失败: {result?.Msg ?? "Unknown error"}");
                }
            }
            else
            {
                if (ModuleConfig.ShowNotification)
                    NotificationError($"副本记录上传失败: HTTP {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            if (ModuleConfig.ShowNotification)
                NotificationError($"副本记录上传失败: {ex.Message}");
        }
    }

    protected override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnTerritoryChanged;
        DService.DutyState.DutyCompleted      -= OnDutyCompleted;

        HttpClientInstance = null;
        Cookies            = null;
        IsLoggedIn         = false;
    }

    #region DungeonLogger网站Response数据结构

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
        public string ServerUrl        = "https://dlog.luyulight.cn";
        public string Username         = "";
        public string Password         = "";
        public bool   AutoUpload       = true;
        public bool   ShowNotification = true;
    }
}
