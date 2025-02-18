using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace DailyRoutines.Modules;

public unsafe class AutoMovePetCenter : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title           = GetLoc("AutoMovePetCenterTitle"),
        Description     = GetLoc("AutoMovePetCenterDescription"),
        Category        = ModuleCategories.Combat,
        Author          = ["逆光"],
        ModulesConflict = ["AutoMovePetPosition"]
    };

    private static readonly CompSig ProcessPacketSpawnNPCSig = new("48 89 5C 24 ?? 57 48 81 EC ?? ?? ?? ?? 48 8B DA 8B F9 E8 ?? ?? ?? ?? 3C ?? 75 ?? E8 ?? ?? ?? ?? 3C ?? 75 ?? 80 BB ?? ?? ?? ?? ?? 75 ?? 8B 05 ?? ?? ?? ?? 39 43 ?? 0F 85 ?? ?? ?? ?? 0F B6 53 ?? 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 0F B6 53 ?? 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8D 54 24 ?? C7 44 24 ?? ?? ?? ?? ?? B8 ?? ?? ?? ?? 66 90 48 8D 92 ?? ?? ?? ?? 0F 10 03 0F 10 4B ?? 48 8D 9B ?? ?? ?? ?? 0F 11 42 ?? 0F 10 43 ?? 0F 11 4A ?? 0F 10 4B ?? 0F 11 42 ?? 0F 10 43 ?? 0F 11 4A ?? 0F 10 4B ?? 0F 11 42 ?? 0F 10 43 ?? 0F 11 4A ?? 0F 10 4B ?? 0F 11 42 ?? 0F 11 4A ?? 48 83 E8 ?? 75 ?? 48 8B 03");
    private delegate void ProcessPacketSpawnNPCDelegate(nint a1, byte* packetData);
    private static Hook<ProcessPacketSpawnNPCDelegate>? ProcessPacketSpawnNPCHook;
    
    public override void Init()
    {
        ProcessPacketSpawnNPCHook ??= ProcessPacketSpawnNPCSig.GetHook<ProcessPacketSpawnNPCDelegate>(ProcessPacketSpawnNPCDetour);
        ProcessPacketSpawnNPCHook.Enable();

        DService.DutyState.DutyStarted += OnDutyStarted;
    }

    public override void Uninit()
    {
        DService.DutyState.DutyStarted -= OnDutyStarted;
        base.Uninit();
    }

    private void OnDutyStarted(object? sender, ushort e) => MovePetToMapCenter();

    private void ProcessPacketSpawnNPCDetour(nint a1, byte* packetData)
    {
        ProcessPacketSpawnNPCHook.Original(a1, packetData);
        
        var npcEntityID = *(uint*)(packetData + 84);
        if (npcEntityID != (DService.ClientState.LocalPlayer?.EntityId ?? 0)) return;

        MovePetToMapCenter();
    }

    private static void MovePetToMapCenter()
    {
        if (!LuminaCache.TryGetRow<ContentFinderCondition>
                (GameMain.Instance()->CurrentContentFinderConditionId, out var content) ||
            content.ContentType.RowId is not (4 or 5)                                     ||
            DService.ClientState.LocalPlayer is null                                    ||
            !LuminaCache.TryGetRow<Map>(DService.ClientState.MapId, out var map))
            return;
        
        var pos = TextureToWorld(new(1024), map).ToVector3();
        ExecuteCommandManager.ExecuteCommandComplexLocation(ExecuteCommandComplexFlag.PetAction, pos, 3);
    }
}

