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
    
    private const string COMMAND = "fps";

    private static Config ModuleConfig = null!;

    private static IDtrBarEntry? Entry;

    private static AddonDRBetterFPSLimitation? Addon;

    private static ushort NewThresholdInput = 120;

    protected override void Init()
    {
        ModuleConfig = Config.Load(this) ??
                       new()
                       {
                           Thresholds = [15, 30, 45, 60, 90, 120]
                       };
        
        Entry         ??= DService.Instance().DTRBar.Get("DailyRoutines-BetterFPSLimitation");
        Entry.OnClick =  _ =>
        {
            if (Addon == null)
            {
                var thresholdGroups = ModuleConfig.Thresholds
                                                  .Select((value, index) => new { value, index })
                                                  .GroupBy(x => x.index / 3)
                                                  .Select(g => g.Select(x => x.value).ToList())
                                                  .ToList();

                Addon = new()
                {
                    InternalName = "DRBetterFPSLimitation",
                    Title        = LuminaWrapper.GetAddonText(4032),
                    Size         = new(250f, 208f + 32f * thresholdGroups.Count)
                };
                Addon.SetWindowPosition(ModuleConfig.AddonPosition);
            }
                    
            Addon.Toggle();
        };
                
        Entry.Shown = true;
        Entry.Text  = LuminaWrapper.GetAddonText(4002);
        
        FrameworkManager.Instance().Reg(OnUpdate, 1_000);

        CommandManager.Instance().AddSubCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("BetterFPSLimitation-CommandHelp") });
    }

    protected override void Uninit()
    {
        CommandManager.Instance().RemoveSubCommand(COMMAND);

        FrameworkManager.Instance().Unreg(OnUpdate);
        
        Entry?.Remove();
        Entry = null;
        
        Addon?.Dispose();
        Addon = null;
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Command"));

        ImGui.TextUnformatted($"/pdr {COMMAND} → {Lang.Get("BetterFPSLimitation-CommandHelp")}");

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("BetterFPSLimitation-FastSetFPSLimitation"));

        using (ImRaii.PushIndent())
        {
            foreach (var threshold in ModuleConfig.Thresholds.ToList())
            {
                using var id = ImRaii.PushId(threshold);

                if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.TrashAlt, Lang.Get("Delete")))
                {
                    ModuleConfig.Thresholds.Remove(threshold);
                    continue;
                }

                ImGui.SameLine();
                ImGui.TextUnformatted($"{threshold}");
            }

            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, Lang.Get("Add")))
            {
                if (NewThresholdInput > 1               &&
                    NewThresholdInput <= short.MaxValue &&
                    !ModuleConfig.Thresholds.Contains((short)NewThresholdInput))
                {
                    ModuleConfig.Thresholds.Add((short)NewThresholdInput);
                    ModuleConfig.Save(this);
                }
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(100f * GlobalUIScale);
            if (ImGui.InputUShort("###NewThreshold", ref NewThresholdInput, 10, 10))
                NewThresholdInput = (ushort)Math.Clamp(NewThresholdInput, 1, short.MaxValue);
        }
    }

    private static void OnCommand(string command, string args) => Addon.Toggle();

    private static unsafe void OnUpdate(IFramework _)
    {
        Update();

        if (Entry == null) return;

        var text = DService.Instance().SeStringEvaluator.EvaluateFromAddon(4002, [(int)Framework.Instance()->FrameRate]).ToDalamudString();

        if (ModuleConfig.IsEnabled)
        {
            text = new SeStringBuilder()
                   .AddUiGlow(37)
                   .Append(text)
                   .AddUiGlowOff()
                   .Build();
        }

        Entry.Text = text;
    }

    private static unsafe void Update()
    {
        Device.Instance()->IsFrameRateLimited = ModuleConfig.IsEnabled;
        Device.Instance()->FrameRateLimit     = (short)MathF.Min(ModuleConfig.Limitation + 2, short.MaxValue);
    }

    private class Config : ModuleConfig
    {
        public Vector2 AddonPosition = new(800f, 350f);
        public bool    IsEnabled;
        public short   Limitation = 60;

        public List<short> Thresholds = [];
    }

    private class AddonDRBetterFPSLimitation : NativeAddon
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
                IsEnabledNode.IsChecked = ModuleConfig.IsEnabled;

            if (FPSInputNode != null)
                FPSInputNode.Value = ModuleConfig.Limitation;

            base.OnUpdate(addon);
        }

        protected override unsafe void OnFinalize(AtkUnitBase* addon)
        {
            ModuleConfig.AddonPosition = RootNode.Position;
            ModuleConfig.Save(ModuleManager.GetModule<BetterFPSLimitation>());

            base.OnFinalize(addon);
        }

        public static NodeBase CreateFPSWidget()
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
                IsChecked = ModuleConfig.IsEnabled,
                IsEnabled = true,
                String    = Lang.Get("Enable"),
                OnClick = newState =>
                {
                    ModuleConfig.IsEnabled = newState;
                    ModuleConfig.Save(ModuleManager.GetModule<BetterFPSLimitation>());

                    Update();
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
                    ModuleConfig.Limitation = (short)newValue;
                    ModuleConfig.Save(ModuleManager.GetModule<BetterFPSLimitation>());

                    Update();
                },
                Value = ModuleConfig.Limitation
            };

            FPSInputNode.Value = ModuleConfig.Limitation;
            FPSInputNode.ValueTextNode.SetNumber(ModuleConfig.Limitation);
            column.AddNode(FPSInputNode);
            totalHeight += FPSInputNode.Size.Y;

            var fpsDisplayColumn = new HorizontalFlexNode
            {
                Width          = Addon.Size.X,
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

            var thresholdGroups = ModuleConfig.Thresholds
                                              .Select((value, index) => new { value, index })
                                              .GroupBy(x => x.index / 3)
                                              .Select(g => g.Select(x => x.value).ToList())
                                              .ToList();

            foreach (var thresholds in thresholdGroups)
            {
                var fpsSetTable = new HorizontalFlexNode
                {
                    Width          = Addon.Size.X,
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
                            ModuleConfig.Limitation = threshold;
                            ModuleConfig.IsEnabled  = true;
                            ModuleConfig.Save(ModuleManager.GetModule<BetterFPSLimitation>());

                            FPSInputNode.Value = ModuleConfig.Limitation;
                            FPSInputNode.ValueTextNode.SetNumber(ModuleConfig.Limitation);

                            Update();
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
}
