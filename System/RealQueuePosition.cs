using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using AgentWorldTravel = FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent.AgentWorldTravel;

namespace DailyRoutines.ModulesPublic;

public unsafe class RealQueuePosition : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("RealQueuePositionTitle"),
        Description = Lang.Get("RealQueuePositionDescription"),
        Category    = ModuleCategory.System,
        Author      = ["逆光", "Nukoooo"]
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private Hook<AgentWorldTravel.Delegates.UpdateAddons> UpdateAddonsHook;

    private Hook<AgentWorldTravel.Delegates.OnWorldTravelStatusPacket> OnWorldTravelStatusPacketHook;
    
    private Hook<ContentsFinderQueueInfo.Delegates.ProcessInfoState>? ContentFinderQueuePositionDataHook;

    private DateTime eta = StandardTimeManager.Instance().Now;

    protected override void Init()
    {
        UpdateAddonsHook = IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(AgentWorldTravel.MemberFunctionPointers),
            nameof(AgentWorldTravel.UpdateAddons),
            (AgentWorldTravel.Delegates.UpdateAddons)UpdateAddonsDetour
        );
        UpdateAddonsHook.Enable();

        OnWorldTravelStatusPacketHook = IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(AgentWorldTravel.MemberFunctionPointers),
            nameof(AgentWorldTravel.OnWorldTravelStatusPacket),
            (AgentWorldTravel.Delegates.OnWorldTravelStatusPacket)UpdateWorldTravelDataDetour
        );
        OnWorldTravelStatusPacketHook.Enable();
        
        ContentFinderQueuePositionDataHook = IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(ContentsFinderQueueInfo.MemberFunctionPointers),
            nameof(ContentsFinderQueueInfo.ProcessInfoState),
            (ContentsFinderQueueInfo.Delegates.ProcessInfoState)ContentFinderQueuePositionDataDetour
        );
        ContentFinderQueuePositionDataHook.Enable();
    }

    private void UpdateWorldTravelDataDetour
    (
        AgentWorldTravel*                         agent,
        AgentWorldTravel.WorldTravelStatusPacket* packet
    )
    {
        if (packet->Status == AgentWorldTravel.WorldTravelStatus.InQueue)
        {
            var position = packet->QueuePosition;
            eta = StandardTimeManager.Instance().Now.AddSeconds(CalculateWaitTime(position));
        }

        OnWorldTravelStatusPacketHook.Original(agent, packet);
    }

    private bool UpdateAddonsDetour
    (
        AgentWorldTravel* agent,
        NumberArrayData*  numberArray,
        StringArrayData*  stringArray,
        bool              a4
    )
    {
        if (agent->IsWorldTraveling == 0)
            return UpdateAddonsHook.Original(agent, numberArray, stringArray, a4);

        var result = UpdateAddonsHook.Original(agent, numberArray, stringArray, a4);
        if (!result)
            return false;

        var index = 5;

        if (numberArray->IntArray[5] > 0)
            index = 6;

        var position    = agent->QueuePosition;
        var positionStr = ISeStringEvaluator.Instance().EvaluateFromAddon(10039, [position]);

        using var builder = new RentedSeStringBuilder();
        builder.Append(LuminaWrapper.GetAddonText(12522))
               .Append(positionStr);
        stringArray->SetValue(index, builder.GetViewAsSpan());

        var queueTime = TimeSpan.FromSeconds(agent->ElapsedQueueTimeSecond);
        var info = Lang.Get
        (
            "RealQueuePosition-ETA",
            new Dictionary<string, object>
            {
                ["currentTimeText"]   = @$"{queueTime:mm\:ss}",
                ["estimatedTimeText"] = @$"{eta - StandardTimeManager.Instance().Now:mm\:ss}"
            }
        );
        stringArray->SetValue(index + 1, info);

        return true;
    }

    private void ContentFinderQueuePositionDataDetour
    (
        ContentsFinderQueueInfo* info,
        ContentsFinderQueueState state,
        QueueInfoState*          infoState
    )
    {
        var positionInQueue = (sbyte)infoState->PositionInQueue;

        if (positionInQueue != 0)
        {
            info->PositionInQueue        = positionInQueue;
            info->ClampedPositionInQueue = positionInQueue;
        }

        ContentFinderQueuePositionDataHook.Original(info, state, infoState);
    }

    private static double CalculateWaitTime
    (
        uint position
    )
    {
        if (position <= 0) return 0;

        var fullGroups = (position - 1) / 4;

        var fullGroupTime = fullGroups * 10f;

        var remainingPeople = (position - 1) % 4;

        var remainingTime = remainingPeople > 0 ?
                                10f :
                                0;
        var totalWaitTime = fullGroupTime + remainingTime;

        return totalWaitTime;
    }
}
