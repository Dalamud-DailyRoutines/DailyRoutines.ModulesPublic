using DailyRoutines.Abstracts;

using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System;
using Dalamud.Hooking;


namespace DailyRoutines.ModulesPublic;

public unsafe class AutoReopenLFG : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("AutoReopenLFGTitle", "自动重开招募板"),
        Description = GetLoc("AutoReopenLFGDescription", "在小队构成发生变化时自动重新打开招募板"),
        Category = ModuleCategories.UIOptimization,
    };

    private static readonly CompSig PrintMessageSig = new("E8 ?? ?? ?? ?? 45 84 F6 75 21");
    private delegate uint PrintMessageDelegate(
        nint raptureLogModule,
        ushort logKindId,
        nint senderName,
        nint message,
        int timestamp,
        byte silent);
    private Hook<PrintMessageDelegate>? printMessageHook;

    public override void Init()
    {
        TaskHelper ??= new TaskHelper { TimeLimitMS = 1_500 };
        printMessageHook = PrintMessageSig.GetHook<PrintMessageDelegate>(PrintMessageDetour);
        printMessageHook.Enable();
    }

    private uint PrintMessageDetour(
        nint raptureLogModule,
        ushort logKindId,
        nint senderName,
        nint message,
        int timestamp,
        byte silent)
    {
        var result = printMessageHook!.Original(raptureLogModule, logKindId, senderName, message, timestamp, silent);

        try
        {
            // 只处理小队构成发生变化的事件
            if (logKindId == 0x3C && !(TaskHelper?.IsBusy ?? false))
            {
                TaskHelper?.Enqueue(() =>
                {
                    AgentModule.Instance()->GetAgentByInternalId(AgentId.LookingForGroup)->Show();
                }, "OpenLFGWindow");
            }
        }
        catch (Exception e)
        {
            DService.Log.Error(e, "[AutoReopenLFG] Error in processing messages");
        }

        return result;
    }

    public override void Uninit()
    {
        printMessageHook?.Disable();
        printMessageHook?.Dispose();

        TaskHelper?.Abort();

        base.Uninit();
    }
}