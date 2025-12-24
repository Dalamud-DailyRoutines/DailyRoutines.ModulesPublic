using System.Collections.Generic;
using DailyRoutines.Abstracts;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.Interop;
using BattleNpcSubKind = Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind;
using ObjectKind = FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoHideGameObjects : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoHideGameObjectsTitle"),
        Description = GetLoc("AutoHideGameObjectsDescription"),
        Category    = ModuleCategories.System
    };

    private static readonly CompSig                          UpdateObjectArraysSig = new("40 57 48 83 EC ?? 48 89 5C 24 ?? 33 DB");
    private delegate        nint                             UpdateObjectArraysDelegate(GameObjectManager* objectManager);
    private static          Hook<UpdateObjectArraysDelegate> UpdateObjectArraysHook;
    
    private static Config ModuleConfig = null!;

    private static readonly HashSet<nint> ProcessedObjects = [];

    private static int ZoneUpdateCount;

    protected override void Init()
    {
        TaskHelper   ??= new() { TimeLimitMS = 30_000 };
        ModuleConfig =   LoadConfig<Config>() ?? new();

        UpdateObjectArraysHook ??= UpdateObjectArraysSig.GetHook<UpdateObjectArraysDelegate>(UpdateObjectArraysDetour);
        UpdateObjectArraysHook.Enable();

        UpdateAllObjects(GameObjectManager.Instance());

        DService.ClientState.TerritoryChanged += OnZoneChanged;
        FrameworkManager.Reg(OnUpdate, throttleMS: 1_000);
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("Default"));

        using (ImRaii.PushId("Default"))
        using (ImRaii.PushIndent())
        {
            if (ImGui.Checkbox(GetLoc("AutoHideGameObjects-HidePlayer"), ref ModuleConfig.DefaultConfig.HidePlayer))
                SaveConfig(ModuleConfig);
            ImGuiOm.TooltipHover(GetLoc("AutoHideGameObjects-HidePlayerHelp"));
            
            if (ImGui.Checkbox(GetLoc("AutoHideGameObjects-HideUnimportantENPC"), ref ModuleConfig.DefaultConfig.HideUnimportantENPC))
                SaveConfig(ModuleConfig);
            ImGuiOm.TooltipHover(GetLoc("AutoHideGameObjects-HideUnimportantENPCHelp"));
            
            if (ImGui.Checkbox(GetLoc("AutoHideGameObjects-HidePet"), ref ModuleConfig.DefaultConfig.HidePet))
                SaveConfig(ModuleConfig);
            ImGuiOm.TooltipHover(GetLoc("AutoHideGameObjects-HidePetHelp"));
            
            if (ImGui.Checkbox(GetLoc("AutoHideGameObjects-HideChocobo"), ref ModuleConfig.DefaultConfig.HideChocobo))
                SaveConfig(ModuleConfig);
            ImGuiOm.TooltipHover(GetLoc("AutoHideGameObjects-HideChocoboHelp"));
        }
    }

    protected override void Uninit()
    {
        FrameworkManager.Unreg(OnUpdate);
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        
        ResetAllObjects();
    }

    private static nint UpdateObjectArraysDetour(GameObjectManager* objectManager)
    {
        var orig = UpdateObjectArraysHook.Original(objectManager);
        UpdateAllObjects(objectManager);
        return orig;
    }
    
    private static void UpdateAllObjects(GameObjectManager* manager)
    {
        if (manager == null) return;
        if (!DService.ClientState.IsLoggedIn) return;
        
        if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent)
        {
            if (GameState.ContentFinderCondition != 0 ||
                GameState.IsInPVPArea                 ||
                GameState.TerritoryIntendedUse == TerritoryIntendedUse.IslandSanctuary)
                return;
        }

        var playerCount   = 0;
        var targetAddress = DService.Targets.Target?.Address ?? nint.Zero;
        foreach (var entry in manager->Objects.IndexSorted)
        {
            if (GameState.TerritoryIntendedUse == TerritoryIntendedUse.OccultCrescent)
            {
                if ((LocalPlayerState.Object?.Position.Y ?? -100) < 0) continue;
                if (!ShouldFilterOccultCrescent(entry.Value, targetAddress, ref playerCount)) 
                    continue;
            }
            else
            {
                if (!ShouldFilter(ModuleConfig.DefaultConfig, entry.Value))
                    continue;
            }
            
            entry.Value->RenderFlags |= (VisibilityFlags)256;
            ProcessedObjects.Add((nint)entry.Value);
        }
    }
    
    private static bool ShouldFilter(FilterConfig config, GameObject* gameObject)
    {
        if (gameObject                  == null) return false;
        if ((nint)gameObject            == (LocalPlayerState.Object?.Address ?? nint.Zero)) return false;
        if (((RenderFlag)gameObject->RenderFlags).HasFlag(RenderFlag.Invisible)) return false;
        if (gameObject->NamePlateIconId != 0) return false;
        
        // 玩家
        if (config.HidePlayer                       &&
            gameObject->ObjectKind == ObjectKind.Pc &&
            IPlayerCharacter.Create((nint)gameObject) is { } player)
        {
            // 假玩家
            if (player.ClassJob.RowId == 0 || string.IsNullOrEmpty(player.Name.TextValue)) 
                return false;
            
            if (player.StatusFlags.HasFlag(StatusFlags.Friend))
                return false;

            if (LocalPlayerState.IsInParty &&
                (player.StatusFlags.HasFlag(StatusFlags.PartyMember) ||
                 player.StatusFlags.HasFlag(StatusFlags.AllianceMember)))
                return false;
            
            return true;
        }
        
        // 不重要 NPC
        if (config.HideUnimportantENPC                    &&
            gameObject->ObjectKind == ObjectKind.EventNpc &&
            string.IsNullOrEmpty(gameObject->NameString))
            return true;
        
        // 宠物
        if (config.HidePet                                                            &&
            gameObject->ObjectKind                            == ObjectKind.BattleNpc &&
            IBattleNPC.Create((nint)gameObject).BattleNPCKind == BattleNpcSubKind.Pet &&
            gameObject->OwnerId                               != LocalPlayerState.EntityID)
            return true;
        
        // 陆行鸟
        if (config.HideChocobo                                                            &&
            gameObject->ObjectKind                            == ObjectKind.BattleNpc     &&
            IBattleNPC.Create((nint)gameObject).BattleNPCKind == BattleNpcSubKind.Chocobo &&
            gameObject->OwnerId                               != LocalPlayerState.EntityID)
            return true;
        
        return false;
    }

    private static bool ShouldFilterOccultCrescent(GameObject* gameObject, nint targetAddress, ref int playerCount)
    {
        if (gameObject       == null) return false;
        if ((nint)gameObject == (LocalPlayerState.Object?.Address ?? nint.Zero)) return false;
        if (gameObject->NamePlateIconId != 0) return false;
        
        // 玩家
        if (gameObject->ObjectKind == ObjectKind.Pc &&
            IPlayerCharacter.Create((nint)gameObject) is { } player)
        {
            playerCount++;

            if (player.IsDead || player.Address == targetAddress)
            {
                gameObject->RenderFlags &= ~(VisibilityFlags)256;
                ProcessedObjects.Remove((nint)gameObject);
                return false;
            }

            if (player.StatusFlags.HasFlag(StatusFlags.Friend))
                return false;

            if (LocalPlayerState.IsInParty &&
                (player.StatusFlags.HasFlag(StatusFlags.PartyMember) ||
                 player.StatusFlags.HasFlag(StatusFlags.AllianceMember)))
                return false;

            return playerCount >= 10;
        }

        // 不重要 NPC
        if (gameObject->ObjectKind == ObjectKind.EventNpc &&
            string.IsNullOrEmpty(gameObject->NameString))
            return true;

        if (gameObject->ObjectKind == ObjectKind.BattleNpc &&
            IBattleNPC.Create((nint)gameObject) is { OwnerID: not (0xE0000000 or 0) })
            return true;

        return false;
    }

    private static void ResetAllObjects()
    {
        if (!DService.ClientState.IsLoggedIn || ProcessedObjects.Count == 0) return;
        
        var manager = GameObjectManager.Instance();
        if (manager == null) return;
        
        foreach (ref var entry in GameObjectManager.Instance()->Objects.IndexSorted)
        {
            if (entry.Value       == null                                            ||
                (nint)entry.Value == (LocalPlayerState.Object?.Address ?? nint.Zero) ||
                !ProcessedObjects.Contains((nint)entry.Value))
                continue;
            
            entry.Value->RenderFlags &= ~(VisibilityFlags)256;
        }
        
        ProcessedObjects.Clear();
        ZoneUpdateCount = 0;
    }
    
    private static void OnZoneChanged(ushort zone)
    {
        ZoneUpdateCount = 0;
        ProcessedObjects.Clear();
    }

    private static void OnUpdate(IFramework _)
    {
        // 主要是小区域更新不及时
        if (ZoneUpdateCount > 3 || BetweenAreas) return;

        ZoneUpdateCount++;
        UpdateAllObjects(GameObjectManager.Instance());
    }

    private class Config : ModuleConfiguration
    {
        public FilterConfig DefaultConfig = new();
    }

    private class FilterConfig
    {
        // 玩家
        public bool HidePlayer = true;
        
        // 宠物
        public bool HidePet = true;
        
        // 陆行鸟
        public bool HideChocobo = true;
        
        // 不重要 NPC
        public bool HideUnimportantENPC = true;
    }
    
    private enum RenderFlag
    {
        Invisible = 256
    }
}
