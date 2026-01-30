using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using DailyRoutines.Managers;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Newtonsoft.Json;

namespace DailyRoutines.ModulesPublic;

public partial class AutoDisplayNetworkLatency : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title           = GetLoc("AutoDisplayNetworkLatencyTitle"),
        Description     = GetLoc("AutoDisplayNetworkLatencyDescription"),
        Category        = ModuleCategories.System,
        PreviewImageURL = ["https://gh.atmoomen.top/raw.githubusercontent.com/AtmoOmen/StaticAssets/main/DailyRoutines/image/AutoDisplayNetworkLatency-UI.png"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static Config ModuleConfig = null!;

    private static ServerPingMonitor?       Monitor;
    private static IDtrBarEntry?            Entry;
    private static CancellationTokenSource? CancelSource;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        Overlay       ??= new(this);
        Overlay.Flags &=  ~ImGuiWindowFlags.AlwaysAutoResize;
        Overlay.SizeConstraints = new()
        {
            MinimumSize = ScaledVector2(300f, 200f)
        };

        CancelSource  ??= new();
        Monitor       ??= new();
        Entry         ??= DService.Instance().DTRBar.Get("DailyRoutines-AutoDisplayNetworkLatency");
        Entry.OnClick =   _ => Overlay.Toggle();

        Task.Run(MainLoop, CancelSource.Token);
    }

    protected override void Uninit()
    {
        CancelSource?.Cancel();
        CancelSource?.Dispose();
        CancelSource = null;

        Monitor?.Dispose();
        Monitor = null;

        if (Entry != null)
        {
            Entry.Remove();
            Entry = null;
        }
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("Format"));

        using (ImRaii.PushIndent())
        {
            ImGui.InputText("##FormatInput", ref ModuleConfig.Format);
            if (ImGui.IsItemDeactivatedAfterEdit())
                SaveConfig(ModuleConfig);
        }
    }

    protected override unsafe void OverlayUI()
    {
        if (Monitor == null) return;

        float min          = 9999f, max = 0f, sum = 0f;
        var   validCount   = 0;
        var   lossCount    = 0;
        var   totalSamples = Monitor.FilledCount;

        for (var i = 0; i < totalSamples; i++)
        {
            var val = Monitor.History[i];

            if (val <= 0.1f)
            {
                lossCount++;
                continue;
            }

            if (val < min) min = val;
            if (val > max) max = val;
            sum += val;
            validCount++;
        }

        var avg      = validCount   > 0 ? sum              / validCount : 0f;
        var lossRate = totalSamples > 0 ? (float)lossCount / totalSamples : 0f;
        if (min == 9999f)
            min = 0f;

        var currentPing = Monitor.LastPing;
        var color       = GetPingColor(currentPing);

        ImGui.SetWindowFontScale(1.5f);
        ImGui.TextColored(color, $"{currentPing}");
        ImGui.SetWindowFontScale(1.0f);

        ImGui.SameLine();
        ImGui.TextColored(color, "ms");

        ImGui.SameLine();

        if (Monitor.ObservedServerPort != 0 && (!Monitor.ObservedServerAddress.Equals(Monitor.ServerAddress) || Monitor.ObservedServerPort != Monitor.ServerPort))
        {
            var observedText   = $"{Monitor.ObservedServerAddress}:{Monitor.ObservedServerPort} → {Monitor.ServerAddress}:{Monitor.ServerPort}";
            var observedSize   = ImGui.CalcTextSize(observedText);
            var observedAvailX = ImGui.GetContentRegionAvail().X;
            if (observedAvailX > observedSize.X)
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + observedAvailX - observedSize.X);
            ImGui.TextDisabled(observedText);
        }
        else
        {
            var ipText = $"{Monitor.ServerAddress}:{Monitor.ServerPort}";
            var ipSize = ImGui.CalcTextSize(ipText);
            var availX = ImGui.GetContentRegionAvail().X;
            if (availX > ipSize.X)
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + availX - ipSize.X);
            ImGui.TextDisabled(ipText);
        }

        var ipRectMax = ImGui.GetItemRectMax();

        if (Monitor.AddressInfo is { } info)
        {
            using (FontManager.Instance().UIFont80.Push())
            {
                var locText = $"{info.CountryName} - {info.CityName}";
                if (Monitor.ISPInfo is { } ispInfo)
                    locText += $" / {ispInfo.Translated}";

                if (!string.IsNullOrWhiteSpace(locText))
                {
                    var locSize = ImGui.CalcTextSize(locText);

                    var windowPos = ImGui.GetWindowPos();
                    var scrollY   = ImGui.GetScrollY();
                    ImGui.SetCursorPosY(ipRectMax.Y - windowPos.Y + scrollY);

                    var locAvailX = ImGui.GetContentRegionAvail().X;
                    if (locAvailX > locSize.X)
                        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + locAvailX - locSize.X);
                    ImGui.TextDisabled(locText);
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
                DrawStatColumn("AVG", $"{avg:F0}", GetPingColor(avg));
                DrawStatColumn("MIN", $"{min:F0}", GetPingColor(min));
                DrawStatColumn("MAX", $"{max:F0}", GetPingColor(max));

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
            if (plot)
            {
                const ImPlotAxisFlags AXIS_FLAGS = ImPlotAxisFlags.NoLabel | ImPlotAxisFlags.NoTickLabels;
                ImPlot.SetupAxes((byte*)null, (byte*)null, AXIS_FLAGS, AXIS_FLAGS);

                var yMax = MathF.Max(max * 1.25f, 100f);
                ImPlot.SetupAxesLimits(0, Monitor.History.Length, 0, yMax, ImPlotCond.Always);

                ImPlot.SetupAxisTicks(ImAxis.X1, 0, Monitor.History.Length, 51);
                ImPlot.SetupAxisTicks(ImAxis.Y1, 0, yMax,                   21);

                using (ImRaii.PushColor(ImPlotCol.Line, color)
                             .Push(ImPlotCol.Fill, color))
                    ImPlot.PlotLine("##Ping", ref Monitor.History[0], Monitor.History.Length, 1.0, 0.0, ImPlotLineFlags.Shaded, Monitor.HistoryIndex);

                if (avg > 0)
                {
                    var avgColor = KnownColor.White.ToVector4() with { W = 0.6f };

                    using (ImRaii.PushColor(ImPlotCol.Line, avgColor))
                    {
                        var xs = new double[] { 0, Monitor.History.Length };
                        var ys = new double[] { avg, avg };
                        ImPlot.PlotLine("##Avg", ref xs[0], ref ys[0], 2);
                    }
                }
            }
        }

        return;

        static Vector4 GetPingColor(float ping) =>
            ping switch
            {
                < 0   => KnownColor.Gray.ToVector4(),
                < 100 => KnownColor.SpringGreen.ToVector4(),
                < 200 => KnownColor.Orange.ToVector4(),
                _     => KnownColor.Red.ToVector4()
            };

        static void DrawStatColumn(string label, string value, Vector4 color)
        {
            ImGui.TableNextColumn();
            ImGui.Spacing();
            ImGui.TextDisabled(label);

            ImGui.SameLine(0, 8f * GlobalFontScale);
            using (FontManager.Instance().UIFont120.Push())
                ImGui.TextColored(color, value);
        }
    }

    private static async Task MainLoop()
    {
        try
        {
            var lastPing = -1L;

            while (!CancelSource!.IsCancellationRequested)
            {
                if (Monitor == null || Entry == null) return;

                if (!GameState.IsLoggedIn)
                {
                    await Task.Delay(3000, CancelSource.Token);
                    continue;
                }

                await Monitor.UpdateAsync();

                var currentPing     = Monitor.LastPing;
                var address         = Monitor.ServerAddress;
                var port            = Monitor.ServerPort;
                var observedAddress = Monitor.ObservedServerAddress;
                var observedPort    = Monitor.ObservedServerPort;

                await DService.Instance().Framework.RunOnTick
                (() =>
                    {
                        if (Entry == null || CancelSource.IsCancellationRequested) return;

                        Entry.Shown = true;

                        if (lastPing != currentPing)
                        {
                            Entry.Text = string.Format(ModuleConfig.Format, currentPing);
                            lastPing   = currentPing;
                        }

                        var tooltipText = observedPort != 0 && (!observedAddress.Equals(address) || observedPort != port)
                                              ? $"{observedAddress}:{observedPort} -> {address}:{port}"
                                              : $"{address}:{port}";

                        var builder = new SeStringBuilder().AddIcon(BitmapFontIcon.Meteor)
                                                           .AddText(tooltipText);

                        if (Monitor.AddressInfo is { } info)
                            builder.AddText($" ({info.CountryName} - {info.CityName})");

                        Entry.Tooltip = builder.Build();
                    }
                );

                await Task.Delay(1_000, CancelSource.Token);
            }
        }
        catch
        {
            // ignored
        }
    }

    private class Config : ModuleConfiguration
    {
        public string Format = GetLoc("AutoDisplayNetworkLatency-DefaultFormat");
    }

    private partial class ServerPingMonitor : IDisposable
    {
        private readonly Ping pingSender = new();

        private unsafe byte* buffer;
        private        int   bufferSize;

        public IPAddress              ServerAddress         { get; private set; } = IPAddress.Loopback;
        public ushort                 ServerPort            { get; private set; }
        public IPAddress              ObservedServerAddress { get; private set; } = IPAddress.Loopback;
        public ushort                 ObservedServerPort    { get; private set; }
        public IPLocationDTO?         AddressInfo           { get; private set; }
        public ISPTranslatorResponse? ISPInfo               { get; private set; }
        public long                   LastPing              { get; private set; } = -1;
        public float[]                History               { get; private set; } = new float[100];
        public int                    HistoryIndex          { get; private set; }
        public int                    FilledCount           { get; private set; }

        private int needToRefreshAddress;

        private CancellationTokenSource? ipInfoCancelSource;
        private Task?                    ipInfoTask;

        private const string TARGET_IP_QUERY_API = "http://ip-api.com/json/{0}?lang={1}";

        private const int AF_INET                   = 2;
        private const int AF_INET6                  = 23;
        private const int TCP_TABLE_OWNER_PID_ALL   = 5;
        private const int MIB_TCP_STATE_ESTABLISHED = 5;
        private const int MIB_TCP_STATE_LISTEN      = 2;

        public ServerPingMonitor()
        {
            DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
            GameState.Instance().Login                       += OnLogin;
        }

        private void OnLogin() =>
            Interlocked.Exchange(ref needToRefreshAddress, 1);

        private void OnZoneChanged(ushort obj) =>
            Interlocked.Exchange(ref needToRefreshAddress, 1);

        public async Task UpdateAsync()
        {
            try
            {
                if (UpdateAddressInfo())
                {
                    if (IsPublicAddress(ServerAddress))
                        RefreshIPInfo(ServerAddress);
                    else
                    {
                        ipInfoCancelSource?.Cancel();
                        AddressInfo = null;
                        ISPInfo     = null;
                    }
                }

                if (ServerAddress.Equals(IPAddress.Loopback))
                {
                    LastPing   = -1;
                    ServerPort = 0;
                    return;
                }

                var reply = await pingSender.SendPingAsync(ServerAddress, 1000);
                LastPing = reply.Status == IPStatus.Success ? reply.RoundtripTime : -1;

                History[HistoryIndex] = LastPing == -1 ? 0 : (float)LastPing;
                HistoryIndex          = (HistoryIndex + 1) % History.Length;
                if (FilledCount < History.Length) FilledCount++;
            }
            catch
            {
                LastPing              = -1;
                History[HistoryIndex] = 0;
                HistoryIndex          = (HistoryIndex + 1) % History.Length;
                if (FilledCount < History.Length) FilledCount++;
            }
        }

        private void RefreshIPInfo(IPAddress address)
        {
            ipInfoCancelSource?.Cancel();
            ipInfoCancelSource?.Dispose();
            ipInfoCancelSource = new();

            ISPInfo     = null;
            AddressInfo = null;

            var token = ipInfoCancelSource.Token;
            ipInfoTask = Task.Run(async () =>
            {
                try
                {
                    if (HTTPClientHelper.Get() is not { } httpClient) return;

                    var response = await httpClient.GetStringAsync(string.Format(TARGET_IP_QUERY_API, address, CultureInfo.CurrentUICulture), token);
                    if (token.IsCancellationRequested) return;

                    if (JsonConvert.DeserializeObject<IPLocationDTO>(response) is { } newInfo)
                    {
                        if (token.IsCancellationRequested) return;

                        ISPInfo = await ISPTranslatorRequest.TranslateAsync(newInfo.InternetServiceProvider);
                        if (await ISPTranslatorRequest.TranslateAsync(newInfo.CityName) is { } cityNameInfo)
                            newInfo.CityName = cityNameInfo.Translated;

                        AddressInfo = newInfo;
                    }
                }
                catch (OperationCanceledException)
                {
                    // ignored
                }
                catch (Exception)
                {
                    AddressInfo = null;
                    ISPInfo     = null;
                }
            }, token);
        }

        private bool UpdateAddressInfo()
        {
            try
            {
                var shouldRefresh =
                    Volatile.Read(ref needToRefreshAddress) != 0 || ServerPort == 0 || IPAddress.IsLoopback(ServerAddress);

                if (!shouldRefresh)
                    return false;

                ResetAddress();

                var currentPID = (uint)Environment.ProcessId;
                if (!TryFindBestEndpointForPID(currentPID, out var observed))
                {
                    ResetAddress();
                    return false;
                }

                ObservedServerAddress = observed.Address;
                ObservedServerPort    = observed.Port;

                var effective = observed;

                if (IPAddress.IsLoopback(observed.Address) && TryFindProxyPidByListenPort(observed.Port, out var proxyPid))
                {
                    if (TryFindBestEndpointForPID(proxyPid, out var proxyEndpoint) && !IPAddress.IsLoopback(proxyEndpoint.Address))
                        effective = proxyEndpoint;
                }

                var changed = !effective.Address.Equals(ServerAddress) || effective.Port != ServerPort;
                ServerAddress = effective.Address;
                ServerPort    = effective.Port;

                Volatile.Write(ref needToRefreshAddress, 0);
                return changed;
            }
            catch
            {
                ResetAddress();
            }

            return false;
        }

        private void ResetAddress()
        {
            ObservedServerAddress = IPAddress.Loopback;
            ObservedServerPort    = 0;

            if (!ServerAddress.Equals(IPAddress.Loopback))
                ServerAddress = IPAddress.Loopback;
            ServerPort = 0;
        }

        private static bool InXIVPortRange(ushort port) =>
            port is >= 54992 and <= 54994
                or >= 55006 and <= 55007
                or >= 55021 and <= 55040
                or >= 55296 and <= 55551;

        private static bool IsFilteredPort(ushort port) =>
            port is 80 or 443;

        private bool TryFindBestEndpointForPID(uint pid, out ConnectionEndpoint endpoint)
        {
            if (TryFindBestEndpointForPID(pid, true, out endpoint))
                return true;

            if (TryFindBestEndpointForPID(pid, false, out endpoint))
                return true;

            endpoint = default;
            return false;
        }

        private bool TryFindBestEndpointForPID(uint pid, bool onlyXivPorts, out ConnectionEndpoint endpoint)
        {
            endpoint = default;
            var found = false;

            TryScanTCPTableForPID(pid, AF_INET,  onlyXivPorts, ref endpoint, ref found);
            TryScanTCPTableForPID(pid, AF_INET6, onlyXivPorts, ref endpoint, ref found);

            return found;
        }

        private unsafe void TryScanTCPTableForPID(uint pid, int ipVersion, bool onlyXivPorts, ref ConnectionEndpoint best, ref bool found)
        {
            var requiredSize = 0;
            GetExtendedTCPTable(nint.Zero, ref requiredSize, false, ipVersion, TCP_TABLE_OWNER_PID_ALL);
            if (requiredSize <= 0)
                return;

            if (bufferSize < requiredSize)
            {
                if (buffer != null) NativeMemory.Free(buffer);
                bufferSize = requiredSize;
                buffer     = (byte*)NativeMemory.Alloc((nuint)bufferSize);
            }

            if (GetExtendedTCPTable((nint)buffer, ref requiredSize, false, ipVersion, TCP_TABLE_OWNER_PID_ALL) != 0)
                return;

            var numEntries = Unsafe.Read<int>(buffer);

            switch (ipVersion)
            {
                case AF_INET:
                {
                    var rowPtr = (TCPRow*)(buffer + sizeof(int));

                    for (var i = 0; i < numEntries; i++)
                    {
                        ref readonly var row = ref rowPtr[i];
                        if (row.OwningPID != pid || row.State != MIB_TCP_STATE_ESTABLISHED)
                            continue;

                        var port = BinaryPrimitives.ReverseEndianness((ushort)row.RemotePort);
                        if (IsFilteredPort(port))
                            continue;
                        if (onlyXivPorts && !InXIVPortRange(port))
                            continue;

                        var remoteAddress = row.RemoteAddress;
                        if (remoteAddress == 0)
                            continue;

                        var inXivRange       = InXIVPortRange(port);
                        var isLoopback       = remoteAddress == 0x0100007F;
                        var isPrivateOrLocal = IsPrivateOrLocalAddressIPv4(remoteAddress);
                        var score            = ScoreEndpoint(isLoopback, isPrivateOrLocal, port, inXivRange);

                        if (!found || score > best.Score)
                        {
                            best  = new ConnectionEndpoint(new IPAddress(remoteAddress), port, score);
                            found = true;
                        }
                    }

                    return;
                }
                case AF_INET6:
                {
                    var        rowPtr       = (TCP6Row*)(buffer + sizeof(int));
                    Span<byte> addressBytes = stackalloc byte[16];

                    for (var i = 0; i < numEntries; i++)
                    {
                        ref readonly var row = ref rowPtr[i];
                        if (row.OwningPID != pid || row.State != MIB_TCP_STATE_ESTABLISHED)
                            continue;

                        var port = BinaryPrimitives.ReverseEndianness((ushort)row.RemotePort);
                        if (IsFilteredPort(port))
                            continue;
                        if (onlyXivPorts && !InXIVPortRange(port))
                            continue;

                        var isUnspecified = true;
                        for (var j = 0; j < 16; j++)
                            isUnspecified &= row.RemoteAddress[j] == 0;
                        if (isUnspecified)
                            continue;

                        var isLoopback = true;
                        for (var j = 0; j < 16; j++)
                            isLoopback &= j == 15 ? row.RemoteAddress[j] == 1 : row.RemoteAddress[j] == 0;

                        var inXivRange = InXIVPortRange(port);

                        fixed (byte* ptr = row.RemoteAddress)
                        {
                            var isPrivateOrLocal = IsPrivateOrLocalAddressIPv6(ptr);
                            var score            = ScoreEndpoint(isLoopback, isPrivateOrLocal, port, inXivRange);

                            if (!found || score > best.Score)
                            {
                                for (var j = 0; j < 16; j++)
                                    addressBytes[j] = row.RemoteAddress[j];

                                best  = new ConnectionEndpoint(new IPAddress(addressBytes), port, score);
                                found = true;
                            }
                        }
                    }

                    break;
                }
            }

        }

        private static int ScoreEndpoint(bool isLoopback, bool isPrivateOrLocal, ushort port, bool inXivRange)
        {
            var score = 0;

            if (inXivRange) score += 100;
            score += isLoopback ? -200 : 40;
            score += isPrivateOrLocal ? -30 : 20;

            score += port switch
            {
                443 or 80 => 10,
                _         => 0
            };

            return score;
        }

        private bool TryFindProxyPidByListenPort(ushort listenPort, out uint proxyPid)
        {
            proxyPid = 0;

            if (TryFindProxyPIDByListenPortForIPVersion(listenPort, AF_INET, out proxyPid))
                return true;

            if (TryFindProxyPIDByListenPortForIPVersion(listenPort, AF_INET6, out proxyPid))
                return true;

            return false;
        }

        private unsafe bool TryFindProxyPIDByListenPortForIPVersion(ushort listenPort, int ipVersion, out uint proxyPid)
        {
            proxyPid = 0;

            var requiredSize = 0;
            GetExtendedTCPTable(nint.Zero, ref requiredSize, false, ipVersion, TCP_TABLE_OWNER_PID_ALL);
            if (requiredSize <= 0)
                return false;

            if (bufferSize < requiredSize)
            {
                if (buffer != null) NativeMemory.Free(buffer);
                bufferSize = requiredSize;
                buffer     = (byte*)NativeMemory.Alloc((nuint)bufferSize);
            }

            if (GetExtendedTCPTable((nint)buffer, ref requiredSize, false, ipVersion, TCP_TABLE_OWNER_PID_ALL) != 0)
                return false;

            var numEntries = Unsafe.Read<int>(buffer);

            switch (ipVersion)
            {
                case AF_INET:
                {
                    var rowPtr = (TCPRow*)(buffer + sizeof(int));

                    for (var i = 0; i < numEntries; i++)
                    {
                        ref readonly var row = ref rowPtr[i];
                        if (row.State != MIB_TCP_STATE_LISTEN)
                            continue;

                        var port = BinaryPrimitives.ReverseEndianness((ushort)row.LocalPort);
                        if (port != listenPort)
                            continue;

                        if (row.LocalAddress != 0 && row.LocalAddress != 0x0100007F)
                            continue;

                        proxyPid = row.OwningPID;
                        return true;
                    }

                    break;
                }
                case AF_INET6:
                {
                    var rowPtr = (TCP6Row*)(buffer + sizeof(int));

                    for (var i = 0; i < numEntries; i++)
                    {
                        ref readonly var row = ref rowPtr[i];
                        if (row.State != MIB_TCP_STATE_LISTEN)
                            continue;

                        var port = BinaryPrimitives.ReverseEndianness((ushort)row.LocalPort);
                        if (port != listenPort)
                            continue;

                        var isUnspecified = true;
                        var isLoopback    = true;

                        for (var j = 0; j < 16; j++)
                        {
                            var b = row.LocalAddress[j];
                            isUnspecified &= b == 0;
                            isLoopback    &= j == 15 ? b == 1 : b == 0;
                        }

                        if (!isUnspecified && !isLoopback)
                            continue;

                        proxyPid = row.OwningPID;
                        return true;
                    }

                    break;
                }
            }

            return false;
        }

        private static bool IsPublicAddress(IPAddress address)
        {
            if (IPAddress.IsLoopback(address))
                return false;

            Span<byte> bytes = stackalloc byte[16];
            if (!address.TryWriteBytes(bytes, out var written))
                return false;

            return written switch
            {
                4  => !IsPrivateOrLocalAddressIPv4(bytes[0], bytes[1]),
                16 => !IsPrivateOrLocalAddressIPv6(bytes),
                _  => false
            };
        }

        private static bool IsPrivateOrLocalAddressIPv4(uint addressValue)
        {
            var b0 = (byte)addressValue;
            var b1 = (byte)(addressValue >> 8);

            return IsPrivateOrLocalAddressIPv4(b0, b1);
        }

        private static bool IsPrivateOrLocalAddressIPv4(byte first, byte second)
        {
            switch (first)
            {
                case 10:
                case 100 when second is >= 64 and <= 127:
                case 172 when second is >= 16 and <= 31:
                case 192 when second == 168:
                case 169 when second == 254:
                case 0:
                case 127:
                case >= 224:
                    return true;
                default:
                    return false;
            }

        }

        private static bool IsPrivateOrLocalAddressIPv6(ReadOnlySpan<byte> address)
        {
            if (address.Length < 16)
                return true;

            switch (address[0])
            {
                case 0xFF:
                case 0xFE when (address[1] & 0xC0) == 0x80:
                    return true;
            }

            if ((address[0] & 0xFE) == 0xFC)
                return true;

            var allZero = true;
            for (var i = 0; i < 16; i++)
                allZero &= address[i] == 0;

            if (allZero)
                return true;

            var isLoopback = true;
            for (var i = 0; i < 16; i++)
                isLoopback &= i == 15 ? address[i] == 1 : address[i] == 0;

            return isLoopback;
        }

        private static unsafe bool IsPrivateOrLocalAddressIPv6(byte* address) =>
            IsPrivateOrLocalAddressIPv6(new ReadOnlySpan<byte>(address, 16));

        [LibraryImport("Iphlpapi.dll", EntryPoint = "GetExtendedTcpTable", SetLastError = true)]
        private static partial uint GetExtendedTCPTable
        (
            nint                                 pTcpTable,
            ref                             int  dwOutBufLen,
            [MarshalAs(UnmanagedType.Bool)] bool sort,
            int                                  ipVersion,
            int                                  tblClass,
            uint                                 reserved = 0
        );

        public void Dispose()
        {
            GameState.Instance().Login                       -= OnLogin;
            DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;

            Volatile.Write(ref needToRefreshAddress, 0);

            ipInfoCancelSource?.Cancel();
            ipInfoCancelSource?.Dispose();

            pingSender.Dispose();

            unsafe
            {
                if (buffer != null)
                {
                    NativeMemory.Free(buffer);
                    buffer = null;
                }
            }

            GC.SuppressFinalize(this);
        }

        ~ServerPingMonitor() =>
            Dispose();
        
        private readonly record struct ConnectionEndpoint
        (
            IPAddress Address,
            ushort    Port,
            int       Score
        );

        [StructLayout(LayoutKind.Sequential)]
        private struct TCPRow
        {
            public uint State;
            public uint LocalAddress;
            public uint LocalPort;
            public uint RemoteAddress;
            public uint RemotePort;
            public uint OwningPID;
        }

        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct TCP6Row
        {
            public fixed byte LocalAddress[16];
            public       uint LocalScopeID;
            public       uint LocalPort;
            public fixed byte RemoteAddress[16];
            public       uint RemoteScopeID;
            public       uint RemotePort;
            public       uint State;
            public       uint OwningPID;
        }
    }

    private class IPLocationDTO
    {
        [JsonProperty("status")]      public string? Status                  { get; set; }
        [JsonProperty("country")]     public string? CountryName             { get; set; }
        [JsonProperty("countryCode")] public string? CountryCode             { get; set; }
        [JsonProperty("region")]      public string? RegionCode              { get; set; }
        [JsonProperty("regionName")]  public string? RegionName              { get; set; }
        [JsonProperty("city")]        public string? CityName                { get; set; }
        [JsonProperty("zip")]         public string? ZipCode                 { get; set; }
        [JsonProperty("lat")]         public double? Latitude                { get; set; }
        [JsonProperty("lon")]         public double? Longitude               { get; set; }
        [JsonProperty("timezone")]    public string? TimeZone                { get; set; }
        [JsonProperty("isp")]         public string? InternetServiceProvider { get; set; }
        [JsonProperty("org")]         public string? Organization            { get; set; }
        [JsonProperty("as")]          public string? AutonomousSystem        { get; set; }
        [JsonProperty("query")]       public string? IPAddress               { get; set; }
    }
}
