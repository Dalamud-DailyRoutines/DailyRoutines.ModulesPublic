using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.RemoteInteraction.ISPTranslation;
using DailyRoutines.RemoteInteraction.ISPTranslation.Models.Responses;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Application.Network;
using Newtonsoft.Json;
using OmenTools.Dalamud;
using OmenTools.OmenService;
using Framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace DailyRoutines.ModulesPublic.Interface;

public class AutoDisplayNetworkLatency : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoDisplayNetworkLatencyTitle"),
        Description = Lang.Get("AutoDisplayNetworkLatencyDescription"),
        Category    = ModuleCategory.Interface,
        PreviewImageURL =
        [
            "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/AutoDisplayNetworkLatency/preview-1.png"
        ]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private Config config = null!;

    private          ServerPingMonitor?      monitor;
    private          IDtrBarEntry?           entry;
    private readonly CancellationTokenSource cancelSource = new();

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        monitor       ??= new();
        entry         ??= DService.Instance().DTRBar.Get("DailyRoutines-AutoDisplayNetworkLatency");
        entry.OnClick =   _ =>
        {
            if (Overlay == null)
            {
                Overlay       =  new(this);
                Overlay.Flags &= ~ImGuiWindowFlags.AlwaysAutoResize;
                Overlay.SizeConstraints = new()
                {
                    MinimumSize = ScaledVector2(300f, 200f)
                };
            }

            Overlay.Toggle();
        };

        Task.Run(MainLoop, cancelSource.Token);
    }

    protected override void Uninit()
    {
        cancelSource.Cancel();
        cancelSource.Dispose();

        monitor?.Dispose();
        monitor = null;

        entry?.Remove();
        entry = null;
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Format"));

        using (ImRaii.PushIndent())
        {
            ImGui.InputText("##FormatInput", ref config.Format);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);
        }
    }

    protected override unsafe void OverlayUI()
    {
        if (monitor == null) return;

        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
        {
            Overlay.IsOpen = false;
            return;
        }

        float min        = 9999f, max = 0f, sum = 0f;
        var   validCount = 0;
        var   lossCount  = 0;
        var   totalSamples = monitor.FilledCount;

        for (var i = 0; i < totalSamples; i++)
        {
            var value = monitor.History[i];

            if (value <= 0.1f)
            {
                lossCount++;
                continue;
            }

            if (value < min) min = value;
            if (value > max) max = value;
            sum += value;
            validCount++;
        }

        var average  = validCount   > 0 ? sum              / validCount : 0f;
        var lossRate = totalSamples > 0 ? (float)lossCount / totalSamples : 0f;
        if (min == 9999f)
            min = 0f;

        var currentPing = monitor.LastPing;
        var color       = GetPingColor(currentPing);

        ImGui.SetWindowFontScale(1.5f);
        ImGui.TextColored(color, $"{currentPing}");
        ImGui.SetWindowFontScale(1.0f);

        ImGui.SameLine();
        ImGui.TextColored(color, "ms");

        ImGui.SameLine();

        var addressText  = $"{monitor.ServerAddress}:{monitor.ServerPort}";
        var addressSize  = ImGui.CalcTextSize(addressText);
        var availableX   = ImGui.GetContentRegionAvail().X;
        if (availableX > addressSize.X)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + availableX - addressSize.X);
        ImGui.TextDisabled(addressText);

        var addressRectMax = ImGui.GetItemRectMax();

        if (monitor.AddressInfo is { } info)
        {
            using (FontManager.Instance().UIFont80.Push())
            {
                var locationText = $"{info.CountryName} - {info.CityName}";
                if (monitor.ISPInfo is { } ispInfo)
                    locationText += $" / {ispInfo.Translated}";

                if (!string.IsNullOrWhiteSpace(locationText))
                {
                    var locationSize = ImGui.CalcTextSize(locationText);
                    var windowPos    = ImGui.GetWindowPos();
                    var scrollY      = ImGui.GetScrollY();
                    ImGui.SetCursorPosY(addressRectMax.Y - windowPos.Y + scrollY);

                    var locationAvailableX = ImGui.GetContentRegionAvail().X;
                    if (locationAvailableX > locationSize.X)
                        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + locationAvailableX - locationSize.X);
                    ImGui.TextDisabled(locationText);
                }
            }
        }

        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.SameLine();

        using (var table = ImRaii.Table("##StatsTable", 4, ImGuiTableFlags.SizingStretchProp))
        {
            if (table)
            {
                DrawStatColumn("AVG",  $"{average:F0}", GetPingColor(average));
                DrawStatColumn("MIN",  $"{min:F0}",     GetPingColor(min));
                DrawStatColumn("MAX",  $"{max:F0}",     GetPingColor(max));

                var lossColor = lossRate switch
                {
                    0f      => KnownColor.SpringGreen.ToVector4(),
                    < 0.05f => KnownColor.Orange.ToVector4(),
                    _       => KnownColor.Red.ToVector4()
                };
                DrawStatColumn("LOSS", $"{lossRate:P0}", lossColor);
            }
        }

        using (ImRaii.PushColor(ImPlotCol.AxisBg, new Vector4(0.05f)))
        using (ImRaii.PushColor(ImPlotCol.FrameBg, Vector4.Zero))
        using (ImRaii.PushColor(ImPlotCol.AxisGrid, new Vector4(1f, 1f, 1f, 0.05f)))
        using (ImRaii.PushStyle(ImPlotStyleVar.FillAlpha, 0.25f))
        using (ImRaii.PushStyle(ImPlotStyleVar.LineWeight, 2f))
        using (var plot = ImRaii.Plot("##LatencyPlot", new(-1), ImPlotFlags.CanvasOnly | ImPlotFlags.NoTitle))
        {
            if (!plot) return;

            const ImPlotAxisFlags AXIS_FLAGS = ImPlotAxisFlags.NoLabel | ImPlotAxisFlags.NoTickLabels;
            ImPlot.SetupAxes((byte*)null, (byte*)null, AXIS_FLAGS, AXIS_FLAGS);

            var yMax = MathF.Max(max * 1.25f, 100f);
            ImPlot.SetupAxesLimits(0, monitor.History.Length, 0, yMax, ImPlotCond.Always);
            ImPlot.SetupAxisTicks(ImAxis.X1, 0, monitor.History.Length, 51);
            ImPlot.SetupAxisTicks(ImAxis.Y1, 0, yMax,                   21);

            using (ImRaii.PushColor(ImPlotCol.Line, color)
                         .Push(ImPlotCol.Fill, color))
                ImPlot.PlotLine("##Ping", ref monitor.History[0], monitor.History.Length, 1.0, 0.0, ImPlotLineFlags.Shaded, monitor.HistoryIndex);

            if (average <= 0) return;

            var averageColor = KnownColor.White.ToVector4() with { W = 0.6f };
            using (ImRaii.PushColor(ImPlotCol.Line, averageColor))
            {
                var xs = new double[] { 0, monitor.History.Length };
                var ys = new double[] { average, average };
                ImPlot.PlotLine("##Avg", ref xs[0], ref ys[0], 2);
            }
        }

        return;

        static Vector4 GetPingColor(float ping)
        {
            return ping switch
            {
                < 0   => KnownColor.Gray.ToVector4(),
                < 100 => KnownColor.SpringGreen.ToVector4(),
                < 200 => KnownColor.Orange.ToVector4(),
                _     => KnownColor.Red.ToVector4()
            };
        }

        static void DrawStatColumn(string label, string value, Vector4 color)
        {
            ImGui.TableNextColumn();
            ImGui.Spacing();
            ImGui.TextDisabled(label);
            ImGui.SameLine(0, 8f * GlobalUIScale);
            using (FontManager.Instance().UIFont120.Push())
                ImGui.TextColored(color, value);
        }
    }

    private async Task MainLoop()
    {
        try
        {
            var lastPing = -1L;

            while (!cancelSource.IsCancellationRequested)
            {
                if (monitor == null || entry == null) return;

                if (!GameState.IsLoggedIn)
                {
                    await Task.Delay(3000, cancelSource.Token);
                    continue;
                }

                await monitor.UpdateAsync(cancelSource.Token);

                var currentPing = monitor.LastPing;
                var address     = monitor.ServerAddress;
                var port        = monitor.ServerPort;

                await DService.Instance().Framework.RunOnTick
                (() =>
                    {
                        if (entry == null || cancelSource.IsCancellationRequested) return;

                        entry.Shown = true;

                        if (lastPing != currentPing)
                        {
                            entry.Text = string.Format(config.Format, currentPing);
                            lastPing   = currentPing;
                        }

                        var builder = new SeStringBuilder().AddIcon(BitmapFontIcon.Meteor)
                                                           .AddText($"{address}:{port}");

                        if (monitor.AddressInfo is { } info)
                            builder.AddText($" ({info.CountryName} - {info.CityName})");

                        entry.Tooltip = builder.Build();
                    }
                );

                await Task.Delay(1_000, cancelSource.Token);
            }
        }
        catch (OperationCanceledException) when (cancelSource.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            DLog.Error("更新网络延迟时发生错误", ex);
        }
    }

    private class Config : ModuleConfig
    {
        public string Format = Lang.Get("AutoDisplayNetworkLatency-DefaultFormat");
    }

    private class ServerPingMonitor : IDisposable
    {
        private const string TARGET_IP_QUERY_API = "http://ip-api.com/json/{0}?lang={1}";

        private CancellationTokenSource? ipInfoCancelSource;
        private ZoneEndpoint?           endpoint;

        public IPAddress              ServerAddress { get; private set; } = IPAddress.Loopback;
        public ushort                 ServerPort    { get; private set; }
        public IPLocationDTO?         AddressInfo   { get; private set; }
        public ISPTranslatorResponse? ISPInfo       { get; private set; }
        public long                   LastPing      { get; private set; } = -1;
        public float[]                History       { get; } = new float[100];
        public int                    HistoryIndex  { get; private set; }
        public int                    FilledCount   { get; private set; }

        public void Dispose()
        {
            ipInfoCancelSource?.Cancel();
            ipInfoCancelSource?.Dispose();
        }

        public async Task UpdateAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (await UpdateServerEndpointAsync(cancellationToken))
                {
                    if (ServerPort != 0)
                        RefreshIPInfo(ServerAddress);
                    else
                        ResetAddressInfo();
                }

                LastPing = ServerPort == 0 ? -1 : await MeasureLatencyAsync(cancellationToken);
            }
            catch (Exception)
            {
                LastPing = -1;
            }

            History[HistoryIndex] = LastPing == -1 ? 0 : LastPing;
            HistoryIndex          = (HistoryIndex + 1) % History.Length;
            if (FilledCount < History.Length) FilledCount++;
        }

        private async Task<bool> UpdateServerEndpointAsync(CancellationToken cancellationToken)
        {
            var nextEndpoint = await GetZoneEndpointAsync(cancellationToken);
            if (nextEndpoint == null || !IPAddress.TryParse(nextEndpoint.Value.Host, out var nextAddress))
                return ResetServerEndpoint();

            if (endpoint == nextEndpoint)
                return false;

            var changed = !nextAddress.Equals(ServerAddress) || nextEndpoint.Value.Port != ServerPort;
            endpoint      = nextEndpoint;
            ServerAddress = nextAddress;
            ServerPort    = nextEndpoint.Value.Port;
            return changed;
        }

        // TODO: 等待 FFCS 合并把 ZoneClient 变成 public
        private static unsafe ZoneEndpoint? GetZoneEndpoint()
        {
            var framework = Framework.Instance();
            if (framework == null)
                return null;

            var networkModuleProxy = framework->GetNetworkModuleProxy();
            if (networkModuleProxy == null || networkModuleProxy->NetworkModule == null)
                return null;

            var zoneClient = *(ZoneClient**)((byte*)networkModuleProxy->NetworkModule + 0xA70);
            if (zoneClient == null)
                return null;

            var host       = zoneClient->Host.ToString();
            return string.IsNullOrWhiteSpace(host) || zoneClient->Port == 0 ? null : new(host, zoneClient->Port);
        }

        private static async Task<ZoneEndpoint?> GetZoneEndpointAsync(CancellationToken cancellationToken)
        {
            ZoneEndpoint? endpoint = null;

            await DService.Instance().Framework.RunOnTick
            (
                () => endpoint = GetZoneEndpoint(),
                cancellationToken: cancellationToken
            );

            return endpoint;
        }

        private async Task<long> MeasureLatencyAsync(CancellationToken cancellationToken)
        {
            using var socket = new Socket(ServerAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(1));

            var timestamp = Stopwatch.GetTimestamp();

            try
            {
                await socket.ConnectAsync(new IPEndPoint(ServerAddress, ServerPort), timeoutSource.Token);
                return (long)Math.Round(Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds);
            }
            catch (OperationCanceledException)
            {
                return -1;
            }
            catch (SocketException)
            {
                return -1;
            }
        }

        private void RefreshIPInfo(IPAddress address)
        {
            ResetAddressInfo();
            ipInfoCancelSource = new();

            var token = ipInfoCancelSource.Token;
            Task.Run
            (
                async () =>
                {
                    try
                    {
                        if (HTTPClientHelper.Instance().Get() is not { } httpClient) return;

                        var response = await httpClient.GetStringAsync(string.Format(TARGET_IP_QUERY_API, address, CultureInfo.CurrentUICulture), token);
                        if (token.IsCancellationRequested) return;

                        if (JsonConvert.DeserializeObject<IPLocationDTO>(response) is not { } newInfo) return;
                        if (token.IsCancellationRequested) return;

                        ISPInfo = await RemoteISPTranslation.GetFreshAsync
                                  (
                                      newInfo.InternetServiceProvider,
                                      cancellationToken: token
                                  );
                        if (await RemoteISPTranslation.GetFreshAsync(newInfo.CityName, 
                                                                     cancellationToken: token) is { } cityNameInfo)
                            newInfo.CityName = cityNameInfo.Translated;

                        AddressInfo = newInfo;
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception)
                    {
                        AddressInfo = null;
                        ISPInfo     = null;
                    }
                },
                token
            );
        }

        private bool ResetServerEndpoint()
        {
            var changed = !ServerAddress.Equals(IPAddress.Loopback) || ServerPort != 0;
            endpoint      = null;
            ServerAddress = IPAddress.Loopback;
            ServerPort    = 0;
            return changed;
        }

        private void ResetAddressInfo()
        {
            ipInfoCancelSource?.Cancel();
            ipInfoCancelSource?.Dispose();
            ipInfoCancelSource = null;
            AddressInfo        = null;
            ISPInfo            = null;
        }

        private readonly record struct ZoneEndpoint
        (
            string Host,
            ushort Port
        );
    }

    private class IPLocationDTO
    {
        [JsonProperty("country")]
        public string? CountryName { get; set; }

        [JsonProperty("city")]
        public string? CityName { get; set; }

        [JsonProperty("isp")]
        public string? InternetServiceProvider { get; set; }
    }
}
