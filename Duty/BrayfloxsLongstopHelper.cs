using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using OmenTools.Info.Game.Packets.Upstream;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;

namespace DailyRoutines.ModulesPublic.Duty;

public unsafe class BrayfloxsLongstopHelper : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = Lang.Get("BrayfloxsLongstopHelperTitle"),
        Description = Lang.Get
        (
            "BrayfloxsLongstopHelperDescription",
            LuminaWrapper.GetContentName(8),        // 休养胜地布雷福洛克斯野营地
            LuminaWrapper.GetBNPCName(1298),        // 哥布林寻路人
            LuminaWrapper.GetEventItemName(2000521) // 避难地正门的钥匙
        ),
        Category            = ModuleCategory.Duty,
        ModulesPrerequisite = ["AutoTalkSkip"]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    protected override void Init()
    {
        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
    }

    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        FrameworkManager.Instance().Unreg(OnUpdate);
    }

    private static void OnZoneChanged(uint zone)
    {
        FrameworkManager.Instance().Unreg(OnUpdate);

        if (GameState.TerritoryType != 1041) return;

        FrameworkManager.Instance().Reg(OnUpdate, 100);
    }

    private static void OnUpdate(IFramework framework)
    {
        var director = EventFramework.Instance()->GetContentDirector();
        if (director == null) return;

        if (director->DirectorTodos[0].CurrentCount != 1)
        {
            var chara = CharacterManager.Instance()->FindFirst(&FindNPC);
            if (chara != null)
                new EventStartPackt(chara->EntityId, 1638401).Send();
        }
        else
            FrameworkManager.Instance().Unreg(OnUpdate);

        return;

        static bool FindNPC(BattleChara* chara) =>
            chara               != null                &&
            chara->ObjectKind   == ObjectKind.EventNpc &&
            chara->BaseId       == 1004346             &&
            chara->NextDistance <= 4;
    }
}
