using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;

namespace DailyRoutines.ModulesPublic;

public class AutoNotifyChaoticRaidBonus : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoNotifyChaoticRaidBonusTitle"),
        Description = GetLoc("AutoNotifyChaoticRaidBonusDescription"),
        Category    = ModuleCategories.Notice
    };

    private static readonly List<string> AllDataCenters =
    [
        "陆行鸟", "莫古力", "猫小胖", "豆豆柴",
        "Elemental", "Gaia", "Mana", "Meteor",
        "Aether", "Crystal", "Dynamis", "Primal",
        "Light", "Chaos", "Materia"
    ];

    private const string BaseUrl = "https://api.ff14.xin/status?data_center={0}";

    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        AllDataCenters.ForEach(x => ModuleConfig.DataCenters.TryAdd(x, false));
        AllDataCenters.ForEach(x => ModuleConfig.DataCentersNotifyTime.TryAdd(x, 0));
        SaveConfig(ModuleConfig);
        
        FrameworkManager.Reg(OnUpdate, throttleMS: 60_000);
        
        RunCheck(true);
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("SendNotification"), ref ModuleConfig.SendNotification))
            SaveConfig(ModuleConfig);
        
        if (ImGui.Checkbox(GetLoc("SendChat"), ref ModuleConfig.SendChat))
            SaveConfig(ModuleConfig);
        
        if (ImGui.Checkbox(GetLoc("SendTTS"), ref ModuleConfig.SendTTS))
            SaveConfig(ModuleConfig);

        ImGui.NewLine();
        
        using var table = ImRaii.Table("Table", 3, ImGuiTableFlags.None, (ImGui.GetContentRegionAvail() / 1.5f) with { Y = 0 });
        if (!table) return;
        
        ImGui.TableSetupColumn(LuminaWrapper.GetLobbyText(802));
        ImGui.TableSetupColumn(GetLoc("Enable"));
        ImGui.TableSetupColumn(GetLoc("AutoNotifyChaoticRaidBonus-LastBonusNotifyTime"));
        
        ImGui.TableHeadersRow();

        foreach (var (name, isEnabled) in ModuleConfig.DataCenters)
        {
            if (!ModuleConfig.DataCentersNotifyTime.TryGetValue(name, out var timeUnix)) continue;

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text($"{name}");
            
            var enabled = isEnabled;
            ImGui.TableNextColumn();
            if (ImGui.Checkbox($"###{name}IsEnabled", ref enabled))
            {
                ModuleConfig.DataCenters[name] = enabled;
                ModuleConfig.Save(this);
            }
            
            ImGui.TableNextColumn();
            ImGui.Text($"{DateTimeOffset.FromUnixTimeSeconds(timeUnix).LocalDateTime}");
        }
    }

    private static void OnUpdate(IFramework framework)
    {
        var currentMinute = DateTime.Now.Minute;
        if (currentMinute is > 5 and < 55) return;

        RunCheck();
    }

    private static void RunCheck(bool isIgnoreTime = false)
    {
        Task.Run(async () => await Task.WhenAll(AllDataCenters.Select(Get)));
        
        return;

        async Task Get(string dcName)
        {
            if (!ModuleConfig.DataCenters.TryGetValue(dcName, out var isEnabled) || 
                (!isEnabled && GameState.CurrentDataCenterData.Name.ExtractText() != dcName)) 
                return;
            // 小于 3 小时
            if (!ModuleConfig.DataCentersNotifyTime.TryGetValue(dcName, out var lastTime) || 
                (!isIgnoreTime && GameState.ServerTimeUnix - lastTime < 10800)) 
                return;

            // 不在副本内且当前就在目标大区
            if (DService.ClientState.IsLoggedIn && !GameState.IsInInstanceArea && 
                GameState.CurrentDataCenterData.Name.ExtractText() == dcName)
            {
                var isBonusNow = GameState.IsChaoticRaidBonusActive;
                if (isBonusNow)
                {
                    Notify(dcName);
                    ModuleConfig.DataCentersNotifyTime[dcName] = GameState.ServerTimeUnix;
                }
            }
            else
            {
                try
                {
                    var result  = await HttpClientHelper.Get().GetStringAsync(string.Format(BaseUrl, dcName));
                    var content = JsonConvert.DeserializeObject<ChaoticUptimeData>(result);
                    if (content.IsUptime)
                    {
                        Notify(dcName);
                        ModuleConfig.DataCentersNotifyTime[dcName] = GameState.ServerTimeUnix;
                    }
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

    private static void Notify(string dcName)
    {
        var text = GetLoc("AutoNotifyChaoticRaidBonus-Notification", dcName);
        
        if (ModuleConfig.SendNotification) 
            NotificationInfo(text);
        if (ModuleConfig.SendChat) 
            Chat(text);
        if (ModuleConfig.SendTTS) 
            Speak(text);
    }

    protected override void Uninit() => FrameworkManager.Unreg(OnUpdate);

    private class Config : ModuleConfiguration
    {
        public Dictionary<string, bool> DataCenters           = [];
        public Dictionary<string, long> DataCentersNotifyTime = [];

        public bool SendNotification = true;
        public bool SendChat = true;
        public bool SendTTS = true;
    }

    private class ChaoticUptimeData
    {
        [JsonProperty("data_center")]
        public string DataCenter { get; set; }

        [JsonProperty("is_uptime")]
        public bool IsUptime { get; set; }

        [JsonProperty("last_bonus_starts")]
        public List<DateTime> LastBonusStartTimes { get; set; }

        [JsonProperty("last_bonus_ends")]
        public List<DateTime> LastBonusEndTimes { get; set; }
    }
}
