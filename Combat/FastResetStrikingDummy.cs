using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Windows.Models;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class FastResetStrikingDummy : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("FastResetStrikingDummyTitle"),
        Description = Lang.Get("FastResetStrikingDummyDescription", COMMAND),
        Category    = ModuleCategory.Combat
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private readonly CancellationTokenSource cancelSource = new();

    private Config config = null!;

    private bool isAlreadyRequest;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        DService.Instance().Condition.ConditionChange += OnConditionChanged;

        ExecuteCommandManager.Instance().RegPre(OnResetStrikingDummies);
        CommandManager.Instance().AddSubCommand
        (
            COMMAND,
            new CommandInfo(OnCommand)
            {
                HelpMessage = Lang.Get("FastResetStrikingDummy-CommandHelp")
            }
        );

        _ = Task.Run(AutoClearLoop);
    }

    protected override void Uninit()
    {
        DService.Instance().Condition.ConditionChange -= OnConditionChanged;

        ExecuteCommandManager.Instance().Unreg(OnResetStrikingDummies);
        CommandManager.Instance().RemoveSubCommand(COMMAND);

        cancelSource.Cancel();
        cancelSource.Dispose();
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Command")}");

        using (ImRaii.PushIndent())
            ImGui.TextUnformatted($"/pdr {COMMAND} → {Lang.Get("FastResetStrikingDummy-CommandHelp")}");

        ImGui.NewLine();

        if (ImGui.Checkbox(Lang.Get("FastResetStrikingDummy-AutoClearEnmityWhenInactive"), ref config.IsAutoClearEnmityWhenInactive))
            config.Save(this);

        ImGuiOm.HelpMarker(Lang.Get("FastResetStrikingDummy-AutoClearEnmityWhenInactive-Help"));

        if (config.IsAutoClearEnmityWhenInactive)
        {
            ImGui.SetNextItemWidth(200f * GlobalUIScale);
            if (ImGui.InputUInt(Lang.Get("FastResetStrikingDummy-AutoClearEnmityInterval"), ref config.AutoClearEnmityInterval))
                config.AutoClearEnmityInterval = Math.Clamp(config.AutoClearEnmityInterval, 5, 300);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);
        }
    }

    private void OnCommand(string command, string arguments) =>
        ResetAllStrikingDummies();

    public void OnResetStrikingDummies
    (
        ref bool               isPrevented,
        ref ExecuteCommandFlag command,
        ref uint               param1,
        ref uint               param2,
        ref uint               param3,
        ref uint               param4
    )
    {
        if (command != ExecuteCommandFlag.ResetStrikingDummy) return;
        isPrevented = true;

        ResetAllStrikingDummies();
    }

    private void ResetAllStrikingDummies()
    {
        DService.Instance().Framework.RunOnTick(FindAndResetInternal, TimeSpan.Zero,                   cancellationToken: cancelSource.Token);
        DService.Instance().Framework.RunOnTick(FindAndResetInternal, TimeSpan.FromMilliseconds(500),  cancellationToken: cancelSource.Token);
        DService.Instance().Framework.RunOnTick(FindAndResetInternal, TimeSpan.FromMilliseconds(1000), cancellationToken: cancelSource.Token);
    }

    private static unsafe void FindAndResetInternal()
    {
        var targets = UIState.Instance()->Hater.Haters;
        foreach (var targetID in targets)
            ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.ResetStrikingDummy, targetID.EntityId);
    }

    #region 常量

    private const string COMMAND = "resetallsd";

    #endregion

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.InCombat || !value) return;
        isAlreadyRequest = false;
    }

    private async Task AutoClearLoop()
    {
        try
        {
            while (!cancelSource.IsCancellationRequested)
            {
                await Task.Delay(1_000, cancelSource.Token).ConfigureAwait(false);

                if (isAlreadyRequest)
                    continue;
                if (!config.IsAutoClearEnmityWhenInactive ||
                    !DService.Instance().Condition[ConditionFlag.InCombat])
                    continue;
                if (LastInputInfo.GetIdleTimeTick() <= config.AutoClearEnmityInterval * 1_000 &&
                    GameState.IsForeground)
                    continue;

                isAlreadyRequest = true;
                ResetAllStrikingDummies();
            }
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
    }

    private class Config : ModuleConfig
    {
        public bool IsAutoClearEnmityWhenInactive;
        public uint AutoClearEnmityInterval = 30;
    }
}
