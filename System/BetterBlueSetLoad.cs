using DailyRoutines.Common.KamiToolKit.Addons.SelectYesno;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.Interop.Game.Models.Native;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class BetterBlueSetLoad : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("BetterBlueSetLoadTitle"),
        Description = Lang.Get("BetterBlueSetLoadDescription", COMMAND),
        Category    = ModuleCategory.System
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private Hook<AgentReceiveEventDelegate>? AgentAozNotebookReceiveEventHook;
    
    private DRSelectYesno? drSelectYesno;

    protected override void Init()
    {
        AgentAozNotebookReceiveEventHook =
            AgentModule.Instance()->GetAgentByInternalId(AgentId.AozNotebook)->VirtualTable->HookVFuncFromName
            (
                "ReceiveEvent",
                (AgentReceiveEventDelegate)AgentAozNotebookReceiveEventDetour
            );
        AgentAozNotebookReceiveEventHook.Enable();

        CommandManager.Instance().AddSubCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("BetterBlueSetLoad-CommandHelp") });
    }

    protected override void Uninit()
    {
        CommandManager.Instance().RemoveSubCommand(COMMAND);
        
        drSelectYesno?.Dispose();
        drSelectYesno = null;
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Command")}");

        using (ImRaii.PushIndent())
            ImGui.TextUnformatted($"/pdr {COMMAND} → {Lang.Get("BetterBlueSetLoad-CommandHelp")}");
    }
    
    private AtkValue* AgentAozNotebookReceiveEventDetour
    (
        AgentInterface* agent,
        AtkValue*       returnValues,
        AtkValue*       values,
        uint            valueCount,
        ulong           eventKind
    )
    {
        var addon = AOZNotebookPresetList;
        
        if (!addon->IsAddonAndNodesReady() ||
            addon->AtkValues       == null ||
            addon->AtkValues->UInt != 0    ||
            eventKind              != 1    ||
            valueCount             != 2)
            return InvokeOriginal();
        
        var index = values[1].UInt;
        if (values[1].Type != AtkValueType.UInt || index > 4) 
            return InvokeOriginal();
        
        if (drSelectYesno != null &&
            !AddonHelper.TryGetPtrByName("DRSelectYesno", out _))
        {
            try
            {
                drSelectYesno?.Dispose();
                drSelectYesno = null;
            }
            catch
            {
                // 谁敢猜这个时候会发生什么
            }
        }

        if (drSelectYesno != null)
            return InvokeOriginal();

        addon->IsVisible = false;
        AOZNotebook->NumBlockingAddons += 2; // 因为 AOZNotebookPresetList 是 AOZNotebook 的 Popup，隐藏前者的时候会自动把 NumBlockingAddons 减一
        drSelectYesno = DRSelectYesno.Open
        (
            new()
            {
                Prompt = ISeStringEvaluator.Instance().EvaluateFromAddon
                (
                    13653,
                    [
                        GetSetName(index)
                    ]
                ),
                Callback = (_, result) =>
                {
                    if (addon != null && !addon->IsVisible)
                        addon->IsVisible = true;
                    
                    if (AOZNotebook->NumBlockingAddons > 0)
                        AOZNotebook->NumBlockingAddons--;

                    drSelectYesno = null;

                    if (result != DRSelectYesnoResult.Yes)
                        return;

                    ApplyByIndex(index);
                },
                Position = new
                (
                    addon->RootNode->GetNodeState().Center,
                    AddonPositionAlignment.TopCenter
                ),
                BlockedParentID = AOZNotebook->Id,
                ParentID        = AOZNotebook->Id
            }
        );
        
        returnValues->SetBool(false);
        return returnValues;

        AtkValue* InvokeOriginal() =>
            AgentAozNotebookReceiveEventHook.Original(agent, returnValues, values, valueCount, eventKind);
    }

    private static void OnCommand
    (
        string command,
        string args
    )
    {
        args = args.Trim();
        if (string.IsNullOrEmpty(args)) return;

        if (uint.TryParse(args, out var setIndex) && setIndex < 5)
            ApplyByIndex(setIndex);
        else
        {
            var names = AozNoteModule.Instance()->ActiveSets
                        .ToArray()
                        .Where(x => !string.IsNullOrWhiteSpace(x.CustomNameString))
                        .Select((value, index) => (Index: (uint)index, Name: value.CustomNameString))
                        .DistinctBy(x => x.Name)
                        .ToDictionary(x => x.Name, x => x.Index);
            if (!names.TryGetValue(args, out setIndex)) return;
            
            ApplyByIndex(setIndex);
        }
    }
    
    private static void ApplyByIndex
    (
        uint index
    )
    {
        if (index > 4) return;

        var set = AozNoteModule.Instance()->ActiveSets[(int)index];
        var setName = GetSetName(index);

        var manager = ActionManager.Instance();

        Span<uint> current = stackalloc uint[24];
        Span<uint> final   = stackalloc uint[24];

        for (var i = 0; i < 24; i++)
        {
            current[i] = manager->GetActiveBlueMageActionInSlot(i);
            final[i]   = set.ActiveActions[i];
        }

        for (var i = 0; i < 24; i++)
        {
            if (final[i] == 0) continue;

            for (var j = 0; j < 24; j++)
            {
                if (i == j) continue;

                if (final[i] == current[j])
                {
                    manager->SwapBlueMageActionSlots(i, j);
                    final[i] = 0;
                    break;
                }
            }
        }

        for (var i = 0; i < 24; i++)
            if (final[i] != 0)
                manager->AssignBlueMageActionToSlot(i, final[i]);
        
        AozNoteModule.Instance()->LoadActiveSetHotBars((int)index);

        using var utf8String = new Utf8String(setName);
        RaptureLogModule.Instance()->ShowLogMessageString(9472, &utf8String);
    }

    private static string GetSetName
    (
        uint index
    )
    {
        var set = AozNoteModule.Instance()->ActiveSets[(int)index];
        var setName = string.IsNullOrWhiteSpace(set.CustomNameString) ?
                          LuminaWrapper.GetAddonText(12271 + index) :
                          set.CustomNameString;
        return setName;
    }

    #region 常量

    private const string COMMAND = "blueset";

    #endregion
}
