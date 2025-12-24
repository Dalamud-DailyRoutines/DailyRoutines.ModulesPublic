using System.Collections.Generic;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedCharacterClass : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("OptimizedCharacterClassTitle"),
        Description = GetLoc("OptimizedCharacterClassDescription"),
        Category    = ModuleCategories.UIOptimization,
        Author      = ["Middo"]
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private static readonly List<IAddonEventHandle> EventHandles = [];
    
    protected override void Init()
    {
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "CharacterClass", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "CharacterClass", OnAddon);
        if (IsAddonAndNodesReady(CharacterClass))
            OnAddon(AddonEvent.PostSetup, null);
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "PvPCharacter", OnAddonPVP);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "PvPCharacter", OnAddonPVP);
        if (IsAddonAndNodesReady(PvPCharacter))
            OnAddonPVP(AddonEvent.PostSetup, null);
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        OnAddon(AddonEvent.PreFinalize, null);
        
        DService.AddonLifecycle.UnregisterListener(OnAddonPVP);
        OnAddonPVP(AddonEvent.PreFinalize, null);
    }

    private static void AddCollisionEvent(AtkUnitBase* addon, AtkComponentNode* componentNode, uint classJobID)
    {
        var colNode = (AtkCollisionNode*)componentNode->Component->UldManager.SearchSimpleNodeByType(NodeType.Collision);
        if (colNode == null) return;

        var evt = colNode->AtkEventManager.Event;
        while (evt != null)
        {
            if (evt->State.EventType is AtkEventType.MouseClick or AtkEventType.InputReceived)
                colNode->RemoveEvent(evt->State.EventType, evt->Param, evt->Listener, false);

            evt = evt->NextEvent;
        }

        if (DService.AddonEvent.AddEvent((nint)addon, (nint)colNode, AddonEventType.MouseClick,
                                         (_, _) => LocalPlayerState.SwitchGearset(classJobID)) is { } clickHandler)
            EventHandles.Add(clickHandler);
    }

    private static void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                if (CharacterClass == null) return;
                
                foreach (var (nodeID, classJobID) in ClassJobComponentMap)
                {
                    var componentNode = CharacterClass->GetComponentNodeById(nodeID);
                    if (componentNode == null) continue;

                    AddCollisionEvent(CharacterClass, componentNode, classJobID);
                }

                break;
            case AddonEvent.PreFinalize:
                foreach (var handle in EventHandles)
                    DService.AddonEvent.RemoveEvent(handle);
                EventHandles.Clear();
                break;
        }
    }
    
    private static void OnAddonPVP(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                if (PvPCharacter == null) return;
                
                foreach (var (nodeID, classJobID) in PVPClassJobComponentMap)
                {
                    var componentNode = PvPCharacter->GetComponentNodeById(nodeID);
                    if (componentNode == null) continue;

                    AddCollisionEvent(PvPCharacter, componentNode, classJobID);
                }

                break;
            case AddonEvent.PreFinalize:
                foreach (var handle in EventHandles)
                    DService.AddonEvent.RemoveEvent(handle);
                EventHandles.Clear();
                break;
        }
    }

    private static readonly Dictionary<uint, uint> ClassJobComponentMap = new()
    {
        [8]  = 19, // 骑士
        [10] = 21, // 战士
        [12] = 32, // 暗黑骑士
        [14] = 37, // 绝枪战士

        [20] = 24, // 白魔法师
        [22] = 28, // 学者
        [24] = 33, // 占星术士
        [26] = 40, // 贤者

        [32] = 20, // 武僧
        [34] = 22, // 龙骑士
        [36] = 30, // 忍者
        [38] = 34, // 武士
        [40] = 39, // 钐镰客
        [42] = 41, // 蝰蛇剑士

        [48] = 23, // 吟游诗人
        [50] = 31, // 机工士
        [52] = 38, // 舞者

        [58] = 25, // 黑魔法师
        [60] = 27, // 召唤师
        [62] = 35, // 赤魔法师
        [64] = 42, // 绘灵法师
        [66] = 36, // 青魔法师

        [71] = 8,  // 刻木匠
        [72] = 9,  // 锻铁匠
        [73] = 10, // 铸甲匠
        [74] = 11, // 雕金匠
        [75] = 12, // 制革匠
        [76] = 13, // 裁衣匠
        [77] = 14, // 炼金术士
        [78] = 15, // 烹调师

        [84] = 16, // 采矿工
        [86] = 17, // 园艺工
        [88] = 18  // 捕鱼人
    };
    
    private static readonly Dictionary<uint, uint> PVPClassJobComponentMap = new()
    {
        [14] = 19, // 骑士
        [16] = 21, // 战士
        [18] = 32, // 暗黑骑士
        [20] = 37, // 绝枪战士
        
        [26] = 20, // 武僧
        [28] = 22, // 龙骑士
        [30] = 30, // 忍者
        [32] = 34, // 武士
        [34] = 39, // 钐镰客
        [36] = 41, // 蝰蛇剑士

        [42] = 24, // 白魔法师
        [44] = 28, // 学者
        [46] = 33, // 占星术士
        [48] = 40, // 贤者

        [54] = 23, // 吟游诗人
        [56] = 31, // 机工士
        [58] = 38, // 舞者

        [64] = 25, // 黑魔法师
        [66] = 27, // 召唤师
        [68] = 35, // 赤魔法师
        [70] = 42, // 绘灵法师
    };
}
