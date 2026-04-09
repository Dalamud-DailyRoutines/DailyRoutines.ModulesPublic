using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class BetterFPSLimitation : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("BetterFPSLimitationTitle"),
        Description = Lang.Get("BetterFPSLimitationDescription"),
        Category    = ModuleCategory.System
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private Config                      config = null!;
    private IDtrBarEntry?               entry;
    private AddonDRBetterFPSLimitation? addon;

    private readonly float[] fpsHistory = new float[HISTORY_LENGTH];

    private int   fpsHistoryIndex;
    private int   fpsHistoryFilledCount;
    private float currentFPS;
    private float averageFPS;
    private float minFPS;
    private float maxFPS;

    private ushort newThresholdInput = 120;

    protected override void Init()
    {
        config = Config.Load(this) ??
                       new()
                       {
                           Thresholds = [15, 30, 45, 60, 90, 120]
                       };
        ResetHistory();

        entry ??= DService.Instance().DTRBar.Get("DailyRoutines-BetterFPSLimitation");
        entry.OnClick = param =>
        {
            switch (param.ClickType)
            {
                case MouseClickType.Left:
                    EnsureAddon();
                    addon.Toggle();
                    break;
                case MouseClickType.Right:
                    EnsureOverlay();
                    Overlay?.Toggle();
                    break;
            }
        };

        entry.Shown   = true;
        entry.Text    = LuminaWrapper.GetAddonText(4002);
        entry.Tooltip = Lang.Get("BetterFPSLimitation-DTR-Tooltip");

        FrameworkManager.Instance().Reg(OnUpdate, 1_000);

        CommandManager.Instance().AddSubCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("BetterFPSLimitation-CommandHelp") });
    }

    protected override void Uninit()
    {
        CommandManager.Instance().RemoveSubCommand(COMMAND);

        FrameworkManager.Instance().Unreg(OnUpdate);

        entry?.Remove();
        entry = null;

        addon?.Dispose();
        addon = null;

        ResetHistory();
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Command"));

        ImGui.TextUnformatted($"/pdr {COMMAND} → {Lang.Get("BetterFPSLimitation-CommandHelp")}");

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("BetterFPSLimitation-FastSetFPSLimitation"));

        using (ImRaii.PushIndent())
        {
            foreach (var threshold in config.Thresholds.ToList())
            {
                using var id = ImRaii.PushId(threshold);

                if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.TrashAlt, Lang.Get("Delete")))
                {
                    config.Thresholds.Remove(threshold);
                    continue;
                }

                ImGui.SameLine();
                ImGui.TextUnformatted($"{threshold}");
            }

            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, Lang.Get("Add")))
            {
                if (newThresholdInput > 1               &&
                    newThresholdInput <= short.MaxValue &&
                    !config.Thresholds.Contains((short)newThresholdInput))
                {
                    config.Thresholds.Add((short)newThresholdInput);
                    config.Save(this);
                }
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(100f * GlobalUIScale);
            if (ImGui.InputUShort("###NewThreshold", ref newThresholdInput, 10, 10))
                newThresholdInput = (ushort)Math.Clamp(newThresholdInput, 1, short.MaxValue);
        }
    }

    protected override unsafe void OverlayUI()
    {
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
                DrawStatColumn("AVG", $"{averageFPS:F0}",              GetFPSColor(averageFPS));
                DrawStatColumn("MIN", $"{minFPS:F0}",                  GetFPSColor(minFPS));
                DrawStatColumn("MAX", $"{maxFPS:F0}",                  GetFPSColor(maxFPS));
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

    private void OnCommand(string command, string args)
    {
        EnsureAddon();
        addon.Toggle();
    }

    private unsafe void OnUpdate(IFramework _)
    {
        Update();

        if (entry == null) return;

        currentFPS = Math.Max(Framework.Instance()->FrameRate, 0f);
        RecordFPS(currentFPS);

        var text = DService.Instance().SeStringEvaluator.EvaluateFromAddon(4002, [(int)currentFPS]).ToDalamudString();

        if (config.IsEnabled)
        {
            text = new SeStringBuilder()
                   .AddUiGlow(37)
                   .Append(text)
                   .AddUiGlowOff()
                   .Build();
        }

        entry.Text = text;
    }

    private unsafe void Update()
    {
        Device.Instance()->IsFrameRateLimited = config.IsEnabled;
        Device.Instance()->FrameRateLimit     = (short)MathF.Min(config.Limitation + 2, short.MaxValue);
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

    private void EnsureAddon()
    {
        if (addon != null)
            return;

        var thresholdGroups = config.Thresholds
                                          .Select((value, index) => new { value, index })
                                          .GroupBy(x => x.index / 3)
                                          .Select(g => g.Select(x => x.value).ToList())
                                          .ToList();
        
        addon = new(this)
        {
            InternalName = "DRBetterFPSLimitation",
            Title        = LuminaWrapper.GetAddonText(4032),
            Size         = new(250f, 208f + 32f * thresholdGroups.Count)
        };
        addon.SetWindowPosition(config.AddonPosition);
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

    private void RecordFPS(float fps)
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
        minFPS     = min == float.MaxValue ? 0f : min;
        maxFPS     = max;
    }

    private Vector4 GetFPSColor(float fps)
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
        config.IsEnabled
            ? KnownColor.SpringGreen.ToVector4()
            : KnownColor.Gray.ToVector4();

    private static void DrawStatColumn(string label, string value, Vector4 color)
    {
        ImGui.TableNextColumn();
        ImGui.Spacing();
        ImGui.TextDisabled(label);

        ImGui.SameLine(0, 8f * GlobalUIScale);
        using (FontManager.Instance().UIFont120.Push())
            ImGui.TextColored(color, value);
    }

    private class Config : ModuleConfig
    {
        public Vector2 AddonPosition = new(800f, 350f);
        public bool    IsEnabled;
        public short   Limitation = 60;

        public List<short> Thresholds = [];
    }

    private class AddonDRBetterFPSLimitation(BetterFPSLimitation module) : NativeAddon
    {
        public static NodeBase FPSWidget;

        private static TextNode         FPSDisplayNumberNode;
        private static NumericInputNode FPSInputNode;
        private static CheckboxNode     IsEnabledNode;

        protected override unsafe void OnSetup(AtkUnitBase* addon)
        {
            FPSWidget          = CreateFPSWidget();
            FPSWidget.Position = ContentStartPosition;

            FPSWidget.AttachNode(this);

            Size = Size with { Y = FPSWidget.Height + 65 };

            base.OnSetup(addon);
        }

        protected override unsafe void OnUpdate(AtkUnitBase* addon)
        {
            if (FPSDisplayNumberNode != null)
            {
                var text = LuminaGetter.GetRow<Addon>(4002).GetValueOrDefault().Text.ToDalamudString();
                text.Payloads[0]            = new TextPayload($"{Framework.Instance()->FrameRate:F0}");
                FPSDisplayNumberNode.String = text.Encode();
            }

            if (IsEnabledNode != null)
                IsEnabledNode.IsChecked = module.config.IsEnabled;

            if (FPSInputNode != null)
                FPSInputNode.Value = module.config.Limitation;

            base.OnUpdate(addon);
        }

        protected override unsafe void OnFinalize(AtkUnitBase* addon)
        {
            module.config.AddonPosition = RootNode.Position;
            module.config.Save(ModuleManager.Instance().GetModule<BetterFPSLimitation>());

            base.OnFinalize(addon);
        }

        public NodeBase CreateFPSWidget()
        {
            var column = new VerticalListNode
            {
                IsVisible = true
            };
            var totalHeight = 0f;

            IsEnabledNode = new CheckboxNode
            {
                Size      = new Vector2(150.0f, 20.0f),
                IsVisible = true,
                IsChecked = module.config.IsEnabled,
                IsEnabled = true,
                String    = Lang.Get("Enable"),
                OnClick = newState =>
                {
                    module.config.IsEnabled = newState;
                    module.config.Save(ModuleManager.Instance().GetModule<BetterFPSLimitation>());

                    module.Update();
                }
            };
            column.AddNode(IsEnabledNode);
            totalHeight += IsEnabledNode.Size.Y;

            var spacer0 = new ResNode { Size = new(0, 8), IsVisible = true };
            column.AddNode(spacer0);
            totalHeight += spacer0.Size.Y;

            var fpsLimitationTextNode = new TextNode
            {
                String        = Lang.Get("BetterFPSLimitation-MaxFPS"),
                FontSize      = 14,
                IsVisible     = true,
                Size          = new(150f, 25f),
                AlignmentType = AlignmentType.Left
            };
            column.AddNode(fpsLimitationTextNode);
            totalHeight += fpsLimitationTextNode.Size.Y;

            FPSInputNode = new NumericInputNode
            {
                Size      = new(200.0f, 28.0f),
                IsVisible = true,
                Min       = 1,
                Max       = short.MaxValue,
                Step      = 10,
                OnValueUpdate = newValue =>
                {
                    module.config.Limitation = (short)newValue;
                    module.config.Save(ModuleManager.Instance().GetModule<BetterFPSLimitation>());

                    module.Update();
                },
                Value = module.config.Limitation
            };

            FPSInputNode.Value = module.config.Limitation;
            FPSInputNode.ValueTextNode.SetNumber(module.config.Limitation);
            column.AddNode(FPSInputNode);
            totalHeight += FPSInputNode.Size.Y;

            var fpsDisplayColumn = new HorizontalFlexNode
            {
                Width          = module.addon.Size.X,
                IsVisible      = true,
                AlignmentFlags = FlexFlags.FitContentHeight
            };

            var fpsDisplayTextNode = new TextNode
            {
                String        = Lang.Get("BetterFPSLimitation-CurrentFPS"),
                FontSize      = 12,
                IsVisible     = true,
                Size          = new(20f, 25f),
                AlignmentType = AlignmentType.Left
            };
            fpsDisplayColumn.AddNode(fpsDisplayTextNode);

            FPSDisplayNumberNode = new TextNode
            {
                String        = "0",
                FontSize      = 12,
                IsVisible     = true,
                Size          = new(30f, 25f),
                AlignmentType = AlignmentType.Center,
                TextFlags     = TextFlags.AutoAdjustNodeSize
            };
            fpsDisplayColumn.AddNode(FPSDisplayNumberNode);

            column.AddNode(fpsDisplayColumn);
            totalHeight += fpsDisplayColumn.Size.Y;

            var spacer1 = new ResNode { Size = new(0, 8), IsVisible = true };
            column.AddNode(spacer1);
            totalHeight += spacer1.Size.Y;

            var fastSetTextNode = new TextNode
            {
                String        = Lang.Get("BetterFPSLimitation-FastSetFPSLimitation"),
                FontSize      = 14,
                IsVisible     = true,
                Size          = new(150f, 20f),
                AlignmentType = AlignmentType.Left
            };
            column.AddNode(fastSetTextNode);
            totalHeight += fastSetTextNode.Size.Y;

            var spacer2 = new ResNode { Size = new(0, 8), IsVisible = true };
            column.AddNode(spacer2);
            totalHeight += spacer2.Size.Y;

            var thresholdGroups = module.config.Thresholds
                                        .Select((value, index) => new { value, index })
                                        .GroupBy(x => x.index / 3)
                                        .Select(g => g.Select(x => x.value).ToList())
                                        .ToList();

            foreach (var thresholds in thresholdGroups)
            {
                var fpsSetTable = new HorizontalFlexNode
                {
                    Width          = module.addon.Size.X,
                    IsVisible      = true,
                    AlignmentFlags = FlexFlags.FitContentHeight
                };

                foreach (var threshold in thresholds)
                {
                    var button = new TextButtonNode
                    {
                        Size      = new(60f, 25f),
                        IsVisible = true,
                        String    = threshold.ToString(),
                        OnClick = () =>
                        {
                            module.config.Limitation = threshold;
                            module.config.IsEnabled  = true;
                            module.config.Save(ModuleManager.Instance().GetModule<BetterFPSLimitation>());

                            FPSInputNode.Value = module.config.Limitation;
                            FPSInputNode.ValueTextNode.SetNumber(module.config.Limitation);

                            module.Update();
                        }
                    };

                    fpsSetTable.AddNode(button);
                }

                column.AddNode(fpsSetTable);
                totalHeight += fpsSetTable.Size.Y;

                var spacerFastSet = new ResNode { Size = new(0, 8), IsVisible = true };

                column.AddNode(spacerFastSet);
                totalHeight += spacerFastSet.Size.Y;
            }

            column.Size = new(150f, totalHeight);
            return column;
        }
    }
    
    #region 常量

    private const string COMMAND        = "fps";
    private const int    HISTORY_LENGTH = 100;

    #endregion
}
