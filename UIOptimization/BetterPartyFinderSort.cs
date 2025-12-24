using System;
using System.Runtime.InteropServices;
using DailyRoutines.Abstracts;
using Dalamud.Hooking;

namespace DailyRoutines.ModulesPublic;

public unsafe class BetterPartyFinderSort: DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("BetterPartyFinderSortTitle"),
        Description = GetLoc("BetterPartyFinderSortDescription"),
        Category    = ModuleCategories.UIOptimization,
        Author      = ["decorwdyun"]
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private static readonly CompSig PartyFinderSortCmpSig = new("40 53 48 83 EC 20 0F B6 82 ?? ?? ?? ?? 48 8B DA 38 81 ?? ?? ?? ??");
    private static readonly byte*   PartyFinderSortType   = new CompSig("75 53 0F B6 05 ?? ?? ?? ??").GetStatic<byte>();
    
    private delegate byte                              PartyFinderSortCmpDelegate(nint a1, nint a2);
    private static   Hook<PartyFinderSortCmpDelegate>? PartyFinderSortCmpHook;

    protected override void Init()
    {
        PartyFinderSortCmpHook ??= PartyFinderSortCmpSig.GetHook<PartyFinderSortCmpDelegate>(PartyFinderSortCmpDetour);
        PartyFinderSortCmpHook.Enable();
    }

    private byte PartyFinderSortCmpDetour(nint a1, nint a2)
    {
        try
        {
            var a1Struct = Marshal.PtrToStructure<PartyFinderListing>(a1);
            var a2Struct = Marshal.PtrToStructure<PartyFinderListing>(a2);
            
            if (a1Struct.Unknown408 != a2Struct.Unknown408)
                return (byte)(a1Struct.Unknown408 < a2Struct.Unknown408 ? 1 : 0);

            if (a1Struct.Unknown409 != a2Struct.Unknown409)
                return (byte)(a1Struct.Unknown409 < a2Struct.Unknown409 ? 1 : 0);

            if (a1Struct.IsDutyUnlocked != a2Struct.IsDutyUnlocked)
                return (byte)(a1Struct.IsDutyUnlocked < a2Struct.IsDutyUnlocked ? 1 : 0);

            if (a1Struct.IsBlacklisted != a2Struct.IsBlacklisted)
                return (byte)(a1Struct.IsBlacklisted < a2Struct.IsBlacklisted ? 1 : 0);

            return GetSortStrategy().Compare(a1Struct, a2Struct); 
        }
        catch (Exception)
        {
            return PartyFinderSortCmpHook.Original(a1, a2);
        }   
    }

    private ISortStrategy GetSortStrategy() =>
        *PartyFinderSortType switch
        {
            // 降序
            0 => new TimeLeftAscendingStrategy(),
            // 升序
            1 => new TimeLeftDescendingStrategy(),
            _ => new TimeLeftAscendingStrategy()
        };

    private interface ISortStrategy
    {
        byte Compare(PartyFinderListing a1, PartyFinderListing a2);
    }

    private class TimeLeftAscendingStrategy : ISortStrategy
    {
        public byte Compare(PartyFinderListing a1, PartyFinderListing a2) => 
            a1.TimeLeftSeconds < a2.TimeLeftSeconds ? (byte)1 : (byte)0;
    }

    private class TimeLeftDescendingStrategy : ISortStrategy
    {
        public byte Compare(PartyFinderListing a1, PartyFinderListing a2) => 
            a1.TimeLeftSeconds > a2.TimeLeftSeconds ? (byte)1 : (byte)0;
    }
    
    [StructLayout(LayoutKind.Explicit, Size = 416)]
    private struct PartyFinderListing
    {
        [FieldOffset(0x20)] public ushort DutyID;
        [FieldOffset(0x44)] public uint   TimeLeftSeconds;
        [FieldOffset(408)]  public byte   Unknown408;
        [FieldOffset(409)]  public byte   Unknown409;
        [FieldOffset(410)]  public byte   IsDutyUnlocked;
        [FieldOffset(411)]  public byte   IsBlacklisted;
    }
}
