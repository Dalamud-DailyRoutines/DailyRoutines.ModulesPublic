using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using KamiToolKit.Classes;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.OmenService.ZoneIndicator;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace DailyRoutines.ModulesPublic.Duty;

public unsafe class HullbreakerIsleHelper : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = Lang.Get("HullbreakerIsleHelperTitle"),
        Description = Lang.Get
        (
            "HullbreakerIsleHelperDescription",
            LuminaWrapper.GetContentName(23), // 财宝传说破舰岛
            LuminaWrapper.GetBNPCName(2891),  // 捕兽夹
            LuminaWrapper.GetBNPCName(2896)   // 怪宝箱
        ),
        Category = ModuleCategory.Duty
    };
    
    private ZoneIndicatorHandle? trapHandle;
    private ZoneIndicatorHandle? fakeTreasureHandle;

    protected override void Init()
    {
        trapHandle = ZoneIndicatorRenderer.Instance().RegPermanent
        (
            361,
            () =>
            {
                var director = EventFramework.Instance()->GetContentDirector();
                if (director == null) return [];

                // 已经过了开头了，不用找了
                var todos = director->DirectorTodos;
                if (todos[1].Type != TodoType.Text)
                    return [];

                return CharacterManager.Instance()->FindAll(&FindTrap);
            },
            ptr => new()
            {
                Text      = $"{((GameObject*)ptr)->NameString}",
                TextScale = 1.2f,
                TextColor = KnownColor.OrangeRed.ToVector4()
            },
            options: new()
            {
                Surrounding = new()
                {
                    Type      = ZoneIndicatorSurrounding.Shape.Circle,
                    Radius    = 1.5f,
                    Thickness = 2
                }
            }
        );
        
        fakeTreasureHandle = ZoneIndicatorRenderer.Instance().RegPermanent
        (
            361,
            () =>
            {
                var director = EventFramework.Instance()->GetContentDirector();
                if (director == null) return [];

                // 已经过了开头了，不用找了
                var todos = director->DirectorTodos;
                if (todos[2].Type == TodoType.Text || todos[2].CurrentCount == 3)
                    return [];

                return EventObjectManager.Instance()->FindAll(&FindFakeTreasure);
            },
            ptr => new()
            {
                Text      = $"{((GameObject*)ptr)->NameString} ({Lang.Get("HullbreakerIsleHelper-FakeTreasureTip")})",
                TextScale = 1.2f,
                TextColor = KnownColor.DarkOrange.ToVector4()
            }
        );
        
        return;

        static bool FindTrap(BattleChara* chara) =>
            chara                   != null &&
            chara->NameId           == 2891 &&
            chara->TargetableStatus == (ObjectTargetableFlags)248;

        static bool FindFakeTreasure(GameObject* chara) =>
            chara != null                           &&
            FakeTreasuresID.Contains(chara->BaseId) &&
            chara->GetIsTargetable();
    }

    protected override void Uninit()
    {
        trapHandle?.Unreg();
        trapHandle = null;
        
        fakeTreasureHandle?.Unreg();
        fakeTreasureHandle = null;
    }
    
    #region 常量

    private static readonly FrozenSet<uint> FakeTreasuresID = [2004074, 2004075, 2004076, 2004077, 2004078, 2004079];

    #endregion
}
