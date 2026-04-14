using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Command;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using OmenTools.Info.Game.Enums;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class FastResetAllSDEnmity : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("FastResetAllSDEnmityTitle"),
        Description = Lang.Get("FastResetAllSDEnmityDescription"),
        Category    = ModuleCategory.Combat
    };

    private readonly CancellationTokenSource cancelSource = new();

    protected override void Init()
    {
        ExecuteCommandManager.Instance().RegPre(OnResetStrikingDummies);
        CommandManager.Instance().AddSubCommand
        (
            COMMAND,
            new CommandInfo(OnCommand)
            {
                HelpMessage = Lang.Get("FastResetAllSDEnmity-CommandHelp")
            }
        );
    }
    
    protected override void Uninit()
    {
        ExecuteCommandManager.Instance().Unreg(OnResetStrikingDummies);
        CommandManager.Instance().RemoveSubCommand(COMMAND);

        cancelSource.Cancel();
        cancelSource.Dispose();
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Command")}:");

        ImGui.SameLine();
        ImGui.TextUnformatted($"/pdr {COMMAND} → {Lang.Get("FastResetAllSDEnmity-CommandHelp")}");
    }

    private void OnCommand(string command, string arguments) => ResetAllStrikingDummies();

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
        DService.Instance().Framework.RunOnTick(FindAndResetInternal, TimeSpan.Zero,                   0, cancelSource.Token);
        DService.Instance().Framework.RunOnTick(FindAndResetInternal, TimeSpan.FromMilliseconds(500),  0, cancelSource.Token);
        DService.Instance().Framework.RunOnTick(FindAndResetInternal, TimeSpan.FromMilliseconds(1000), 0, cancelSource.Token);
        DService.Instance().Framework.RunOnTick(FindAndResetInternal, TimeSpan.FromMilliseconds(1500), 0, cancelSource.Token);
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
}
