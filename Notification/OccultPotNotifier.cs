using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net.Http;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Newtonsoft.Json;
using OmenTools;
using OmenTools.Extensions;
using OmenTools.OmenService;
using static OmenTools.Global.Globals;

namespace DailyRoutines.ModulesPublic;

// 新月岛（蜃景幻界·新月岛 / The Occult Crescent）「幸福的魔法罐」宝兔 FATE 刷新提醒
// 原理参考: https://github.com/Infiziert90/EurekaTrackerAutoPopper (PotDtrBar.cs / Fates.cs)
public class OccultPotNotifier : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "新月岛 魔法罐刷新提醒",
        Description = "监控新月岛「幸福的魔法罐」宝兔 FATE（北 / 南）的刷新倒计时，可用悬浮窗或服务器信息栏显示倒计时与即将刷新的方位，并可在刷新前发送 TTS / 通知，或同步转发到小队频道（附带可点击的 <flag> 坐标）。",
        Category    = ModuleCategory.Notification,
        Author      = ["黑川启太"]
    };

    // 魔法罐 spawn-to-spawn 周期: 30 分钟
    private const long Respawn = 1800;

    private Config        config = null!;
    private IDtrBarEntry? entry;

    // 当前展示 / 点击定位的目标 + 展示文本（信息栏与悬浮窗共用）
    private Pot?   displayPot;
    private string displayText       = string.Empty;
    private long   notifiedSpawnTime = -1;

    // 北 = FateId 1976（结晶洞窟附近）, 南 = FateId 1977（石沼附近）
    private readonly Pot[] pots =
    [
        new() { FateID = 1976, World = new(204.66835f,  111.81729f, -204.96242f), DirName = "北" },
        new() { FateID = 1977, World = new(-479.8395f,  75f,         524.78894f), DirName = "南" }
    ];

    // ── 在线追踪器 (infi.ovh, 即 tracker.xivstats.com 的数据源; 仅只读拉取) ──
    private const string TrackerBaseURL     = "https://infi.ovh/api/";
    private const string TrackerTable       = "OccultTrackerV3";
    private const string TrackerAnonKey     = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJyb2xlIjoiYW5vbiJ9.Ur6wgi_rD4dr3uLLvbLoaEvfLCu4QFWdrF-uHRtbl_s";
    private const string TrackerVersion     = "DR-OccultPotNotifier";
    private const int    SyncRefreshSeconds = 60; // 已连上后的刷新间隔
    private const int    FastRetrySeconds   = 5;  // 尚未取到数据时的快速重试间隔

    // 新月岛常规 FATE (用于为所在实例生成指纹), 来源: EurekaTrackerAutoPopper Fates.cs
    private static readonly HashSet<uint> OccultFateIds =
        [1962, 1963, 1964, 1965, 1966, 1967, 1968, 1969, 1970, 1971, 1972];

    private static readonly HttpClient Client = CreateClient();

    private          string lastFingerprint    = string.Empty;
    private          string createdFingerprint = string.Empty;
    private          long   lastSyncAt;
    private volatile bool   syncInFlight;
    private volatile bool   hasOnlineData;
    private readonly object syncLock = new();
    private (long NorthSpawn, long NorthSeen, long SouthSpawn, long SouthSeen)? pendingSync;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        Overlay        = new(this);
        Overlay.IsOpen = false;

        entry         ??= DService.Instance().DTRBar.Get("DailyRoutines-OccultPotNotifier");
        entry.Shown   =   false;
        entry.Tooltip =   "新月岛 魔法罐刷新提醒\n点击在地图上标记下一个魔法罐位置 (<flag>)";
        entry.OnClick =   OnDtrClick;

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
    }

    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        FrameworkManager.Instance().Unreg(OnUpdate);

        if (entry != null)
        {
            entry.Remove();
            entry = null;
        }
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), "倒计时显示方式");
        using (ImRaii.PushIndent())
        {
            if (ImGui.RadioButton("服务器信息栏", config.DisplayMode == PotDisplayMode.DtrBar))
            {
                config.DisplayMode = PotDisplayMode.DtrBar;
                config.Save(this);
            }

            ImGui.SameLine();
            if (ImGui.RadioButton("悬浮窗", config.DisplayMode == PotDisplayMode.Overlay))
            {
                config.DisplayMode = PotDisplayMode.Overlay;
                config.Save(this);
            }

            ImGui.SameLine();
            if (ImGui.RadioButton("不显示", config.DisplayMode == PotDisplayMode.None))
            {
                config.DisplayMode = PotDisplayMode.None;
                config.Save(this);
            }
        }

        ImGui.NewLine();

        if (ImGui.Checkbox("从在线追踪器同步并上报数据 (无需亲眼看到魔法罐)", ref config.UseOnlineTracker))
            config.Save(this);

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), "提醒方式");
        using (ImRaii.PushIndent())
        {
            if (ImGui.Checkbox("语音播报 (TTS)", ref config.SendTTS))
                config.Save(this);

            if (ImGui.Checkbox("游戏内通知", ref config.SendNotification))
                config.Save(this);

            if (ImGui.Checkbox("转发到聊天频道 (附带 <flag> 坐标)", ref config.SendChat))
                config.Save(this);

            if (config.SendChat)
            {
                using (ImRaii.PushIndent())
                {
                    if (ImGui.RadioButton("小队 (/p)", config.ChatChannel == PotChatChannel.Party))
                    {
                        config.ChatChannel = PotChatChannel.Party;
                        config.Save(this);
                    }

                    ImGui.SameLine();
                    if (ImGui.RadioButton("默语 (/e)", config.ChatChannel == PotChatChannel.Emote))
                    {
                        config.ChatChannel = PotChatChannel.Emote;
                        config.Save(this);
                    }
                }
            }
        }

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), "提前提醒时间");
        using (ImRaii.PushIndent())
        {
            var minutes = Math.Clamp(config.LeadSeconds / 60, 1, 15);
            ImGui.SetNextItemWidth(150f * GlobalUIScale);
            if (ImGui.SliderInt("分钟###LeadMinutes", ref minutes, 1, 15))
                config.LeadSeconds = minutes * 60;
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);
        }
    }

    // 悬浮窗内容 (点击在地图上标记魔法罐位置)
    protected override void OverlayUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), "新月岛 魔法罐");
        ImGui.Separator();

        var text = string.IsNullOrEmpty(displayText) ? "等待刷新数据…" : displayText;
        if (ImGui.Selectable(text) && displayPot != null)
            OpenPotMap(displayPot);

        if (displayPot != null && ImGui.IsItemHovered())
            ImGui.SetTooltip("点击在地图上标记魔法罐位置 (<flag>)");
    }

    private void OnZoneChanged(uint zone)
    {
        FrameworkManager.Instance().Unreg(OnUpdate);

        foreach (var pot in pots)
            pot.Reset();
        displayPot        = null;
        displayText       = string.Empty;
        notifiedSpawnTime  = -1;
        lastFingerprint    = string.Empty;
        createdFingerprint = string.Empty;
        lastSyncAt         = 0;
        hasOnlineData      = false;
        lock (syncLock)
            pendingSync = null;
        HideDisplay();

        if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent) return;

        FrameworkManager.Instance().Reg(OnUpdate, 1_000);
    }

    private void OnUpdate(IFramework _)
    {
        if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent)
        {
            FrameworkManager.Instance().Unreg(OnUpdate);
            HideDisplay();
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 1. 扫描 FATE 表, 命中的魔法罐记录其真实刷新时间 (StartTimeEpoch)
        foreach (var fate in DService.Instance().Fate)
        {
            var pot = GetPot(fate.FateId);
            if (pot == null) continue;

            pot.LastSeenAlive   = now;
            pot.SpawnTime       = fate.StartTimeEpoch;
            pot.LocallyObserved = true;

            if (!pot.Alive)
                pot.Alive = true;
        }

        // 2. 本帧未再观测到 -> 视为已结束 (记录结束时间用于上报)
        foreach (var pot in pots)
        {
            if (pot.Alive && pot.LastSeenAlive != now)
            {
                pot.Alive     = false;
                pot.DeathTime = pot.LastSeenAlive;
            }
        }

        // 3. 在线追踪器同步 (按大区 + 当前常规 FATE 指纹拉取同实例数据)
        if (config.UseOnlineTracker)
            TrySyncOnline(now);
        ApplyPendingSync();

        // 4. 预测下一个魔法罐 + 按需提醒, 并刷新显示
        UpdatePrediction(now);
        ApplyDisplay();
    }

    private void Notify(Pot pot, int minutes)
    {
        // TTS / 通知不含 <flag> (无法展开为坐标)
        var message = $"魔法罐约{minutes}分钟后在{pot.DirName}处刷新";

        if (config.SendNotification)
            NotifyHelper.Instance().NotificationInfo(message);

        if (config.SendTTS)
            NotifyHelperExtension.Speak(message);

        if (config.SendChat)
        {
            SetPotFlag(pot);
            var channelCommand = config.ChatChannel == PotChatChannel.Emote ? "/e" : "/p";
            // 聊天消息把 <flag> 嵌在方位后, 游戏会展开为可点击坐标
            ChatManager.Instance().SendMessage($"{channelCommand} 魔法罐约{minutes}分钟后在{pot.DirName}<flag>处刷新");
        }
    }

    // 预测下一个魔法罐 (信息栏 / 悬浮窗共用), 并按需触发提醒。
    // 北 / 南两罐交替刷新, 间隔 30 分钟; 由"最近一次有效刷新 (本地观测或在线同步) + k*30 分钟"向后推算,
    // 因此本地看到任意一罐、或从在线追踪器同步到任意一罐数据, 即可推算下一个 (方位为另一侧)。
    private void UpdatePrediction(long now)
    {
        var north = pots[0];
        var south = pots[1];

        // 本地正在观测中 -> 进行中
        var alive = north.Alive ? north : south.Alive ? south : null;
        if (alive != null)
        {
            displayPot        = alive;
            displayText       = $"魔法罐: 进行中 ({alive.DirName})";
            notifiedSpawnTime = -1; // 进行中时重置, 使下一轮可再次提醒
            return;
        }

        // 取最近一次有效刷新的罐子
        Pot? lastSpawned = null;
        if (north.SpawnTime > 0)
            lastSpawned = north;
        if (south.SpawnTime > 0 && (lastSpawned == null || south.SpawnTime > lastSpawned.SpawnTime))
            lastSpawned = south;

        if (lastSpawned == null)
        {
            displayPot  = null;
            displayText = "魔法罐: 等待刷新";
            return;
        }

        // 交替推算: 第 k 次刷新 = 最近刷新 + k*30min, 方位逐次在北/南间交替
        var other    = ReferenceEquals(lastSpawned, north) ? south : north;
        var s        = lastSpawned.SpawnTime;
        var k        = now < s ? 0 : ((now - s) / Respawn) + 1;
        var nextTime = s + (k * Respawn);
        var nextPot  = k % 2 == 0 ? lastSpawned : other;

        ShowCountdown(now, nextTime, nextPot);
    }

    // 统一的倒计时显示 + 提醒 (信息栏 / 悬浮窗共用)
    private void ShowCountdown(long now, long nextTime, Pot pot)
    {
        displayPot = pot;

        var remaining = nextTime - now;
        if (remaining <= 0)
        {
            displayText = $"魔法罐: 即将刷新 ({pot.DirName})";
            return;
        }

        var span = TimeSpan.FromSeconds(remaining);
        displayText = $"下个魔法罐 {span:mm\\:ss} ({pot.DirName})";

        if (notifiedSpawnTime != nextTime && remaining <= config.LeadSeconds)
        {
            Notify(pot, (int)Math.Ceiling(remaining / 60.0));
            notifiedSpawnTime = nextTime;
        }
    }

    // 根据显示方式应用到信息栏 / 悬浮窗
    private void ApplyDisplay()
    {
        if (entry != null)
        {
            if (config.DisplayMode == PotDisplayMode.DtrBar)
            {
                entry.Text  = displayText;
                entry.Shown = true;
            }
            else
                entry.Shown = false;
        }

        if (Overlay != null)
            Overlay.IsOpen = config.DisplayMode == PotDisplayMode.Overlay;
    }

    private void HideDisplay()
    {
        if (entry != null)
            entry.Shown = false;
        if (Overlay != null)
            Overlay.IsOpen = false;
    }

    private void OnDtrClick(DtrInteractionEvent _)
    {
        if (displayPot == null) return;
        OpenPotMap(displayPot);
    }

    private unsafe void OpenPotMap(Pot pot)
    {
        var agent = AgentMap.Instance();
        if (agent == null) return;

        var mapID = agent->CurrentMapId;
        agent->SelectedMapId = mapID;
        if (!agent->IsAgentActive())
            agent->Show();

        agent->SetFlagMapMarker(GameState.TerritoryType, mapID, pot.World);
        agent->OpenMap(mapID, GameState.TerritoryType, "魔法罐");
    }

    private unsafe void SetPotFlag(Pot pot)
    {
        var agent = AgentMap.Instance();
        if (agent == null) return;

        agent->SetFlagMapMarker(GameState.TerritoryType, agent->CurrentMapId, pot.World);
    }

    private Pot? GetPot(ushort fateID)
    {
        foreach (var pot in pots)
        {
            if (pot.FateID == fateID)
                return pot;
        }

        return null;
    }

    // ── 在线追踪器同步 ──

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {TrackerAnonKey}");
        client.DefaultRequestHeaders.Add("Prefer",        "return=representation");
        client.DefaultRequestHeaders.Add("User-Agent",    "DailyRoutines-OccultPotNotifier");
        return client;
    }

    private void TrySyncOnline(long now)
    {
        if (syncInFlight) return;
        if (!TryBuildContext(out var context)) return;

        var refresh = hasOnlineData ? SyncRefreshSeconds : FastRetrySeconds;
        var due     = context.Fingerprint != lastFingerprint || now - lastSyncAt >= refresh;
        if (!due) return;

        lastFingerprint = context.Fingerprint;
        lastSyncAt      = now;
        syncInFlight    = true;
        _ = SyncAsync(context, now);
    }

    // 在帧线程采集上下文: 实例指纹 (大区 + 当前常规 FATE) + 两罐本地观测快照
    private bool TryBuildContext(out SyncContext context)
    {
        context = default;

        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return false;

        var dcID = localPlayer.CurrentWorld.Value.DataCenter.RowId;
        if (dcID == 0) return false;

        uint fateID    = 0;
        long bestEpoch = 0;
        foreach (var fate in DService.Instance().Fate)
        {
            if (!OccultFateIds.Contains(fate.FateId)) continue;
            if (fate.StartTimeEpoch <= 0)             continue;
            if (fate.StartTimeEpoch > bestEpoch)
            {
                bestEpoch = fate.StartTimeEpoch;
                fateID    = fate.FateId;
            }
        }

        if (fateID == 0) return false;

        context = new SyncContext
        {
            Fingerprint = ComputeHash(dcID, fateID, (int)bestEpoch),
            Datacenter  = (ushort)dcID,
            North       = PotObs.From(pots[0]),
            South       = PotObs.From(pots[1])
        };
        return true;
    }

    private static string ComputeHash(uint dcID, uint fateID, int timestamp)
    {
        Span<byte> buffer = stackalloc byte[12];
        BitConverter.TryWriteBytes(buffer[..4],  dcID);
        BitConverter.TryWriteBytes(buffer[4..8], fateID);
        BitConverter.TryWriteBytes(buffer[8..],  timestamp);

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(buffer, hash);

        var sb = new StringBuilder(64);
        foreach (var b in hash)
            sb.Append(b.ToString("X2"));
        return sb.ToString();
    }

    private async Task SyncAsync(SyncContext context, long now)
    {
        try
        {
            var json = await Client.GetStringAsync($"{TrackerBaseURL}{TrackerTable}?last_fate=eq.{context.Fingerprint}");
            var rows = JsonConvert.DeserializeObject<TrackerRow[]>(json);

            if (rows is { Length: > 0 })
            {
                var row = rows[0];
                hasOnlineData = true;

                // 读取 -> 应用到本地显示
                var shared = string.IsNullOrEmpty(row.PotHistory)
                                 ? null
                                 : JsonConvert.DeserializeObject<SharedPot[]>(row.PotHistory);
                if (shared != null)
                {
                    long ns = -1, nl = -1, ss = -1, sl = -1;
                    foreach (var sp in shared)
                    {
                        if (sp.FateID      == 1976) { ns = sp.SpawnTime; nl = sp.LastSeen; }
                        else if (sp.FateID == 1977) { ss = sp.SpawnTime; sl = sp.LastSeen; }
                    }

                    lock (syncLock)
                        pendingSync = (ns, nl, ss, sl);
                }

                // 上报 -> 把本地观测合并进该行 pot_history (仅 PATCH pot_history, 不动其他列)
                await PatchPotHistoryAsync(row, context, now, shared);
            }
            else if (context.HasObservation && createdFingerprint != context.Fingerprint)
            {
                await CreateRowAsync(context);
                createdFingerprint = context.Fingerprint;
            }
        }
        catch
        {
            // 网络 / 解析错误忽略, 下次自动重试
        }
        finally
        {
            syncInFlight = false;
        }
    }

    // 把本地观测合并进现有行的 pot_history, 仅当本地比线上更新时才 PATCH (不动 fate/encounter 等其他列)
    private async Task PatchPotHistoryAsync(TrackerRow row, SyncContext context, long now, SharedPot[]? shared)
    {
        if (row.RowID <= 0) return;

        var changed = false;
        var north   = MergePot(1976, context.North, shared, ref changed);
        var south   = MergePot(1977, context.South, shared, ref changed);
        if (!changed) return;

        var body = JsonConvert.SerializeObject(new Dictionary<string, object>
        {
            ["pot_history"] = JsonConvert.SerializeObject(new[] { north, south }),
            ["last_update"] = now
        });

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        await Client.PatchAsync($"{TrackerBaseURL}{TrackerTable}?id=eq.{row.RowID}", content);
    }

    // 当该实例尚无记录时, 用本地观测新建一行 (fate/encounter 留空, 由其他玩家补全)
    private async Task CreateRowAsync(SyncContext context)
    {
        var potHistory = JsonConvert.SerializeObject(new[]
        {
            UploadPot.From(1976, context.North),
            UploadPot.From(1977, context.South)
        });

        var body = JsonConvert.SerializeObject(new Dictionary<string, object>
        {
            ["version"]           = TrackerVersion,
            ["last_fate"]         = context.Fingerprint,
            ["tracker_type"]      = 1,
            ["datacenter"]        = context.Datacenter,
            ["encounter_history"] = "[]",
            ["fate_history"]      = "[]",
            ["pot_history"]       = potHistory
        });

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        await Client.PostAsync($"{TrackerBaseURL}{TrackerTable}", content);
    }

    // 合并单罐: 本地观测比线上更新 (last_seen 更大) 时采用本地值并标记需上报
    private static UploadPot MergePot(uint fateID, PotObs local, SharedPot[]? shared, ref bool changed)
    {
        long spawn = -1, death = 0, lastSeen = -1;
        if (shared != null)
        {
            foreach (var sp in shared)
            {
                if (sp.FateID != fateID) continue;
                spawn    = sp.SpawnTime;
                death    = sp.DeathTime;
                lastSeen = sp.LastSeen;
                break;
            }
        }

        if (local.Observed && local.LastSeen > lastSeen)
        {
            spawn    = local.Spawn;
            death    = local.Death;
            lastSeen = local.LastSeen;
            changed  = true;
        }

        return new UploadPot { FateID = fateID, SpawnTime = spawn, DeathTime = death, LastSeen = lastSeen };
    }

    private void ApplyPendingSync()
    {
        (long NorthSpawn, long NorthSeen, long SouthSpawn, long SouthSeen)? data;
        lock (syncLock)
        {
            data        = pendingSync;
            pendingSync = null;
        }

        if (data == null) return;

        MergeSynced(pots[0], data.Value.NorthSpawn, data.Value.NorthSeen);
        MergeSynced(pots[1], data.Value.SouthSpawn, data.Value.SouthSeen);
    }

    // 同步数据只在比本地更新时填入; 本地正在观测的罐子不被覆盖 (本地观测最准)
    private static void MergeSynced(Pot pot, long spawn, long lastSeen)
    {
        if (pot.Alive) return;
        if (lastSeen > pot.LastSeenAlive) pot.LastSeenAlive = lastSeen;
        if (spawn    > pot.SpawnTime)     pot.SpawnTime     = spawn;
    }

    private struct SyncContext
    {
        public string Fingerprint;
        public ushort Datacenter;
        public PotObs North;
        public PotObs South;

        public readonly bool HasObservation =>
            (North.Observed && North.Spawn > 0) || (South.Observed && South.Spawn > 0);
    }

    private readonly struct PotObs
    {
        public bool Observed { get; init; }
        public long Spawn    { get; init; }
        public long Death    { get; init; }
        public long LastSeen { get; init; }

        public static PotObs From(Pot pot) => new()
        {
            Observed = pot.LocallyObserved,
            Spawn    = pot.SpawnTime,
            Death    = pot.DeathTime,
            LastSeen = pot.LastSeenAlive
        };
    }

    private class TrackerRow
    {
        [JsonProperty("id")]
        public long RowID;

        [JsonProperty("pot_history")]
        public string PotHistory = string.Empty;
    }

    private struct SharedPot
    {
        [JsonProperty("fate_id")]
        public uint FateID;

        [JsonProperty("spawn_time")]
        public long SpawnTime;

        [JsonProperty("death_time")]
        public long DeathTime;

        [JsonProperty("last_seen")]
        public long LastSeen;
    }

    private class UploadPot
    {
        [JsonProperty("fate_id")]
        public uint FateID;

        [JsonProperty("spawn_time")]
        public long SpawnTime;

        [JsonProperty("death_time")]
        public long DeathTime;

        [JsonProperty("last_seen")]
        public long LastSeen;

        [JsonProperty("respawn_times")]
        public long[] RespawnTimes = [];

        public static UploadPot From(uint fateID, PotObs obs) => new()
        {
            FateID    = fateID,
            SpawnTime = obs.Observed ? obs.Spawn    : -1,
            DeathTime = obs.Observed ? obs.Death    : 0,
            LastSeen  = obs.Observed ? obs.LastSeen : -1
        };
    }

    private enum PotDisplayMode
    {
        None,
        DtrBar,
        Overlay
    }

    private enum PotChatChannel
    {
        Party, // 小队 /p
        Emote  // 默语 /e
    }

    private sealed class Pot
    {
        public ushort  FateID;
        public Vector3 World;
        public string  DirName = string.Empty;

        // 运行时状态
        public bool Alive;
        public long SpawnTime       = -1;
        public long DeathTime       = -1;
        public long LastSeenAlive   = -1;
        public bool LocallyObserved; // 是否本人亲眼观测到 (决定是否上报)

        public void Reset()
        {
            Alive           = false;
            SpawnTime       = -1;
            DeathTime       = -1;
            LastSeenAlive   = -1;
            LocallyObserved = false;
        }
    }

    private class Config : ModuleConfig
    {
        public PotDisplayMode DisplayMode      = PotDisplayMode.DtrBar;
        public bool           UseOnlineTracker = true; // 开启即同时读取与上报

        public bool SendTTS          = true;
        public bool SendNotification = true;
        public bool SendChat;                 // 默认关闭: 主动开启才转发到聊天频道
        public PotChatChannel ChatChannel = PotChatChannel.Party;
        public int  LeadSeconds      = 300;   // 提前 5 分钟
    }
}
