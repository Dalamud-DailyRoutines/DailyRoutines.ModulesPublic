using System;
using System.Linq;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace DailyRoutines.ModulesPublic;

public unsafe class BetterBlueSetLoad : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("BetterBlueSetLoadTitle"),
        Description = GetLoc("BetterBlueSetLoadDescription"),
        Category    = ModuleCategories.UIOptimization,
    };

    private static Hook<AgentReceiveEventDelegate>? AgentAozNotebookReceiveEventHook;

    private const string Command = "blueset";

    protected override void Init()
    {
        AgentAozNotebookReceiveEventHook ??=
            DService.Hook.HookFromAddress<AgentReceiveEventDelegate>(
                GetVFuncByName(AgentModule.Instance()->GetAgentByInternalId(AgentId.AozNotebook)->VirtualTable, "ReceiveEvent"),
                AgentAozNotebookReceiveEventDetour);
        AgentAozNotebookReceiveEventHook.Enable();

        CommandManager.AddSubCommand(Command, new(OnCommand) { HelpMessage = GetLoc("BetterBlueSetLoad-CommandHelp") });
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("Command")}:");
        
        ImGui.SameLine();
        ImGui.Text($"/pdr {Command} → {GetLoc("BetterBlueSetLoad-CommandHelp")}");
    }

    private static AtkValue* AgentAozNotebookReceiveEventDetour(
        AgentInterface* agent,
        AtkValue*       returnvalues,
        AtkValue*       values,
        uint            valueCount,
        ulong           eventKind)
    {
        if (!IsAddonAndNodesReady(AOZNotebookPresetList) || AOZNotebookPresetList->AtkValues->UInt != 0 || eventKind != 1 || valueCount != 2)
            return InvokeOriginal();

        var index = values[1].UInt;
        if (values[1].Type != ValueType.UInt || index > 4) return InvokeOriginal();

        ApplyByIndex(index);

        using var returnValue = new AtkValueArray(false);
        return returnValue;

        AtkValue* InvokeOriginal() =>
            AgentAozNotebookReceiveEventHook.Original(agent, returnvalues, values, valueCount, eventKind);
    }

    private static void OnCommand(string command, string args)
    {
        args = args.Trim();
        if (string.IsNullOrEmpty(args)) return;

        if (uint.TryParse(args, out var setIndex) && setIndex < 5)
            ApplyByIndex(setIndex);
        else
        {
            var names = AozNoteModule.Instance()->ActiveSets.ToArray()
                                                            .Where(x => !string.IsNullOrWhiteSpace(x.CustomNameString))
                                                            .Select((value, index) => (Index: (uint)index, Name: value.CustomNameString))
                                                            .DistinctBy(x => x.Name)
                                                            .ToDictionary(x => x.Name, x => x.Index);
            if (!names.TryGetValue(args, out setIndex)) return;
            
            ApplyByIndex(setIndex);
        }
    }

    private static void ApplyByIndex(uint index)
    {
        if (index > 4) return;
        
        CompareAndApply((int)index);
        CompareAndApply((int)index);
        
        var setName = AozNoteModule.Instance()->ActiveSets[(int)index].CustomNameString;
        NotificationSuccess(GetLoc("BetterBlueSetLoad-Notification", index + 1) + 
                            (string.IsNullOrWhiteSpace(setName) ? string.Empty : $": {setName}"));
    }
    
    private static void CompareAndApply(int index)
    {
        if (index > 4) return;

        var blueModule    = AozNoteModule.Instance();
        var actionManager = ActionManager.Instance();

        Span<uint> presetActions = stackalloc uint[24];
        fixed (uint* actions = blueModule->ActiveSets[index].ActiveActions)
        {
            for (var i = 0; i < 24; i++)
            {
                var action = actions[i];
                if (action == 0) continue;

                presetActions[i] = action;
            }
        }

        Span<uint> currentActions = stackalloc uint[24];
        for (var i = 0; i < 24; i++)
        {
            var action = actionManager->GetActiveBlueMageActionInSlot(i);
            if (action == 0) continue;
            currentActions[i] = action;
        }

        Span<uint> finalActions = stackalloc uint[24];
        presetActions.CopyTo(finalActions);

        for (var i = 0; i < 24; i++)
        {
            if (finalActions[i] == 0) continue;
            for (var j = 0; j < 24; j++)
            {
                if (finalActions[i] == currentActions[j])
                {
                    actionManager->SwapBlueMageActionSlots(i, j);
                    finalActions[i] = 0;
                    break;
                }
            }
        }

        for (var i = 0; i < 24; i++)
        {
            var action = finalActions[i];
            if (action == 0) continue;
            actionManager->AssignBlueMageActionToSlot(i, action);
        }

        blueModule->LoadActiveSetHotBars(index);
    }
}
