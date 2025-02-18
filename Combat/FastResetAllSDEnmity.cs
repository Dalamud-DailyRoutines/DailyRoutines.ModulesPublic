using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.Command;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using System;
using System.Threading;

namespace DailyRoutines.Modules;

public class FastResetAllSDEnmity : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("FastResetAllSDEnmityTitle"),
        Description = GetLoc("FastResetAllSDEnmityDescription"),
        Category = ModuleCategories.Combat,
    };

    private static CancellationTokenSource? CancelSource;
    
    private const string Command = "resetallsd";

    public override void Init()
    {
        CancelSource ??= new();

        ExecuteCommandManager.Register(OnResetStrikingDummies);
        CommandManager.AddSubCommand(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = GetLoc("FastResetAllSDEnmity-CommandHelp"),
        });
    }

    public override void ConfigUI()
    {
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("Command")}:");

        ImGui.SameLine();
        ImGui.Text($"/pdr {Command} → {GetLoc("FastResetAllSDEnmity-CommandHelp")}");
    }

    private static void OnCommand(string command, string arguments) => ResetAllStrikingDummies();

    public static void OnResetStrikingDummies(ref bool isPrevented, ref ExecuteCommandFlag command, ref int param1, ref int param2, ref int param3, ref int param4)
    {
        if (command != ExecuteCommandFlag.ResetStrikingDummy) return;
        isPrevented = true;

        ResetAllStrikingDummies();
    }

    private static void ResetAllStrikingDummies()
    {
        DService.Framework.RunOnTick(FindAndResetInternal, default, 0, CancelSource.Token);
        DService.Framework.RunOnTick(FindAndResetInternal, TimeSpan.FromMilliseconds(500), 0, CancelSource.Token);
        DService.Framework.RunOnTick(FindAndResetInternal, TimeSpan.FromMilliseconds(1000), 0, CancelSource.Token);
        DService.Framework.RunOnTick(FindAndResetInternal, TimeSpan.FromMilliseconds(1500), 0, CancelSource.Token);
    }

    private static unsafe void FindAndResetInternal()
    {
        var targets = UIState.Instance()->Hater.Haters;
        foreach (var targetID in targets)
            ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.ResetStrikingDummy, (int)targetID.EntityId);
    }

    public override void Uninit()
    {
        ExecuteCommandManager.Unregister(OnResetStrikingDummies);
        CommandManager.RemoveSubCommand(Command);

        CancelSource?.Cancel();
        CancelSource?.Dispose();
        CancelSource = null;
    }
}
