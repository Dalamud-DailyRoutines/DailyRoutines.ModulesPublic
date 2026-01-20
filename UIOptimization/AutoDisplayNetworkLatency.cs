using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DailyRoutines.Abstracts;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;

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

        float min        = 9999f, max = 0f, sum = 0f;
        var   validCount = 0;

        foreach (var val in Monitor.History)
        {
            if (val <= 0.1f)
                continue;

            if (val < min) min = val;
            if (val > max) max = val;
            sum += val;
            validCount++;
        }

        var avg = validCount > 0 ? sum / validCount : 0f;
        if (min == 9999f)
            min = 0f;

        var currentPing = Monitor.LastPing;
        var color = currentPing switch
        {
            < 0   => KnownColor.Gray.ToVector4(),
            < 100 => KnownColor.SpringGreen.ToVector4(),
            < 200 => KnownColor.Orange.ToVector4(),
            _     => KnownColor.Red.ToVector4()
        };

        ImGui.SetWindowFontScale(1.5f);
        ImGui.TextColored(color, $"{currentPing}");
        ImGui.SetWindowFontScale(1.0f);

        ImGui.SameLine();
        ImGui.TextColored(color, "ms");

        ImGui.SameLine();
        var ipText = $"{Monitor.ServerAddress}:{Monitor.ServerPort}";
        var ipSize = ImGui.CalcTextSize(ipText);
        var availX = ImGui.GetContentRegionAvail().X;
        if (availX > ipSize.X)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + availX - ipSize.X);
        ImGui.TextDisabled(ipText);

        ImGui.Spacing();

        ImGui.Dummy(ImGui.GetStyle().ItemSpacing);

        ImGui.SameLine();

        using (var table = ImRaii.Table("##StatsTable", 3, ImGuiTableFlags.SizingStretchProp))
        {
            if (table)
            {
                DrawStatColumn("AVG", $"{avg:F0}", KnownColor.White.ToVector4());
                DrawStatColumn("MIN", $"{min:F0}", KnownColor.SpringGreen.ToVector4());
                DrawStatColumn("MAX", $"{max:F0}", KnownColor.Orange.ToVector4());
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

        static void DrawStatColumn(string label, string value, Vector4 color)
        {
            ImGui.TableNextColumn();
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

                var currentPing = Monitor.LastPing;
                var address     = Monitor.ServerAddress;
                var port        = Monitor.ServerPort;

                await DService.Instance().Framework.RunOnTick
                (() =>
                    {
                        if (Entry == null || CancelSource.IsCancellationRequested) return;

                        var isConnected = currentPing != -1;
                        if (Entry.Shown != isConnected)
                            Entry.Shown = isConnected;

                        if (!isConnected) return;

                        if (lastPing != currentPing)
                        {
                            Entry.Text = string.Format(ModuleConfig.Format, currentPing);
                            lastPing   = currentPing;
                        }

                        Entry.Tooltip = new SeStringBuilder().AddIcon(BitmapFontIcon.Meteor)
                                                             .AddText($"{address}:{port}")
                                                             .Build();
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

    public partial class ServerPingMonitor : IDisposable
    {
        private readonly Ping  pingSender = new();
        private unsafe   byte* buffer;
        private          int   bufferSize;

        public IPAddress ServerAddress { get; private set; } = IPAddress.Loopback;
        public ushort    ServerPort    { get; private set; }

        public long LastPing { get; private set; } = -1;

        public float[] History = new float[100];
        public int     HistoryIndex;

        private const int AF_INET                         = 2;
        private const int TCP_TABLE_OWNER_PID_CONNECTIONS = 4;
        private const int MIB_TCP_STATE_ESTABLISHED       = 5;

        public async Task UpdateAsync()
        {
            try
            {
                UpdateAddressInfo();

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
            }
            catch
            {
                LastPing              = -1;
                History[HistoryIndex] = 0;
                HistoryIndex          = (HistoryIndex + 1) % History.Length;
            }
        }

        private unsafe void UpdateAddressInfo()
        {
            var requiredSize = 0;
            GetExtendedTCPTable(nint.Zero, ref requiredSize, false, AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS);

            if (bufferSize < requiredSize)
            {
                if (buffer != null) NativeMemory.Free(buffer);
                bufferSize = requiredSize;
                buffer     = (byte*)NativeMemory.Alloc((nuint)bufferSize);
            }

            try
            {
                if (GetExtendedTCPTable((nint)buffer, ref requiredSize, false, AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS) != 0)
                {
                    ResetAddress();
                    return;
                }

                var numEntries = Unsafe.Read<int>(buffer);
                var rowPtr     = (TCPRow*)(buffer + sizeof(int));
                var currentPID = (uint)Environment.ProcessId;

                for (var i = 0; i < numEntries; i++)
                {
                    var row = rowPtr[i];

                    if (row.OwningPID == currentPID && row.State == MIB_TCP_STATE_ESTABLISHED)
                    {
                        if (row.RemoteAddress == 0x0100007F) // 127.0.0.1
                            continue;

                        var port = BinaryPrimitives.ReverseEndianness((ushort)row.RemotePort);

                        if (InXIVPortRange(port))
                        {
                            var newAddress = new IPAddress(row.RemoteAddress);
                            if (!newAddress.Equals(ServerAddress))
                                ServerAddress = newAddress;

                            ServerPort = port;
                            return;
                        }
                    }
                }

                ResetAddress();
            }
            catch
            {
                ResetAddress();
            }
        }

        private void ResetAddress()
        {
            if (!ServerAddress.Equals(IPAddress.Loopback))
                ServerAddress = IPAddress.Loopback;
            ServerPort = 0;
        }

        private static bool InXIVPortRange(ushort port) =>
            port is >= 54992 and <= 54994
                or >= 55006 and <= 55007
                or >= 55021 and <= 55040
                or >= 55296 and <= 55551;

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

        public void Dispose()
        {
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
    }
}
