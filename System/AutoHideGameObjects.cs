using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using BattleNpcSubKind = Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind;
using ObjectKind = FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoHideGameObjects : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoHideGameObjectsTitle"),
        Description = Lang.Get("AutoHideGameObjectsDescription"),
        Category    = ModuleCategory.System
    };
    
    private static readonly CompSig                          UpdateObjectArraysSig = new("40 57 48 83 EC ?? 48 89 5C 24 ?? 33 DB");
    private delegate        void*                            UpdateObjectArraysDelegate(GameObjectManager* objectManager);
    private                 Hook<UpdateObjectArraysDelegate> UpdateObjectArraysHook;

    private Config config = null!;

    private readonly HashSet<nint> processedObjects = [];

    private int zoneUpdateCount;

    protected override void Init()
    {
        TaskHelper   ??= new() { TimeoutMS = 30_000 };
        config =   Config.Load(this) ?? new();

        UpdateObjectArraysHook ??= UpdateObjectArraysSig.GetHook<UpdateObjectArraysDelegate>(UpdateObjectArraysDetour);
        UpdateObjectArraysHook.Enable();

        UpdateAllObjects(GameObjectManager.Instance());

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        FrameworkManager.Instance().Reg(OnUpdate, 1_000);
    }

    protected override void Uninit()
    {
        FrameworkManager.Instance().Unreg(OnUpdate);
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;

        ResetAllObjects();
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Default"));

        using (ImRaii.PushId("Default"))
        using (ImRaii.PushIndent())
        {
            if (ImGui.Checkbox(Lang.Get("AutoHideGameObjects-HidePlayer"), ref config.DefaultConfig.HidePlayer))
                config.Save(this);
            ImGuiOm.TooltipHover(Lang.Get("AutoHideGameObjects-HidePlayerHelp"));

            if (ImGui.Checkbox(Lang.Get("AutoHideGameObjects-HideUnimportantENPC"), ref config.DefaultConfig.HideUnimportantENPC))
                config.Save(this);
            ImGuiOm.TooltipHover(Lang.Get("AutoHideGameObjects-HideUnimportantENPCHelp"));

            if (ImGui.Checkbox(Lang.Get("AutoHideGameObjects-HidePet"), ref config.DefaultConfig.HidePet))
                config.Save(this);
            ImGuiOm.TooltipHover(Lang.Get("AutoHideGameObjects-HidePetHelp"));

            if (ImGui.Checkbox(Lang.Get("AutoHideGameObjects-HideChocobo"), ref config.DefaultConfig.HideChocobo))
                config.Save(this);
            ImGuiOm.TooltipHover(Lang.Get("AutoHideGameObjects-HideChocoboHelp"));
        }
    }

    private void* UpdateObjectArraysDetour(GameObjectManager* objectManager)
    {
        var orig = UpdateObjectArraysHook.Original(objectManager);
        UpdateAllObjects(objectManager);
        return orig;
    }

    private void UpdateAllObjects(GameObjectManager* manager)
    {
        if (manager == null) return;
        if (!GameState.IsLoggedIn) return;

        if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent)
        {
            if (GameState.ContentFinderCondition != 0 ||
                GameState.IsInPVPArea                 ||
                GameState.TerritoryIntendedUse == TerritoryIntendedUse.IslandSanctuary)
                return;
        }
        else
        {
            // 在两歧塔里
            if ((LocalPlayerState.Object?.Position.Y ?? -100) < 0)
                return;
        }

        var playerCount   = 0;
        var targetAddress = TargetManager.Target?.Address ?? nint.Zero;

        if (GameState.TerritoryIntendedUse                == TerritoryIntendedUse.OccultCrescent &&
            (LocalPlayerState.Object?.Position.Y ?? -100) < 0)
            return;

        for (var index = 0; index < manager->Objects.IndexSorted.Length; index++)
        {
            if (index > 629)
                break;

            if (index is > 200 and < 489)
            {
                index = 488;
                continue;
            }

            var entry = manager->Objects.IndexSorted[index];

            if (GameState.TerritoryIntendedUse == TerritoryIntendedUse.OccultCrescent)
            {
                if (!ShouldFilterOccultCrescent(entry.Value, targetAddress, ref playerCount, (uint)index))
                    continue;
            }
            else
            {
                if (!ShouldFilter(config.DefaultConfig, entry.Value, (uint)index))
                    continue;
            }

            entry.Value->RenderFlags |= (VisibilityFlags)256;
            processedObjects.Add((nint)entry.Value);
        }
    }

    private static bool ShouldFilter(FilterConfig config, GameObject* gameObject, uint index)
    {
        if (gameObject == null) return false;

        if (gameObject->EntityId == LocalPlayerState.EntityID) return false;

        if (((RenderFlag)gameObject->RenderFlags).IsSet(RenderFlag.Invisible)) return false;

        if (gameObject->NamePlateIconId != 0) return false;

        // 玩家
        if (config.HidePlayer             &&
            index                  <= 200 &&
            index % 2              == 0   &&
            gameObject->ObjectKind == ObjectKind.Pc)
        {
            var player = (BattleChara*)gameObject;

            if (player->IsFriend)
                return false;

            if (LocalPlayerState.IsInParty &&
                (player->IsPartyMember || player->IsAllianceMember))
                return false;

            return true;
        }

        // 宠物
        if (config.HidePet                             &&
            index                  <= 200              &&
            index % 2              == 1                &&
            gameObject->ObjectKind != ObjectKind.Mount &&
            gameObject->OwnerId    != LocalPlayerState.EntityID)
            return true;

        // 陆行鸟
        if (config.HideChocobo                                                &&
            index                                 <= 200                      &&
            index % 2                             == 0                        &&
            gameObject->ObjectKind                == ObjectKind.BattleNpc     &&
            (BattleNpcSubKind)gameObject->SubKind == BattleNpcSubKind.Buddy   &&
            gameObject->OwnerId                   != LocalPlayerState.EntityID)
            return true;

        // 不重要 NPC
        if (config.HideUnimportantENPC                                              &&
            index is >= 489 and <= 629                                              &&
            !gameObject->TargetableStatus.IsSet(ObjectTargetableFlags.IsTargetable) &&
            gameObject->EventHandler == null)
            return true;

        return false;
    }

    private bool ShouldFilterOccultCrescent(GameObject* gameObject, nint targetAddress, ref int playerCount, uint index)
    {
        if (gameObject == null) return false;

        if (gameObject->EntityId == LocalPlayerState.EntityID) return false;

        if (gameObject->NamePlateIconId != 0) return false;

        // 玩家
        if (index                  <= 200 &&
            index % 2              == 0   &&
            gameObject->ObjectKind == ObjectKind.Pc)
        {
            var player = (BattleChara*)gameObject;

            playerCount++;

            if (player->IsDead() || (nint)gameObject == targetAddress)
            {
                gameObject->RenderFlags &= ~(VisibilityFlags)256;
                processedObjects.Remove((nint)gameObject);
                return false;
            }

            if (player->IsFriend)
                return false;

            if (LocalPlayerState.IsInParty &&
                (player->IsPartyMember || player->IsAllianceMember))
                return false;

            return playerCount >= 10;
        }

        // 不重要 NPC
        if (index is >= 489 and <= 629                                              &&
            !gameObject->TargetableStatus.IsSet(ObjectTargetableFlags.IsTargetable) &&
            gameObject->EventHandler == null)
            return true;

        // 其他玩家的召唤物
        if (gameObject->ObjectKind == ObjectKind.BattleNpc      &&
            index                  <= 200                       &&
            index % 2              == 0                         &&
            gameObject->ObjectKind == ObjectKind.BattleNpc      &&
            gameObject->OwnerId    != LocalPlayerState.EntityID &&
            gameObject->OwnerId    != 0                         &&
            gameObject->OwnerId    != 0xE0000000)
            return true;

        return false;
    }

    private void ResetAllObjects()
    {
        if (!DService.Instance().ClientState.IsLoggedIn || processedObjects.Count == 0) return;

        var manager = GameObjectManager.Instance();
        if (manager == null) return;

        foreach (ref var entry in GameObjectManager.Instance()->Objects.IndexSorted)
        {
            if (entry.Value       == null                                            ||
                (nint)entry.Value == (LocalPlayerState.Object?.Address ?? nint.Zero) ||
                !processedObjects.Contains((nint)entry.Value))
                continue;

            entry.Value->RenderFlags &= ~(VisibilityFlags)256;
        }

        processedObjects.Clear();
        zoneUpdateCount = 0;
    }

    private void OnZoneChanged(uint u)
    {
        zoneUpdateCount = 0;
        processedObjects.Clear();
    }

    private void OnUpdate(IFramework _)
    {
        // 主要是小区域更新不及时
        if (zoneUpdateCount > 3 || DService.Instance().Condition.IsBetweenAreas) return;

        zoneUpdateCount++;
        UpdateAllObjects(GameObjectManager.Instance());
    }
    
    private class Config : ModuleConfig
    {
        public FilterConfig DefaultConfig = new();
    }

    private class FilterConfig
    {
        // 陆行鸟
        public bool HideChocobo = true;

        // 宠物
        public bool HidePet = true;

        // 玩家
        public bool HidePlayer = true;

        // 不重要 NPC
        public bool HideUnimportantENPC = true;
    }

    private enum RenderFlag
    {
        Invisible = 256
    }
}
