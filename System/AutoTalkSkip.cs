using System.Runtime.InteropServices;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Interop.Game.Models;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoTalkSkip : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoTalkSkipTitle"),
        Description = Lang.Get("AutoTalkSkipDescription"),
        Category    = ModuleCategory.System
    };

    private delegate AtkValue* ReceiveDialogueEventDelegate
    (
        AtkModuleInterface.AtkEventInterface* eventInterface,
        AtkValue*                             returnValue,
        AtkValue*                             values,
        uint                                  valueCount
    );

    private delegate void HandleDialogueDelegate
    (
        DialogueCallback* callback,
        int               valueCount,
        byte**            text
    );

    private delegate void ShortTalkDelegate
    (
        EventSceneModule* eventSceneModule,
        nint              target,
        nint              text,
        nint              speakerName,
        float             duration,
        int               unknown0,
        int               unknown1,
        byte              withLineVoice
    );

    private delegate void SchedulerTalkDelegate
    (
        SchedulerControl* control,
        nint              line,
        nint              text,
        nint              target,
        nint              result
    );

    private delegate void SchedulerCommandDelegate
    (
        AtkModuleInterface.AtkEventInterface* control,
        int                                   command,
        AtkValue*                             values,
        uint                                  valueCount,
        uint                                  unknown
    );

    private delegate ushort OpenTalkDelegate
    (
        AgentCutscene*                        agentCutscene,
        uint                                  valueCount,
        AtkValue*                             values,
        AtkModuleInterface.AtkEventInterface* eventInterface,
        ulong                                 eventKind
    );

    private static readonly CompSig HandleDialogueSig = new
        ("40 57 41 56 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 4D 8B F0");

    private static readonly CompSig ShortTalkSig = new
        ("48 89 5C 24 ?? 48 89 74 24 ?? 48 89 7C 24 ?? 4C 89 74 24 ?? 55 48 8B EC 48 83 EC ?? 33 F6");

    private static readonly CompSig SchedulerTalkSig = new
        ("40 55 57 41 54 41 55 41 56 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 4C 8B A4 24");

    private static readonly CompSig SchedulerCommandSig = new
        ("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 48 89 7C 24 ?? 41 56 48 83 EC ?? 48 8B D9 48 63 FA");

    private static readonly CompSig OpenTalkSig = new
    (
        "40 53 55 56 57 41 54 41 56 41 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 4C 8B BC 24 ?? ?? ?? ?? 48 8B D9 48 8B 49 ?? 4D 8B F1 49 8B E8 8B F2"
    );

    private static readonly CompSig ReceiveDialogueEventSig = new("40 53 48 83 EC ?? 4D 8B D0 48 8B DA 4C 8B C1");

    private Hook<HandleDialogueDelegate>?               HandleDialogueHook;
    private Hook<ShortTalkDelegate>?                    ShortTalkHook;
    private Hook<SchedulerTalkDelegate>?                SchedulerTalkHook;
    private Hook<SchedulerCommandDelegate>?             SchedulerCommandHook;
    private Hook<OpenTalkDelegate>?                     OpenTalkHook;
    private Hook<RaptureAtkModule.Delegates.OpenAddon>? OpenDialogueAddonHook;

    private ReceiveDialogueEventDelegate ReceiveDialogueEvent = null!;

    protected override void Init()
    {
        ReceiveDialogueEvent = ReceiveDialogueEventSig.GetDelegate<ReceiveDialogueEventDelegate>();

        HandleDialogueHook = HandleDialogueSig.GetHook<HandleDialogueDelegate>(HandleDialogueDetour);
        HandleDialogueHook.Enable();

        ShortTalkHook = ShortTalkSig.GetHook<ShortTalkDelegate>(ShortTalkDetour);
        ShortTalkHook.Enable();

        SchedulerTalkHook = SchedulerTalkSig.GetHook<SchedulerTalkDelegate>(SchedulerTalkDetour);
        SchedulerTalkHook.Enable();

        SchedulerCommandHook = SchedulerCommandSig.GetHook<SchedulerCommandDelegate>(SchedulerCommandDetour);
        SchedulerCommandHook.Enable();

        OpenTalkHook = OpenTalkSig.GetHook<OpenTalkDelegate>(OpenTalkDetour);
        OpenTalkHook.Enable();

        OpenDialogueAddonHook = IGameInteropProvider.Instance().HookFromAddress<RaptureAtkModule.Delegates.OpenAddon>
        (
            (nint)RaptureAtkModule.MemberFunctionPointers.OpenAddon,
            OpenDialogueAddonDetour
        );
        OpenDialogueAddonHook.Enable();
    }

    private void HandleDialogueDetour
    (
        DialogueCallback* callback,
        int               valueCount,
        byte**            text
    )
    {
        if (callback->Remaining <= 0)
            callback->SelectedEventImpl = (nint)(&callback->EventImpl0);
        else
        {
            callback->SelectedEventImpl = (nint)(&callback->EventImpl1);
            callback->Remaining--;
        }

        // 直接按已结束交回等待中的事件任务
        callback->Finished = 1;

        var result = default(AtkValue);
        ReceiveDialogueEvent((AtkModuleInterface.AtkEventInterface*)callback, &result, null, 0);
    }

    private static void ShortTalkDetour
    (
        EventSceneModule* eventSceneModule,
        nint              target,
        nint              text,
        nint              speakerName,
        float             duration,
        int               unknown0,
        int               unknown1,
        byte              withLineVoice
    )
    {
        // 调用方不等待结果, 不进入显示流程即为跳过
    }

    private static void SchedulerTalkDetour
    (
        SchedulerControl* control,
        nint              line,
        nint              text,
        nint              target,
        nint              result
    )
    {
        FinishSchedulerText(control);

        ((byte*)result)[4] = 0;
    }

    private void SchedulerCommandDetour
    (
        AtkModuleInterface.AtkEventInterface* control,
        int                                   command,
        AtkValue*                             values,
        uint                                  valueCount,
        uint                                  unknown
    )
    {
        if (command != 0)
        {
            SchedulerCommandHook.Original(control, command, values, valueCount, unknown);
            return;
        }

        FinishSchedulerText((SchedulerControl*)control);
    }

    private static ushort OpenTalkDetour
    (
        AgentCutscene*                        agentCutscene,
        uint                                  valueCount,
        AtkValue*                             values,
        AtkModuleInterface.AtkEventInterface* eventInterface,
        ulong                                 eventKind
    ) => 0;

    private ushort OpenDialogueAddonDetour
    (
        RaptureAtkModule*                     module,
        uint                                  addonNameID,
        uint                                  valueCount,
        AtkValue*                             values,
        AtkModuleInterface.AtkEventInterface* eventInterface,
        ulong                                 eventKind,
        ushort                                parentAddonID,
        int                                   depthLayer
    )
    {
        var addonNames = module->AddonNames.AsSpan();

        if (addonNameID < addonNames.Length)
        {
            var addonName = addonNames[(int)addonNameID].AsSpan();

            if (addonName.SequenceEqual("Talk"u8))
                return 0;
        }

        return OpenDialogueAddonHook.Original
        (
            module,
            addonNameID,
            valueCount,
            values,
            eventInterface,
            eventKind,
            parentAddonID,
            depthLayer
        );
    }

    private static void FinishSchedulerText
    (
        SchedulerControl* control
    )
    {
        control->Flags = (ushort)((control->Flags & 0xFFF9) | 4);

        var callback = control->TextFinishedCallback;

        if (callback != 0)
            ((delegate* unmanaged<nint, void>)callback)(control->TextFinishedContext);
    }

    // 对话逻辑层持有的对话回调
    [StructLayout(LayoutKind.Explicit)]
    private struct DialogueCallback
    {
        [FieldOffset(0x50)]
        public int Remaining;
        [FieldOffset(0x67)]
        public byte Finished;
        [FieldOffset(0x68)]
        public nint EventImpl0;
        [FieldOffset(0x70)]
        public nint EventImpl1;
        [FieldOffset(0x78)]
        public nint SelectedEventImpl;
    }

    // 排程系统的界面控制
    [StructLayout(LayoutKind.Explicit)]
    private struct SchedulerControl
    {
        [FieldOffset(0x40)]
        public ushort Flags;
        [FieldOffset(0x70)]
        public nint TextFinishedCallback;
        [FieldOffset(0x78)]
        public nint TextFinishedContext;
    }
}
