using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using System;
using System.Runtime.InteropServices;
using System.Text;
using DailyRoutines.Infos;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace DailyRoutines.Modules;

public unsafe class RealQueuePosition : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("RealQueuePositionTitle"),
        Description = GetLoc("RealQueuePositionDescription"),
        Category    = ModuleCategories.UIOptimization,
        Author      = ["逆光", "Nukoooo"]
    };

    private DateTime ETA = DateTime.Now;
    
    private readonly CompSig AgentWorldTravelUpdaterSig = new("40 53 56 57 41 54 41 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 4C 8B FA");
    private delegate bool AgentWorldTravelUpdateDelegate(nint a1, NumberArrayData* a2, StringArrayData* a3, bool a4);
    private static Hook<AgentWorldTravelUpdateDelegate> AgentWorldTravelUpdateHook;

    private static readonly CompSig UpdateWorldTravelDataSig = new("48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 48 8B D9 48 8B FA 0F B6 4A");
    private delegate void UpdateWorldTravelDataDelegate(nint a1, nint a2);
    private static Hook<UpdateWorldTravelDataDelegate> UpdateWorldTravelDataHook;

    private static readonly CompSig ContentFinderQueuePositionDataSig = new("40 ?? 57 41 ?? 48 ?? ?? ?? 0f ?? ?? ?? 49");
    private delegate byte ContentFinderQueuePositionDataDelegate(nint a1, uint a2, nint a3);
    private static Hook<ContentFinderQueuePositionDataDelegate>? ContentFinderQueuePositionDataHook;

    protected override void Init()
    {
        AgentWorldTravelUpdateHook ??= AgentWorldTravelUpdaterSig.GetHook<AgentWorldTravelUpdateDelegate>(AgentWorldTravelUpdaterDetour);
        AgentWorldTravelUpdateHook.Enable();

        UpdateWorldTravelDataHook ??= UpdateWorldTravelDataSig.GetHook<UpdateWorldTravelDataDelegate>(UpdateWorldTravelDataDetour);
        UpdateWorldTravelDataHook.Enable();

        ContentFinderQueuePositionDataHook ??= ContentFinderQueuePositionDataSig.GetHook<ContentFinderQueuePositionDataDelegate>(ContentFinderQueuePositionDataDetour);
        ContentFinderQueuePositionDataHook.Enable();
    }

    private static double CalculateWaitTime(int position)
    {
        if (position <= 0) return 0;

        var fullGroups = (position - 1) / 4;

        var fullGroupTime = fullGroups * 10f;

        var remainingPeople = (position - 1) % 4;

        var remainingTime = remainingPeople > 0 ? 10f : 0;
        var totalWaitTime = fullGroupTime + remainingTime;

        return totalWaitTime;
    }

    private void UpdateWorldTravelDataDetour(nint a1, nint a2)
    {
        var type = *(byte*)(a2 + 16);

        if (type == 1)
        {
            var position = *(int*)(a2 + 20);
            ETA = DateTime.Now.AddSeconds(CalculateWaitTime(position));
        }

        UpdateWorldTravelDataHook.Original(a1, a2);
    }

    private bool AgentWorldTravelUpdaterDetour(nint a1, NumberArrayData* a2, StringArrayData* a3, bool a4)
    {
        var agentData = (nint)AgentWorldTravel.Instance();
        if (agentData == nint.Zero || !(*(bool*)(agentData + 0x120)))
            return AgentWorldTravelUpdateHook.Original(a1, a2, a3, a4);

        var result = AgentWorldTravelUpdateHook.Original(a1, a2, a3, a4);
        if (!result) return false;

        var index = 5;

        if (a2->IntArray[5] > 0)
            index = 6;

        var position = *(uint*)(agentData + 0x12c);
        var positionStr = $"{LuminaGetter.GetRow<Addon>(10988)!.Value.Text.ExtractText()}: #{position}";
        a3->SetValue(index, positionStr);
        
        var queueTime = TimeSpan.FromSeconds(*(int*)(agentData + 0x128));
        var info      = GetLoc("RealQueuePosition-ETA", @$"{queueTime:mm\:ss}", @$"{ETA - DateTime.Now:mm\:ss}");
        a3->SetValue(index + 1, info);

        return true;
    }

    private byte ContentFinderQueuePositionDataDetour(nint a1, uint a2, nint a3)
    {
        uint v9 = Marshal.ReadByte(new nint(a3 + 4));

        if (v9 != 0)
        {
            Marshal.WriteByte(new nint(a1 + 91), (byte)v9);
            Marshal.WriteByte(new nint(a1 + 92), (byte)v9);
        }

        return ContentFinderQueuePositionDataHook.Original(a1, a2, a3);
    }
}
