using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OmenTools.Dalamud;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using AtkEventWrapper = OmenTools.OmenService.AtkEventWrapper;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedCharacterClass : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("OptimizedCharacterClassTitle"),
        Description = Lang.Get("OptimizedCharacterClassDescription"),
        Category    = ModuleCategory.UIOptimization,
        Author      = ["Middo"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private readonly List<AtkEventWrapper> events = [];

    protected override void Init()
    {
        TaskHelper ??= new();

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "CharacterClass", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "CharacterClass", OnAddon);
        if (CharacterClass->IsAddonAndNodesReady())
            OnAddon(AddonEvent.PostSetup, null);

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "PvPCharacter", OnAddonPVP);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "PvPCharacter", OnAddonPVP);
        if (PvPCharacter->IsAddonAndNodesReady())
            OnAddonPVP(AddonEvent.PostSetup, null);
    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon, OnAddonPVP);

        ClearEvents();
    }

    private void AddCollisionEvent(AtkUnitBase* addon, AtkComponentNode* componentNode, uint classJobID)
    {
        if (!LuminaGetter.TryGetRow(classJobID, out ClassJob classJob)) return;

        var colNode = componentNode->Component->UldManager.SearchSimpleNodeByType<AtkCollisionNode>(NodeType.Collision);
        if (colNode == null) return;

        var iconNodes = componentNode->Component->UldManager.SearchSimpleNodesByType(NodeType.Image);
        if (iconNodes is { Count: 0 }) return;

        colNode->AtkEventManager.ClearEvents();

        var iconNode = (AtkImageNode*)iconNodes.Last();

        var clickEvent = new AtkEventWrapper
        ((_, _, _, _) =>
            {
                DLog.Debug($"[{nameof(OptimizedCharacterClass)}] 切换至职业 {classJob.Name} ({classJobID})");
                LocalPlayerState.SwitchGearset(classJobID);
                UIGlobals.PlaySoundEffect(1);
            }
        );
        clickEvent.Add(addon, (AtkResNode*)colNode, AtkEventType.MouseClick);

        var cursorOverEvent = new AtkEventWrapper
        ((_, ownerAddon, _, _) =>
            {
                DService.Instance().AddonEvent.SetCursor(AddonCursorType.Clickable);
                UIGlobals.PlaySoundEffect(0);
                AtkStage.Instance()->TooltipManager.ShowTooltip
                (
                    ownerAddon->Id,
                    (AtkResNode*)iconNode,
                    new ReadOnlySeString
                    (
                        new SeStringBuilder()
                            .AddIcon(classJob.ToBitmapFontIcon())
                            .AddText($" {classJob.Name}")
                            .Build()
                            .Encode()
                    )
                );
            }
        );
        cursorOverEvent.Add(addon, (AtkResNode*)colNode, AtkEventType.MouseOver);

        var cursorOutEvent = new AtkEventWrapper
        ((_, ownerAddon, _, _) =>
            {
                DService.Instance().AddonEvent.ResetCursor();
                AtkStage.Instance()->TooltipManager.HideTooltip(ownerAddon->Id);
            }
        );
        cursorOutEvent.Add(addon, (AtkResNode*)colNode, AtkEventType.MouseOut);

        events.Add(clickEvent, cursorOverEvent, cursorOutEvent);
    }

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                if (CharacterClass == null) return;
                if (events is not { Count: 0 }) return;

                TaskHelper.Enqueue
                (() =>
                    {
                        if (!CharacterClass->IsAddonAndNodesReady()) return false;

                        foreach (var (nodeID, classJobID) in ClassJobComponentMap)
                        {
                            var componentNode = CharacterClass->GetComponentNodeById(nodeID);
                            if (componentNode == null) continue;

                            AddCollisionEvent(CharacterClass, componentNode, classJobID);
                        }

                        return true;
                    }
                );

                break;
            case AddonEvent.PreFinalize:
                ClearEvents();
                break;
        }
    }

    private void OnAddonPVP(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                if (PvPCharacter == null) return;
                if (events is not { Count: 0 }) return;

                TaskHelper.Enqueue
                (() =>
                    {
                        if (!PvPCharacter->IsAddonAndNodesReady()) return false;

                        foreach (var (nodeID, classJobID) in PVPClassJobComponentMap)
                        {
                            var componentNode = PvPCharacter->GetComponentNodeById(nodeID);
                            if (componentNode == null) continue;

                            AddCollisionEvent(PvPCharacter, componentNode, classJobID);
                        }

                        return true;
                    }
                );

                break;
            case AddonEvent.PreFinalize:
                ClearEvents();
                break;
        }
    }

    private void ClearEvents()
    {
        foreach (var atkEvent in events.ToList())
            atkEvent.Dispose();

        events.Clear();
    }
    
    #region 常量

    private static readonly FrozenDictionary<uint, uint> ClassJobComponentMap = new Dictionary<uint, uint>
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
    }.ToFrozenDictionary();

    private static readonly FrozenDictionary<uint, uint> PVPClassJobComponentMap = new Dictionary<uint, uint>
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
        [70] = 42  // 绘灵法师
    }.ToFrozenDictionary();

    #endregion
}
