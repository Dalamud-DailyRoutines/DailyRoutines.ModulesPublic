using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.RemoteInteraction.ISPTranslation;
using DailyRoutines.RemoteInteraction.ISPTranslation.Models.Responses;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Utility;
using Dalamud.Utility.Numerics;
using Newtonsoft.Json;
using OmenTools.Dalamud;
using OmenTools.Info.Game.Packets.Downstream;
using OmenTools.Info.Game.Packets.Upstream;
using OmenTools.Interop.Windows.Models;
using Framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;
using NetworkModule = FFXIVClientStructs.Expansion.FFXIV.Application.Network.NetworkModule;

namespace DailyRoutines.ModulesPublic;

public class AutoDisplayNetworkLatency : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoDisplayNetworkLatencyTitle"),
        Description = Lang.Get("AutoDisplayNetworkLatencyDescription"),
        Category    = ModuleCategory.System,
        PreviewImageURL =
        [
            "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/AutoDisplayNetworkLatency/preview-1.webp"
        ]
    };

    public override ModuleBehaviour Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private ServerPingMonitor?       monitor;
    private IDtrBarEntry?            entry;
    private CancellationTokenSource? cancelSource;

    protected override void Init()
    {
        monitor ??= new();

        entry       ??= IDtrBar.Instance().Get("DailyRoutines-AutoDisplayNetworkLatency");
        entry.Shown =   false;
        entry.OnClick = _ =>
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

        GamePacketManager.Instance().RegPostReceivePacket(OnHeartbeatReceived);
        IClientState.Instance().Logout += OnLogout;

        cancelSource = new();
        _            = SendHeartbeatsAsync(cancelSource.Token);
    }

    protected override void Uninit()
    {
        GamePacketManager.Instance().Unreg(OnHeartbeatReceived);
        IClientState.Instance().Logout -= OnLogout;

        cancelSource?.Cancel();
        cancelSource?.Dispose();
        cancelSource = null;

        monitor?.Dispose();
        monitor = null;

        entry?.Remove();
        entry = null;
    }

    protected override unsafe void OverlayUI()
    {
        if (monitor == null) return;

        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
        {
            var (dtrMin, dtrMax) = entry.ScreenBounds;
            var viewportPos = ImGui.GetMainViewport().Pos;

            if (!ImGui.IsMouseHoveringRect(dtrMin + viewportPos, dtrMax + viewportPos, false))
            {
                Overlay.IsOpen = false;
                return;
            }
        }

        var min     = monitor.Minimum;
        var max     = monitor.Maximum;
        var average = monitor.Average;

        var currentPing = monitor.LastPing;
        var color       = GetPingColor(currentPing);

        using (FontManager.Instance().UIFont160.Push())
            ImGui.TextColored(color, $"{currentPing:F0}");

        ImGui.SameLine();
        ImGui.TextColored(color, "ms");

        ImGui.SameLine();

        var addressText = $"{monitor.ServerAddress}:{monitor.ServerPort}";
        var addressSize = ImGui.CalcTextSize(addressText);
        var availableX  = ImGui.GetContentRegionAvail().X;
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

        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.SameLine();

        using (var table = ImRaii.Table("##StatsTable", 4, ImGuiTableFlags.SizingStretchProp))
        {
            if (table)
            {
                DrawStatColumn(Lang.Get("AutoDisplayNetworkLatencyTitle-Stat-Avg"), $"{average:F0}", GetPingColor(average));
                DrawStatColumn(Lang.Get("AutoDisplayNetworkLatencyTitle-Stat-Min"), $"{min:F0}",     GetPingColor(min));
                DrawStatColumn(Lang.Get("AutoDisplayNetworkLatencyTitle-Stat-Max"), $"{max:F0}",     GetPingColor(max));

                var retransmissionRate = monitor.RetransmissionRate;
                var retransmissionColor = retransmissionRate switch
                {
                    < 0 => KnownColor.Gray.ToVector4(),
                    0   => KnownColor.SpringGreen.ToVector4(),
                    _   => KnownColor.Orange.ToVector4()
                };
                DrawStatColumn
                (
                    Lang.Get("AutoDisplayNetworkLatencyTitle-Stat-Loss"),
                    retransmissionRate < 0 ?
                        "—" :
                        $"{retransmissionRate:F1}%",
                    retransmissionColor
                );
            }
        }

        using (ImRaii.PushColor(ImPlotCol.AxisBg, new Vector4(0.05f)))
        using (ImRaii.PushColor(ImPlotCol.FrameBg, Vector4.Zero))
        using (ImRaii.PushColor(ImPlotCol.AxisGrid, Vector4.One.WithW(0.05f)))
        using (ImRaii.PushStyle(ImPlotStyleVar.FillAlpha, 0.25f))
        using (ImRaii.PushStyle(ImPlotStyleVar.LineWeight, 2f))
        using (var plot = ImRaii.Plot("##LatencyPlot", new(-1), ImPlotFlags.CanvasOnly | ImPlotFlags.NoTitle))
        {
            if (!plot) return;

            ImPlot.SetupAxes((byte*)null, (byte*)null, AXIS_FLAGS, AXIS_FLAGS);

            var yMax = MathF.Max(max * 1.25f, 100f);
            ImPlot.SetupAxesLimits(0, monitor.History.Length, 0, yMax, ImPlotCond.Always);
            ImPlot.SetupAxisTicks(ImAxis.X1, 0, monitor.History.Length, 51);
            ImPlot.SetupAxisTicks(ImAxis.Y1, 0, yMax,                   21);

            if (monitor.FilledCount > 0)
            {
                using (ImRaii.PushColor(ImPlotCol.Line, color)
                             .Push(ImPlotCol.Fill, color))
                    ImPlot.PlotLine
                    (
                        "##Ping",
                        ref monitor.History[0],
                        monitor.FilledCount,
                        1.0,
                        0.0,
                        ImPlotLineFlags.Shaded,
                        monitor.FilledCount == monitor.History.Length ?
                            monitor.HistoryIndex :
                            0
                    );
            }

            if (average <= 0) return;

            var averageColor = KnownColor.White.ToVector4() with { W = 0.6f };

            using (ImRaii.PushColor(ImPlotCol.Line, averageColor))
            {
                Span<double> xs = [0, monitor.FilledCount];
                Span<double> ys = [average, average];
                ImPlot.PlotLine("##Avg", ref xs[0], ref ys[0], 2);
            }
        }

        return;

        static Vector4 GetPingColor
        (
            float ping
        ) =>
            ping switch
            {
                < 0   => KnownColor.Gray.ToVector4(),
                < 100 => KnownColor.SpringGreen.ToVector4(),
                < 200 => KnownColor.Orange.ToVector4(),
                _     => KnownColor.Red.ToVector4()
            };

        static void DrawStatColumn
        (
            string  label,
            string  value,
            Vector4 color
        )
        {
            ImGui.TableNextColumn();

            ImGui.Spacing();
            ImGui.TextDisabled(label);

            ImGui.SameLine(0, 8f * GlobalUIScale);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() - ImGui.GetStyle().FramePadding.Y);
            using (FontManager.Instance().UIFont140.Push())
                ImGui.TextColored(color, value);
        }
    }

    private void OnLogout(int type, int code)
    {
        monitor?.Dispose();
        monitor = new();

        entry?.Shown    = false;
        Overlay?.IsOpen = false;
    }

    private void OnHeartbeatReceived(int opcode, nint packet)
    {
        if (opcode       != DownstreamOpcode.HeartbeatOpcode ||
            cancelSource == null)
            return;

        _ = IFramework.Instance().RunOnTick(UpdateLatency, delayTicks: 1, cancellationToken: cancelSource.Token);
    }

    private async Task SendHeartbeatsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await IFramework.Instance().Run(SendHeartbeat, cancellationToken: cancellationToken);
                await Task.Delay(1_000, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException &&
                cancellationToken.IsCancellationRequested)
                return;

            DLog.Error("发送网络延迟采样心跳时发生错误", ex);
        }
    }

    private unsafe void SendHeartbeat()
    {
        if (monitor == null ||
            !monitor.UpdateEndpoint())
        {
            entry?.Shown    = false;
            Overlay?.IsOpen = false;
            return;
        }

        var module = (NetworkModule*)Framework.Instance()->GetNetworkModuleProxy()->NetworkModule;
        var heartbeat = new HeartbeatPacket
        {
            ClientTime    = unchecked((uint)((UInt128)(ulong)Stopwatch.GetTimestamp() * 1000 / (ulong)Stopwatch.Frequency)),
            RoundTripTime = module->RoundTripTime,
            ClientData    = module->HeartbeatData
        };
        heartbeat.Send();
    }

    private unsafe void UpdateLatency()
    {
        if (monitor == null ||
            !monitor.UpdateEndpoint())
        {
            entry?.Shown    = false;
            Overlay?.IsOpen = false;
            return;
        }

        var module  = (NetworkModule*)Framework.Instance()->GetNetworkModuleProxy()->NetworkModule;
        var manager = module->ZoneClient->ChannelManager;
        if (manager                                == null ||
            manager->ConnectionManager             == null ||
            manager->ConnectionManager->Connection == null)
            return;

        var socket = manager->ConnectionManager->Connection->TCPSocket;
        if (socket         == null ||
            socket->Handle == nuint.MaxValue)
            return;

        var error = TCPInfo.Query(socket->Handle, out var info);

        if (error != 0)
        {
            if (monitor.TCPInfoError != error)
            {
                monitor.TCPInfoError = error;
                DLog.Error($"读取游戏连接的 TCP RTT 失败，Winsock 错误码：{error}");
            }

            entry?.Shown = false;
            return;
        }

        monitor.TCPInfoError = 0;
        monitor.AddSample(info, socket->Handle);
        UpdateEntry();
    }

    private void UpdateEntry()
    {
        if (entry   == null ||
            monitor == null)
            return;

        entry.Shown = monitor.FilledCount > 0;
        entry.Text = Lang.Get
        (
            "AutoDisplayNetworkLatency-Format",
            new Dictionary<string, object>
            {
                ["ms"] = monitor.LastPing < 0 ?
                             -1 :
                             MathF.Round(monitor.LastPing)
            }
        );

        using var rented = new RentedSeStringBuilder();

        rented.AppendIcon((uint)BitmapFontIcon.Meteor)
              .Append($"{monitor.ServerAddress}:{monitor.ServerPort}");
        if (monitor.AddressInfo is { } info)
            rented.Append((string)$"（{info.CountryName} - {info.CityName}）");

        entry.Tooltip = rented.ToReadOnlySeString().ToDalamudString();
    }

    private class ServerPingMonitor : IDisposable
    {
        public string  ServerAddress      { get; private set; } = string.Empty;
        public ushort  ServerPort         { get; private set; }
        public float   LastPing           { get; private set; } = -1;
        public float[] History            { get; }              = new float[HISTORY_CAPACITY];
        public int     HistoryIndex       { get; private set; }
        public int     FilledCount        { get; private set; }
        public float   Minimum            { get; private set; }
        public float   Maximum            { get; private set; }
        public float   Average            { get; private set; }
        public double  RetransmissionRate { get; private set; } = -1;

        public IPLocationDTO?         AddressInfo { get; private set; }
        public ISPTranslatorResponse? ISPInfo     { get; private set; }

        private          CancellationTokenSource?           addressLookupSource;
        private readonly (ulong Sent, uint Retransmitted)[] retransmissionHistory = new (ulong, uint)[HISTORY_CAPACITY];
        private          TCPInfo?                           previousTCPInfo;
        private          nuint                              previousSocketHandle;
        private          ulong                              sentBytes;
        private          ulong                              retransmittedBytes;

        public int TCPInfoError { get; set; }

        public void Dispose()
        {
            addressLookupSource?.Cancel();
            addressLookupSource?.Dispose();
            addressLookupSource = null;
        }

        public unsafe bool UpdateEndpoint()
        {
            var framework = Framework.Instance();
            var proxy = framework == null ?
                            null :
                            framework->GetNetworkModuleProxy();
            var module = proxy == null ?
                             null :
                             (NetworkModule*)proxy->NetworkModule;
            var client = module == null ?
                             null :
                             module->ZoneClient;

            if (!GameState.IsLoggedIn ||
                client       == null  ||
                client->Port == 0)
            {
                Reset();
                return false;
            }

            var address = client->Host.ToString();

            if (string.IsNullOrWhiteSpace(address))
            {
                Reset();
                return false;
            }

            if (ServerAddress != address ||
                ServerPort    != client->Port)
            {
                Reset();
                ServerAddress = address;
                ServerPort    = client->Port;

                if (!RuntimeInformation.IsWine())
                {
                    addressLookupSource = new();
                    var token = addressLookupSource.Token;
                    _ = Task.Run(() => RefreshAddressInfoAsync(address, token), token);
                }
            }

            return true;
        }

        public void AddSample(TCPInfo info, nuint socketHandle)
        {
            var previous = previousTCPInfo;

            if (previous is { } baseline &&
                (previousSocketHandle != socketHandle           ||
                 info.ConnectionTime  < baseline.ConnectionTime ||
                 info.BytesSent       < baseline.BytesSent))
            {
                Array.Clear(retransmissionHistory);
                sentBytes          = 0;
                retransmittedBytes = 0;
                previous           = null;
            }

            var expired = retransmissionHistory[HistoryIndex];
            sentBytes          -= expired.Sent;
            retransmittedBytes -= expired.Retransmitted;

            var sent = previous is { } previousSent ?
                           info.BytesSent - previousSent.BytesSent :
                           0;
            var retransmitted = previous is { } previousRetransmitted ?
                                    unchecked(info.BytesRetransmitted - previousRetransmitted.BytesRetransmitted) :
                                    0;
            retransmissionHistory[HistoryIndex] =  (sent, retransmitted);
            sentBytes                           += sent;
            retransmittedBytes                  += retransmitted;
            previousTCPInfo                     =  info;
            previousSocketHandle                =  socketHandle;
            RetransmissionRate = sentBytes == 0 ?
                                     -1 :
                                     100d * retransmittedBytes / sentBytes;

            var value = info.RoundTripTime / 1000f;
            History[HistoryIndex] = value;
            HistoryIndex          = (HistoryIndex + 1) % History.Length;
            FilledCount           = Math.Min(FilledCount + 1, History.Length);
            LastPing              = value;

            var sum = 0f;
            Minimum = float.PositiveInfinity;
            Maximum = 0;

            for (var i = 0; i < FilledCount; i++)
            {
                var sample = History[i];
                Minimum =  MathF.Min(Minimum, sample);
                Maximum =  MathF.Max(Maximum, sample);
                sum     += sample;
            }

            Average = sum / FilledCount;
        }

        private void Reset()
        {
            Dispose();
            AddressInfo   = null;
            ISPInfo       = null;
            ServerAddress = string.Empty;
            ServerPort    = 0;
            HistoryIndex  = 0;
            FilledCount   = 0;
            LastPing      = -1;
            Minimum       = 0;
            Maximum       = 0;
            Average       = 0;
            TCPInfoError  = 0;
            Array.Clear(retransmissionHistory);
            previousTCPInfo      = null;
            previousSocketHandle = 0;
            sentBytes            = 0;
            retransmittedBytes   = 0;
            RetransmissionRate   = -1;
        }

        private async Task RefreshAddressInfoAsync(string address, CancellationToken cancellationToken)
        {
            try
            {
                if (HTTPClientHelper.Instance().Get() is not { } httpClient)
                    return;

                var response = await httpClient.GetStringAsync
                               (
                                   $"http://ip-api.com/json/{Uri.EscapeDataString(address)}?lang={CultureInfo.CurrentUICulture.Name}",
                                   cancellationToken
                               );
                var info = JsonConvert.DeserializeObject<IPLocationDTO>(response);
                if (info == null ||
                    string.IsNullOrWhiteSpace(info.CountryName))
                    throw new InvalidOperationException($"IP 归属地查询未返回有效结果：{response}");

                await IFramework.Instance().RunOnTick
                (
                    () => AddressInfo = info,
                    cancellationToken: cancellationToken
                );

                var ispInfo  = await RemoteISPTranslation.GetFreshAsync(info.InternetServiceProvider, cancellationToken: cancellationToken);
                var cityInfo = await RemoteISPTranslation.GetFreshAsync(info.CityName,                cancellationToken: cancellationToken);

                await IFramework.Instance().RunOnTick
                (
                    () =>
                    {
                        info.CityName = cityInfo.Translated;
                        ISPInfo       = ispInfo;
                    },
                    cancellationToken: cancellationToken
                );
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException &&
                    cancellationToken.IsCancellationRequested)
                    return;

                DLog.Error($"更新服务器归属地时发生错误：{address}", ex);
            }
        }

        #region 常量

        private const int HISTORY_CAPACITY = 100;

        #endregion
    }

    private class IPLocationDTO
    {
        [JsonProperty("country")]
        public string CountryName { get; set; } = string.Empty;

        [JsonProperty("city")]
        public string CityName { get; set; } = string.Empty;

        [JsonProperty("isp")]
        public string InternetServiceProvider { get; set; } = string.Empty;
    }

    #region 常量

    private const ImPlotAxisFlags AXIS_FLAGS = ImPlotAxisFlags.NoLabel | ImPlotAxisFlags.NoTickLabels;

    #endregion
}
