using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Component.Shell;
using OmenTools.Info.Lumina;
using OmenTools.Interop.Game.Models;

namespace DailyRoutines.ModulesPublic;

public unsafe class BetterPVPACCommand : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("BetterPVPACCommandTitle"),
        Description = Lang.Get("BetterPVPACCommandDescription"),
        Category    = ModuleCategory.Action,
        ModulesPair = ["OptimizedPVPProfileAction"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static readonly CompSig ShellCommandActionExecuteCommandSig = new("40 57 41 55 41 56 48 83 EC ?? 45 32 ED");
    private delegate long ExecuteCommandDelegate
    (
        ShellCommandInterface*                commandInterface,
        ShellCommandInterface.CommandContext* context,
        void*                                 source
    );
    private Hook<ExecuteCommandDelegate>? executeCommandHook;
    
    private readonly Dictionary<string, uint> actionIDsByName = [];
    
    protected override void Init()
    {
        actionIDsByName.Clear();
        foreach (var action in Sheets.PVPActions.Values)
        {
            var name = action.Name.ToString();
            if (string.IsNullOrEmpty(name))
                continue;

            actionIDsByName.TryAdd(name, action.RowId);
        }
        
        executeCommandHook = ShellCommandActionExecuteCommandSig.GetHook<ExecuteCommandDelegate>(ExecuteCommandDetour);
        executeCommandHook.Enable();
    }

    private long ExecuteCommandDetour
    (
        ShellCommandInterface*                commandInterface,
        ShellCommandInterface.CommandContext* context,
        void*                                 source
    )
    {
        if (context != null && context->TextCommandId == PVP_ACTION_COMMAND_ID)
            ForcePvPAction(context);

        return executeCommandHook.Original(commandInterface, context, source);
    }

    private void ForcePvPAction(ShellCommandInterface.CommandContext* context)
    {
        var argPtr  = (int*)((byte*)context + COMMAND_ARG0_OFFSET);
        var encoded = *argPtr;

        var name = context->StringArgs.First != null && context->StringArgs.First != context->StringArgs.Last
                       ? context->StringArgs.First->ToString()
                       : null;

        uint actionID;
        if (encoded is -1 or -2)
        {
            if (name is null || !actionIDsByName.TryGetValue(name, out actionID))
                return;
        }
        else
            actionID = (uint)(encoded & 0xFFFFFF);

        if (actionID == 0)
            return;

        // 来源索引 3: ActionType = 1, 且 usabilityFlag = 0
        *argPtr = (3 << 24) | (int)(actionID & 0xFFFFFF);
    }

    #region 常量

    private const ushort PVP_ACTION_COMMAND_ID = 277;
    private const int    COMMAND_ARG0_OFFSET   = 0x08;

    #endregion
}
