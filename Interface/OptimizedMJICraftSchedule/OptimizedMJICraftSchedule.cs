using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.KamiToolKit.Styles.Island;
using ScheduleAgent = FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent.AgentMJICraftSchedule;
using RestAddon = FFXIVClientStructs.Expansion.FFXIV.Client.UI.AddonMJICraftScheduleMaintenance;
using NativeScheduleAgent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMJICraftSchedule;

namespace DailyRoutines.ModulesPublic.OptimizedMJICraftSchedule;

public unsafe partial class OptimizedMJICraftSchedule : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("OptimizedMJICraftScheduleTitle"),
        Description = Lang.Get("OptimizedMJICraftScheduleDescription"),
        Category    = ModuleCategory.Interface,
        PreviewImageURL =
        [
            "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/OptimizedMJICraftSchedule/preview-1.webp"
        ]
    };

    public override ModuleBehaviour Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private Hook<ScheduleAgent.Delegates.UpdateScheduleData>        UpdateScheduleDataHook;
    private Hook<ScheduleAgent.Delegates.HandleEventSetRestConfirm> HandleEventSetRestConfirmHook;
    private Hook<RestAddon.Delegates.UpdateConfirmButton>           UpdateConfirmButtonHook;
    private Hook<ScheduleAgent.Delegates.AskSetRestConfirm>         AskSetRestConfirmHook;
    private Hook<ScheduleAgent.Delegates.UpdateMainAddon>           UpdateMainAddonHook;

    private Calculator calculator = null!;

    protected override void Init()
    {
        TaskHelper ??= new()
        {
            TimeoutMS       = 15_000,
            RetryIntervalMS = 200
        };

        calculator = new(this);
        uiStyle    = new IslandUIStyle();

        UpdateScheduleDataHook ??= IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(ScheduleAgent.MemberFunctionPointers),
            nameof(ScheduleAgent.UpdateScheduleData),
            (ScheduleAgent.Delegates.UpdateScheduleData)UpdateScheduleDataDetour
        );
        UpdateScheduleDataHook.Enable();

        HandleEventSetRestConfirmHook ??= IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(ScheduleAgent.MemberFunctionPointers),
            nameof(ScheduleAgent.HandleEventSetRestConfirm),
            (ScheduleAgent.Delegates.HandleEventSetRestConfirm)HandleEventSetRestConfirmDetour
        );
        HandleEventSetRestConfirmHook.Enable();

        UpdateConfirmButtonHook ??= IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(RestAddon.MemberFunctionPointers),
            nameof(RestAddon.UpdateConfirmButton),
            (RestAddon.Delegates.UpdateConfirmButton)UpdateConfirmButtonDetour
        );
        UpdateConfirmButtonHook.Enable();

        AskSetRestConfirmHook ??= IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(ScheduleAgent.MemberFunctionPointers),
            nameof(ScheduleAgent.AskSetRestConfirm),
            (ScheduleAgent.Delegates.AskSetRestConfirm)AskSetRestConfirmDetour
        );
        AskSetRestConfirmHook.Enable();

        UpdateMainAddonHook ??= IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(ScheduleAgent.MemberFunctionPointers),
            nameof(ScheduleAgent.UpdateMainAddon),
            (ScheduleAgent.Delegates.UpdateMainAddon)UpdateMainAddonDetour
        );
        UpdateMainAddonHook.Enable();

        panel = new(this)
        {
            InternalName   = "DROptimizedMJICraftSchedule",
            Title          = Info.Title,
            Style          = uiStyle,
            ContentPadding = Vector2.Zero,
            Size           = new(480, 384)
        };

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup,   "MJICraftSchedule", OnSchedule);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize, "MJICraftSchedule", OnSchedule);
        if (MJICraftSchedule->IsAddonAndNodesReady())
            OnSchedule(AddonEvent.PostSetup, null);
    }

    protected override void Uninit()
    {
        CancelPreview();
        calculator.Reset();

        IAddonLifecycle.Instance().UnregisterListener(OnSchedule);

        previewIndicator?.Dispose();
        previewIndicator = null;

        panel?.Dispose();
        panel = null;
    }

    private bool UpdateMainAddonDetour(ScheduleAgent* agent)
    {
        previewIndicator?.IsVisible = false;
        var refreshed = UpdateMainAddonHook.Original(agent);
        if (!refreshed                ||
            !isPreviewing             ||
            calculator.Result == null ||
            agent->Data       == null)
            return refreshed;

        var data = agent->Data;

        if (data->CycleInProgress != calculator.Input.CurrentCycle ||
            data->RestCycles      != calculator.Input.RestCycles   ||
            calculator.HasTimeChanged)
        {
            isPreviewing = false;
            calculator.Reset();
            panel.SetStatus("OptimizedMJICraftSchedule-Stale");
            return refreshed;
        }

        if (!calculator.Result.Days.TryGetValue(data->CycleDisplayed, out var entries))
            return refreshed;

        previewIndicator?.IsVisible = true;
        Span<NativeScheduleAgent.WorkshopData> schedules = stackalloc NativeScheduleAgent.WorkshopData[4];
        data->WorkshopSchedules.CopyTo(schedules);
        var derivedFlag = data->Flags2 & NativeScheduleAgent.DataFlags2.DerivedDataDirty;

        try
        {
            data->WorkshopSchedules.Clear();

            foreach (var entry in calculator.Input.Fixed[data->CycleDisplayed])
            {
                ref var workshop = ref data->WorkshopSchedules[entry.Workshop];
                var     original = schedules[entry.Workshop];

                for (var index = 0; index < original.NumScheduleEntries; index++)
                {
                    if (original.EntryData[index].StartingSlot != entry.Hour)
                        continue;

                    workshop.EntryData[workshop.NumScheduleEntries++] = original.EntryData[index];
                    if (entry.Efficient)
                        workshop.NumEfficientCrafts++;
                    break;
                }
            }

            foreach (var entry in entries)
            {
                ref var workshop = ref data->WorkshopSchedules[entry.Workshop];
                workshop.EntryData[workshop.NumScheduleEntries++] = new()
                {
                    CraftObjectId = entry.ID,
                    StartingSlot  = entry.Hour,
                    Duration      = (byte)calculator.Input.Recipes[entry.ID].Duration,
                    Efficient     = entry.Efficient
                };
                if (entry.Efficient)
                    workshop.NumEfficientCrafts++;
            }

            return UpdateMainAddonHook.Original(agent);
        }
        finally
        {
            schedules.CopyTo(data->WorkshopSchedules);
            data->Flags2 = (data->Flags2 & ~NativeScheduleAgent.DataFlags2.DerivedDataDirty) | derivedFlag;
        }
    }

    private static void UpdateConfirmButtonDetour(RestAddon* addon)
    {
        uint mask = 0;

        for (var index = 0; index < 14; index++)
        {
            var node = addon->GetNodeById
            (
                (uint)(index < 7 ?
                           index + 6 :
                           index + 9)
            );
            if (node != null &&
                node->GetAsAtkComponentCheckBox()->IsChecked)
                mask |= 1u << index;
        }

        var count = BitOperations.PopCount(mask);
        addon->GetComponentButtonById(24)->SetEnabledState(count is >= 1 and <= 4);
        var text = addon->GetTextNodeById(2);
        if (text != null)
            text->SetText(Lang.Get("OptimizedMJICraftSchedule-RestHint"));
    }

    private uint AskSetRestConfirmDetour(ScheduleAgent* agent, uint restCycles)
    {
        var addonID      = AskSetRestConfirmHook.Original(agent, restCycles);
        var confirmation = (AddonSelectYesno*)SelectYesno;
        if (confirmation == null)
            return addonID;

        var weeks = new string[2];

        for (var week = 0; week < 2; week++)
        {
            var days = Enumerable.Range(0, 7)
                                 .Where(day => (restCycles & (1u << ((week * 7) + day))) != 0)
                                 .Select(day => (day + 1).ToString())
                                 .ToArray();
            weeks[week] = Lang.Get
            (
                "OptimizedMJICraftSchedule-RestCycle",
                new Dictionary<string, object>
                {
                    ["week"] = Lang.Get
                    (
                        week == 0 ?
                            "OptimizedMJICraftSchedule-ThisWeek" :
                            "OptimizedMJICraftSchedule-NextWeek"
                    ),
                    ["text"] = days.Length == 0 ?
                                   Lang.Get("OptimizedMJICraftSchedule-NoRest") :
                                   string.Join("、", days)
                }
            );
        }

        confirmation->PromptText->SetText
        (
            Lang.Get
            (
                "OptimizedMJICraftSchedule-RestConfirm",
                new Dictionary<string, object>
                {
                    ["text"] = string.Join("\n", weeks)
                }
            )
        );
        return addonID;
    }

    private bool HandleEventSetRestConfirmDetour(ScheduleAgent* agent, AtkValue* values)
    {
        if (values->Int != 0)
            return HandleEventSetRestConfirmHook.Original(agent, values);

        var mask = agent->Data->NewRestCycles & 0x3FFF;
        if (BitOperations.PopCount(mask) is < 1 or > 4)
            return true;

        Span<uint> days  = stackalloc uint[4];
        var        count = 0;

        for (var index = 0; index < 14; index++)
        {
            if ((mask & (1u << index)) != 0)
                days[count++] = (uint)index;
        }

        days[count..].Fill(days[0]);

        CancelPreview();
        calculator.Reset();
        agent->Data->RestCycles         = mask;
        agent->Data->ConfirmAddonHandle = 0;
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.SetMJIWorkshopRest, days[0], days[1], days[2], days[3]);
        if (MJICraftScheduleMaintenance != null)
            MJICraftScheduleMaintenance->Close(true);
        agent->Data->OpenedModalAddonHandle = 0;
        agent->SetDisplayedCycle(agent->Data->CycleDisplayed);
        panel.Refresh();
        return true;
    }

    private void UpdateScheduleDataDetour
    (
        ScheduleAgent* agent,
        byte           cycleInProgress,
        byte*          durations,
        byte*          startingHours,
        byte*          craftObjectIDs,
        byte*          currentRestDays,
        byte*          nextRestDays,
        byte*          efficiencies,
        uint           groove
    )
    {
        UpdateScheduleDataHook.Original
        (
            agent,
            cycleInProgress,
            durations,
            startingHours,
            craftObjectIDs,
            currentRestDays,
            nextRestDays,
            efficiencies,
            groove
        );
        calculator.UpdateScheduleData(agent);
    }
}
