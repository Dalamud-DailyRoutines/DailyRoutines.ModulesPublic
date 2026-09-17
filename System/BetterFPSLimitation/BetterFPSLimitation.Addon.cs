using DailyRoutines.Extensions;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic;

public partial class BetterFPSLimitation
{
    private AddonDRBetterFPSLimitation? addon;
    
    private void ToggleAddon()
    {
        if (addon is { IsOpen: true })
        {
            addon.Dispose();
            addon = null;
            return;
        }

        addon?.Dispose();
        addon = new(this)
        {
            InternalName          = "DRBetterFPSLimitation",
            Title                 = LuminaWrapper.GetAddonText(4032),
            Size                  = new(220f, 240f),
            RememberClosePosition = true
        };
        addon.Open();
    }

    private class AddonDRBetterFPSLimitation
    (
        BetterFPSLimitation module
    ) : NativeAddon
    {
        public VerticalListNode? FPSWidget;

        private TextNode?         FPSDisplayNumberNode;
        private NumericInputNode? FPSInputNode;
        private CheckboxNode?     IsEnabledNode;

        protected override unsafe void OnSetup
        (
            AtkUnitBase*   addon,
            Span<AtkValue> atkValues
        )
        {
            FPSWidget = new VerticalListNode
            {
                FitContents = true,
                Position    = ContentStartPosition
            };

            IsEnabledNode = new CheckboxNode
            {
                Size      = new(200f, 20f),
                IsChecked = module.config.IsEnabled,
                String    = Lang.Get("Enable"),
                OnClick = newState =>
                {
                    module.config.IsEnabled = newState;
                    module.config.Save(module);
                }
            };
            FPSWidget.AddNode(IsEnabledNode);

            FPSWidget.AddDummy(8f);

            var fpsLimitationTextNode = new TextNode
            {
                String   = Lang.Get("BetterFPSLimitation-MaxFPS"),
                FontSize = 14,
                Size     = new(200f, 28f),
            };
            FPSWidget.AddNode(fpsLimitationTextNode);

            FPSInputNode = new NumericInputNode
            {
                Size      = new(200f, 28f),
                Min       = 1,
                Max       = short.MaxValue,
                Step      = 10,
                OnValueUpdate = newValue =>
                {
                    module.config.Limitation = (short)newValue;
                    module.config.Save(module);
                },
                Value = module.config.Limitation
            };

            FPSInputNode.Value = module.config.Limitation;
            FPSInputNode.ValueTextNode.SetNumber(module.config.Limitation);
            FPSWidget.AddNode(FPSInputNode);

            var fpsDisplayColumn = new ResNode
            {
                Width  = 200f,
                Height = 28f,
            };

            var fpsDisplayTextNode = new TextNode
            {
                String        = Lang.Get("BetterFPSLimitation-CurrentFPS"),
                FontSize      = 12,
                Size          = new(20f, 25f),
                AlignmentType = AlignmentType.Left
            };
            fpsDisplayTextNode.AttachNode(fpsDisplayColumn);

            FPSDisplayNumberNode = new TextNode
            {
                String        = "0",
                FontSize      = 12,
                Size          = new(30f, 25f),
                X             = 180f,
                AlignmentType = AlignmentType.Right,
                TextFlags     = TextFlags.AutoAdjustNodeSize
            };
            FPSDisplayNumberNode.AttachNode(fpsDisplayColumn);

            FPSWidget.AddDummy(8f);
            FPSWidget.AddNode(fpsDisplayColumn);

            FPSWidget.AddDummy(8f);

            var fastSetTextNode = new TextNode
            {
                String        = Lang.Get("BetterFPSLimitation-FastSetFPSLimitation"),
                FontSize      = 14,
                Size          = new(200f, 20f),
                AlignmentType = AlignmentType.Left
            };
            FPSWidget.AddNode(fastSetTextNode);

            var thresholdGroups = module.config.Thresholds
                                        .Select((value, index) => new { value, index })
                                        .GroupBy(x => x.index / 3)
                                        .Select(g => g.Select(x => x.value).ToList())
                                        .ToList();

            foreach (var thresholds in thresholdGroups)
            {
                FPSWidget.AddDummy(8f);

                var fpsSetTable = new HorizontalFlexNode
                {
                    Width  = 200f,
                    Height = 28f,
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
                            module.config.Save(module);

                            FPSInputNode.Value = module.config.Limitation;
                            FPSInputNode.ValueTextNode.SetNumber(module.config.Limitation);
                        }
                    };

                    fpsSetTable.AddNode(button);
                }

                FPSWidget.AddNode(fpsSetTable);
            }

            FPSWidget.RecalculateLayout();

            FPSWidget.AttachNode(this);

            SetWindowSize(Size.X, FPSWidget.Height + ContentStartPosition.Y + 24f);
        }

        protected override unsafe void OnUpdate
        (
            AtkUnitBase* addon
        )
        {
            if (!Throttler.Shared.Throttle("BetterFPSLimitation.Addon.Update", 1000)) return;
            
            FPSDisplayNumberNode?.String = ISeStringEvaluator.Instance().EvaluateFromAddon(4002, [(int)MathF.Round(module.currentFPS)]);
            FPSDisplayNumberNode.Width   = FPSDisplayNumberNode.GetTextDrawSize(false).X;
            FPSDisplayNumberNode.X       = 200f - 6f - FPSDisplayNumberNode.Width;

            IsEnabledNode?.IsChecked = module.config.IsEnabled;
            FPSInputNode?.Value = module.config.Limitation;
        }
    }
}
