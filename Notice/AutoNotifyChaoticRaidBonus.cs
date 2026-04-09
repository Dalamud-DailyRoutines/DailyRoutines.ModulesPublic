using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Newtonsoft.Json;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using NotifyHelper = OmenTools.OmenService.NotifyHelper;

namespace DailyRoutines.ModulesPublic;

public class AutoNotifyChaoticRaidBonus : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoNotifyChaoticRaidBonusTitle"),
        Description = Lang.Get("AutoNotifyChaoticRaidBonusDescription"),
        Category    = ModuleCategory.Notice
    };
    
    private Config config = null!;

    private readonly CancellationTokenSource cancelSource = new();

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        var state = false;

        foreach (var x in AllDataCenters)
        {
            if (config.DataCenters.TryAdd(x, false))
                state = true;
        }

        foreach (var x in AllDataCenters)
        {
            if (config.DataCentersNotifyTime.TryAdd(x, 0))
                state = true;
        }

        if (state)
            config.Save(this);

        Task.Run(() => CheckLoop(cancelSource.Token));
    }

    protected override void Uninit()
    {
        cancelSource.Cancel();
        cancelSource.Dispose();
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("SendNotification"), ref config.SendNotification))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("SendChat"), ref config.SendChat))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("SendTTS"), ref config.SendTTS))
            config.Save(this);

        ImGui.NewLine();

        using var table = ImRaii.Table("Table", 3, ImGuiTableFlags.None, (ImGui.GetContentRegionAvail() / 1.5f) with { Y = 0 });
        if (!table) return;

        ImGui.TableSetupColumn(LuminaWrapper.GetLobbyText(802));
        ImGui.TableSetupColumn(Lang.Get("Enable"));
        ImGui.TableSetupColumn(Lang.Get("AutoNotifyChaoticRaidBonus-LastBonusNotifyTime"));

        ImGui.TableHeadersRow();

        foreach (var (name, isEnabled) in config.DataCenters)
        {
            if (!config.DataCentersNotifyTime.TryGetValue(name, out var timeUnix)) continue;

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{name}");

            var enabled = isEnabled;
            ImGui.TableNextColumn();

            if (ImGui.Checkbox($"###{name}IsEnabled", ref enabled))
            {
                config.DataCenters[name] = enabled;
                config.Save(this);
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{DateTimeOffset.FromUnixTimeSeconds(timeUnix).LocalDateTime}");
        }
    }

    private async Task CheckLoop(CancellationToken ct)
    {
        await Task.Delay(5000, ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now    = DateTime.Now;
                var minute = now.Minute;

                if (minute is > 5 and < 55)
                {
                    var delayMinutes = 55 - minute;
                    await Task.Delay(TimeSpan.FromMinutes(delayMinutes), ct);
                    continue;
                }

                var snapshot = await GetStateSnapshot();
                if (snapshot != null) await RunCheckAsync(snapshot);

                await Task.Delay(60_000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // ignored
            }
        }
    }

    private static Task<StateSnapshot?> GetStateSnapshot()
    {
        var tcs = new TaskCompletionSource<StateSnapshot?>();

        DService.Instance().Framework.RunOnTick
        (() =>
            {
                try
                {
                    if (!GameState.IsLoggedIn)
                    {
                        tcs.SetResult(null);
                        return;
                    }

                    tcs.SetResult
                    (
                        new StateSnapshot
                        (
                            GameState.CurrentDataCenterData.Name.ToString(),
                            true,
                            GameState.IsInInstanceArea,
                            GameState.IsChaoticRaidBonusActive,
                            GameState.ServerTimeUnix
                        )
                    );
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }
        );

        return tcs.Task;
    }

    private async Task RunCheckAsync(StateSnapshot state)
    {
        var results = await Task.WhenAll(AllDataCenters.Select(dcName => CheckDC(dcName, state)));

        foreach (var result in results)
        {
            if (result != null)
                config.DataCentersNotifyTime[result.Value.DC] = result.Value.Time;
        }
    }

    private async Task<(string DC, long Time)?> CheckDC(string dcName, StateSnapshot state)
    {
        if (!config.DataCenters.TryGetValue(dcName, out var isEnabled) ||
            !isEnabled && state.CurrentDC != dcName)
            return null;

        var lastTime = config.DataCentersNotifyTime.GetValueOrDefault(dcName, 0);
        if (state.ServerTime - lastTime < 10800) return null;

        if (state is { IsLoggedIn: true, IsInInstance: false } && state.CurrentDC == dcName)
        {
            if (state.IsBonusActive)
            {
                Notify(dcName);
                return (dcName, state.ServerTime);
            }
        }
        else
        {
            try
            {
                var result  = await HTTPClientHelper.Instance().Get().GetStringAsync(string.Format(BASE_URL, dcName));
                var content = JsonConvert.DeserializeObject<ChaoticUptimeData>(result);

                if (content is { IsUptime: true })
                {
                    Notify(dcName);
                    return (dcName, state.ServerTime);
                }
            }
            catch
            {
                /* ignored */
            }
        }

        return null;
    }

    private void Notify(string dcName)
    {
        DService.Instance().Framework.RunOnTick
        (() =>
            {
                var text = Lang.Get("AutoNotifyChaoticRaidBonus-Notification", dcName);

                if (config.SendNotification)
                    NotifyHelper.Instance().NotificationInfo(text);
                if (config.SendChat)
                    NotifyHelper.Instance().Chat(text);
                if (config.SendTTS)
                    NotifyHelper.Speak(text);
            }
        );
    }

    private class Config : ModuleConfig
    {
        public Dictionary<string, bool> DataCenters           = [];
        public Dictionary<string, long> DataCentersNotifyTime = [];
        public bool                     SendChat              = true;

        public bool SendNotification = true;
        public bool SendTTS          = true;
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

    private record StateSnapshot
    (
        string CurrentDC,
        bool   IsLoggedIn,
        bool   IsInInstance,
        bool   IsBonusActive,
        long   ServerTime
    );
    
    #region 常量

    private const string BASE_URL = "https://api.ff14.xin/status?data_center={0}";

    private static readonly FrozenSet<string> AllDataCenters =
    [
        "陆行鸟", "莫古力", "猫小胖", "豆豆柴",
        "Elemental", "Gaia", "Mana", "Meteor",
        "Aether", "Crystal", "Dynamis", "Primal",
        "Light", "Chaos", "Materia"
    ];

    #endregion
}
