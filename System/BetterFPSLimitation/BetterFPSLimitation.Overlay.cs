using System.Numerics;
using DailyRoutines.Common.Extensions;
using DailyRoutines.Extensions;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public partial class BetterFPSLimitation
{
    private readonly float[] fpsHistory = new float[HISTORY_LENGTH];

    private int   fpsHistoryIndex;
    private int   fpsHistoryFilledCount;
    private float currentFPS;
    private float averageFPS;
    private float minFPS;
    private float maxFPS;

    protected override unsafe void OverlayUI()
    {
        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
        {
            Overlay.IsOpen = false;
            return;
        }

        var color = GetFPSColor(currentFPS);

        ImGui.SetWindowFontScale(1.5f);
        ImGui.TextColored(color, $"{currentFPS:F0}");
        ImGui.SetWindowFontScale(1.0f);

        ImGui.SameLine();
        ImGui.TextColored(color, "FPS");

        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.Spacing();

        ImGui.SameLine();

        using (var table = ImRaii.Table("##FPSStatsTable", 4, ImGuiTableFlags.SizingStretchProp))
        {
            if (table)
            {
                DrawStatColumn("AVG", $"{averageFPS:F0}",        GetFPSColor(averageFPS));
                DrawStatColumn("MIN", $"{minFPS:F0}",            GetFPSColor(minFPS));
                DrawStatColumn("MAX", $"{maxFPS:F0}",            GetFPSColor(maxFPS));
                DrawStatColumn("CAP", $"{config.Limitation:F0}", GetCapColor());
            }
        }

        using (ImRaii.PushColor(ImPlotCol.AxisBg, new Vector4(0.05f)))
        using (ImRaii.PushColor(ImPlotCol.FrameBg, Vector4.Zero))
        using (ImRaii.PushColor(ImPlotCol.AxisGrid, new Vector4(1f, 1f, 1f, 0.05f)))
        using (ImRaii.PushStyle(ImPlotStyleVar.FillAlpha, 0.25f))
        using (ImRaii.PushStyle(ImPlotStyleVar.LineWeight, 2f))
        using (var plot = ImRaii.Plot("##FPSPlot", new(-1), ImPlotFlags.CanvasOnly | ImPlotFlags.NoTitle))
        {
            if (!plot)
                return;

            const ImPlotAxisFlags AXIS_FLAGS = ImPlotAxisFlags.NoLabel | ImPlotAxisFlags.NoTickLabels;
            ImPlot.SetupAxes((byte*)null, (byte*)null, AXIS_FLAGS, AXIS_FLAGS);

            var yMax = MathF.Max(MathF.Max(maxFPS, config.Limitation) * 1.25f, 100f);
            ImPlot.SetupAxesLimits(0, fpsHistory.Length, 0, yMax, ImPlotCond.Always);

            ImPlot.SetupAxisTicks(ImAxis.X1, 0, fpsHistory.Length, 51);
            ImPlot.SetupAxisTicks(ImAxis.Y1, 0, yMax,              21);

            using (ImRaii.PushColor(ImPlotCol.Line, color)
                         .Push(ImPlotCol.Fill, color))
                ImPlot.PlotLine("##FPS", ref fpsHistory[0], fpsHistory.Length, 1.0, 0.0, ImPlotLineFlags.Shaded, fpsHistoryIndex);

            if (averageFPS <= 0)
                return;

            var avgColor = KnownColor.White.ToVector4() with { W = 0.6f };
            var xs       = new double[] { 0, fpsHistory.Length };
            var ys       = new double[] { averageFPS, averageFPS };

            using (ImRaii.PushColor(ImPlotCol.Line, avgColor))
                ImPlot.PlotLine("##FPSAvg", ref xs[0], ref ys[0], 2);
        }
    }

    private void EnsureOverlay()
    {
        if (Overlay != null)
            return;

        Overlay       =  new(this);
        Overlay.Flags &= ~ImGuiWindowFlags.AlwaysAutoResize;
        Overlay.SizeConstraints = new()
        {
            MinimumSize = ScaledVector2(300f, 200f)
        };
    }

    private void ResetHistory()
    {
        Array.Clear(fpsHistory);
        fpsHistoryIndex       = 0;
        fpsHistoryFilledCount = 0;
        currentFPS            = 0f;
        averageFPS            = 0f;
        minFPS                = 0f;
        maxFPS                = 0f;
    }

    private void RecordFPS
    (
        float fps
    )
    {
        fpsHistory[fpsHistoryIndex] = fps;
        fpsHistoryIndex             = (fpsHistoryIndex + 1) % fpsHistory.Length;

        if (fpsHistoryFilledCount < fpsHistory.Length)
            fpsHistoryFilledCount++;

        if (fpsHistoryFilledCount == 0)
        {
            averageFPS = 0f;
            minFPS     = 0f;
            maxFPS     = 0f;
            return;
        }

        var min = float.MaxValue;
        var max = 0f;
        var sum = 0f;

        for (var i = 0; i < fpsHistoryFilledCount; i++)
        {
            var value            = fpsHistory[i];
            if (value < min) min = value;
            if (value > max) max = value;
            sum += value;
        }

        averageFPS = sum / fpsHistoryFilledCount;
        minFPS = min == float.MaxValue ?
                     0f :
                     min;
        maxFPS = max;
    }

    private Vector4 GetFPSColor
    (
        float fps
    )
    {
        if (config.IsEnabled)
        {
            if (config.Limitation <= 0)
                return KnownColor.Gray.ToVector4();

            var ratio = fps / config.Limitation;
            return ratio switch
            {
                >= 0.95f => KnownColor.SpringGreen.ToVector4(),
                >= 0.75f => KnownColor.Orange.ToVector4(),
                _        => KnownColor.Red.ToVector4()
            };
        }

        return fps switch
        {
            >= 60f => KnownColor.SpringGreen.ToVector4(),
            >= 30f => KnownColor.Orange.ToVector4(),
            _      => KnownColor.Red.ToVector4()
        };
    }

    private Vector4 GetCapColor() =>
        config.IsEnabled ?
            KnownColor.SpringGreen.ToVector4() :
            KnownColor.Gray.ToVector4();

    private static void DrawStatColumn
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
        using (FontManager.Instance().UIFont120.Push())
            ImGui.TextColored(color, value);
    }

    #region 常量

    private const int HISTORY_LENGTH = 100;

    #endregion
}
