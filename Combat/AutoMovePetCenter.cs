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

    private static readonly CompSig ProcessPacketSpawnNPCSig =
        new("48 89 5C 24 08 57 48 81 EC 30 04 00 00 48 8B DA 8B F9 E8 ?? ?? ?? ?? 3C 01 75 21 E8 ?? ?? ?? ?? 3C 01 75 18 80 BB 82 00 00 00 02 75 0F 8B 05 ?? ?? ?? ?? 39 43 54 0F 85 ?? ?? ?? ?? 0F B6 53 7E 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 0F B6 53 7E 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8D 44 24 28 C7 44 24 20 02 00 00 00");
    private delegate        void ProcessPacketSpawnNPCDelegate(uint targetID, byte* packetData);
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
        MovePetToMapCenter(LocalPlayerState.EntityID);

    private static void ProcessPacketSpawnNPCDetour(uint targetID, byte* packetData)
    {
        ProcessPacketSpawnNPCHook.Original(targetID, packetData);
        
        var entityIDPtr = (uint*)(packetData + 84);
        if (entityIDPtr == null) return;
        
        MovePetToMapCenter(*entityIDPtr);
    }

    private static void MovePetToMapCenter(uint npcEntityID)
    {
        if (GameState.ContentFinderCondition == 0                                  ||
            GameState.Map                    == 0                                  ||
            npcEntityID                      != LocalPlayerState.EntityID          ||
            GameState.ContentFinderConditionData.ContentType.RowId is not (4 or 5) ||
            DService.ObjectTable.LocalPlayer is null)
            return;
        
        var pos = TextureToWorld(new(1024), GameState.MapData).ToVector3();
        ExecuteCommandManager.ExecuteCommandComplexLocation(ExecuteCommandComplexFlag.PetAction, pos, 3);
    }
}

