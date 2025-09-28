using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Hooking;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoMovePetCenter : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title           = GetLoc("AutoMovePetCenterTitle"),
        Description     = GetLoc("AutoMovePetCenterDescription"),
        Category        = ModuleCategories.Combat,
        Author          = ["逆光"],
        ModulesConflict = ["AutoMovePetPosition"]
    };

    private static readonly CompSig ProcessPacketSpawnNPCSig = new("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 41 56 48 83 EC ?? 48 8B F1 49 8B E8 48 83 C1");
    private delegate        void ProcessPacketSpawnNPCDelegate(void* a1, byte* packetData);
    private static          Hook<ProcessPacketSpawnNPCDelegate>? ProcessPacketSpawnNPCHook;

    protected override void Init()
    {
        ProcessPacketSpawnNPCHook ??= ProcessPacketSpawnNPCSig.GetHook<ProcessPacketSpawnNPCDelegate>(ProcessPacketSpawnNPCDetour);
        ProcessPacketSpawnNPCHook.Enable();

        DService.DutyState.DutyStarted += OnDutyStarted;
    }

    protected override void Uninit() => 
        DService.DutyState.DutyStarted -= OnDutyStarted;

    private static void OnDutyStarted(object? sender, ushort e) => 
        MovePetToMapCenter();

    private static void ProcessPacketSpawnNPCDetour(void* a1, byte* packetData)
    {
        ProcessPacketSpawnNPCHook.Original(a1, packetData);
        
        var npcEntityID = *(uint*)(packetData + 84);
        if (npcEntityID != LocalPlayerState.EntityID) 
            return;

        MovePetToMapCenter();
    }

    private static void MovePetToMapCenter()
    {
        if (GameState.ContentFinderCondition == 0                                  ||
            GameState.ContentFinderConditionData.ContentType.RowId is not (4 or 5) ||
            DService.ObjectTable.LocalPlayer is null                               ||
            GameState.Map == 0)
            return;
        
        var pos = TextureToWorld(new(1024), GameState.MapData).ToVector3();
        ExecuteCommandManager.ExecuteCommandComplexLocation(ExecuteCommandComplexFlag.PetAction, pos, 3);
    }
}

