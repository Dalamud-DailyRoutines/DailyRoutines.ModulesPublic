using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public partial class BetterFPSLimitation : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("BetterFPSLimitationTitle"),
        Description = Lang.Get("BetterFPSLimitationDescription"),
        Category    = ModuleCategory.System
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private Config        config = null!;
    private IDtrBarEntry? entry;
    private FramePacer?   pacer;

    private ushort newThresholdInput = 120;

    protected override unsafe void Init()
    {
        config = Config.Load(this) ??
                 new()
                 {
                     Thresholds = [15, 30, 45, 60, 90, 120]
                 };

        pacer = new FramePacer(this);

        entry ??= IDtrBar.Instance().Get("DailyRoutines-BetterFPSLimitation");
        entry.OnClick = param =>
        {
            switch (param.ClickType)
            {
                case MouseClickType.Left:
                    ToggleAddon();
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

        CommandManager.Instance().AddSubCommand
        (
            COMMAND,
            new(OnCommand)
            {
                HelpMessage = Lang.Get("BetterFPSLimitation-CommandHelp")
            }
        );
    }

    protected override void Uninit()
    {
        CommandManager.Instance().RemoveSubCommand(COMMAND);

        FrameworkManager.Instance().Unreg(OnUpdate);

        entry?.Remove();
        entry = null;

        addon?.Dispose();
        addon = null;

        pacer?.Dispose();
        pacer = null!;

        ResetHistory();
    }

    protected override void ConfigUI()
    {
        using (ImRaii.Heading1(Lang.Get("Command")))
            ImGui.TextUnformatted($"/pdr {COMMAND} {Lang.Get("BetterFPSLimitation-CommandHelp")}");

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

    private void OnCommand
    (
        string command,
        string args
    ) =>
        ToggleAddon();

    private unsafe void OnUpdate
    (
        IFramework _
    )
    {
        if (entry == null) return;

        currentFPS = pacer.FPS >= 1 ? pacer.FPS : 1;
        RecordFPS(currentFPS);

        var text = ISeStringEvaluator.Instance().EvaluateFromAddon(4002, [(int)MathF.Round(currentFPS)]);
        
        if (config.IsEnabled)
        {
            using var rented  = new RentedSeStringBuilder();
            var       builder = rented.Builder;
            
            text = builder
                   .PushEdgeColorType(37)
                   .Append(text)
                   .PopEdgeColorType()
                   .ToReadOnlySeString();
        }

        entry.Text = text.ToDalamudString();
    }

    private class Config : ModuleConfig
    {
        public bool  IsEnabled;
        public short Limitation = 60;

        public List<short> Thresholds = [];
    }

    #region 常量

    private const string COMMAND = "fps";

    #endregion
}
