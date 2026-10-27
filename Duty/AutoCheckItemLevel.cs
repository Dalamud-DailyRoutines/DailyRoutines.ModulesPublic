using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OmenTools.Info.Lumina;
using OmenTools.Interop.Game.ExecuteCommand.Implementations;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoCheckItemLevel : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoCheckItemLevelTitle"),
        Description = Lang.Get("AutoCheckItemLevelDescription"),
        Category    = ModuleCategory.Duty
    };

    protected override void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 20_000 };

        IClientState.Instance().TerritoryChanged += OnZoneChanged;
    }

    protected override void Uninit() =>
        IClientState.Instance().TerritoryChanged -= OnZoneChanged;

    private void OnZoneChanged
    (
        uint zone
    )
    {
        TaskHelper.Abort();

        if (GameState.IsInPVPArea                                                                                  ||
            GameState.ContentFinderCondition == 0                                                                  ||
            GameState.ContentFinderConditionData.PvP                                                               ||
            !ValidContentJobCategories.Contains(GameState.ContentFinderConditionData.AcceptClassJobCategory.RowId) ||
            GameState.ContentFinderConditionData.ContentMemberType.Value.MeleesPerParty == 0                       ||
            ICondition.Instance()[ConditionFlag.DutyRecorderPlayback])
            return;

        TaskHelper.Enqueue
        (
            () => !ICondition.Instance().IsBetweenAreas &&
                  IObjectTable.Instance().LocalPlayer != null,
            "等待进入副本"
        );

        TaskHelper.Enqueue(StartInspect, "开始检查");
    }

    private bool StartInspect()
    {
        var group = GroupManager.Instance()->GetGroup();

        if (group == null || group->MemberCount <= 1)
        {
            TaskHelper.Abort();
            return true;
        }

        var results = new List<MemberItemLevel>();

        for (var index = 0; index < group->MemberCount; index++)
        {
            var member = group->GetPartyMemberByIndex(index);

            if (member            == null ||
                member->EntityId  == 0    ||
                member->ContentId == 0)
                continue;

            var entityID = member->EntityId;

            TaskHelper.Enqueue
            (
                () =>
                {
                    InspectCommand.Inspect(entityID);
                    return true;
                },
                "请求检视"
            );

            TaskHelper.Enqueue(() => ReadMemberItemLevel(entityID, member, results), "读取装等");
        }

        TaskHelper.Enqueue
        (
            () =>
            {
                SendNotifications(results);
                return true;
            },
            "发送通知"
        );

        return true;
    }

    private static bool ReadMemberItemLevel
    (
        uint                  entityID,
        PartyMember*          member,
        List<MemberItemLevel> results
    )
    {
        if (UIState.Instance()->Inspect.EntityId != entityID) return false;
        if (!TryGetItemLevel(out var avgItemLevel, out var lowestItemLevel)) return false;

        results.Add(new(member, avgItemLevel, lowestItemLevel));

        return true;
    }

    private static bool TryGetItemLevel
    (
        out uint avgItemLevel,
        out uint lowestItemLevel
    )
    {
        avgItemLevel    = 0;
        lowestItemLevel = 0;

        var container = InventoryManager.Instance()->GetInventoryContainer(InventoryType.Examine);
        if (container == null || !container->IsLoaded) return false;

        uint totalItemLevel = 0;
        var  itemSlotAmount = 11;
        var  lowestIL       = 9999U;

        for (var index = 0; index < 13 && index < container->Size; index++)
        {
            var slot = container->GetInventorySlot(index);
            if (slot == null) continue;

            if (!LuminaGetter.TryGetRow(slot->ItemId, out Item item)) continue;

            switch (index)
            {
                case 0:
                {
                    if (HaveOffHandJobCategories.Contains(item.ClassJobCategory.RowId))
                        itemSlotAmount++;

                    break;
                }
                case 1 when itemSlotAmount != 12:
                case 5:
                    continue;
            }

            if (item.LevelItem.RowId < lowestIL)
                lowestIL = item.LevelItem.RowId;

            totalItemLevel += item.LevelItem.RowId;
        }

        avgItemLevel    = totalItemLevel / (uint)itemSlotAmount;
        lowestItemLevel = lowestIL;

        return true;
    }

    private static void SendNotifications
    (
        List<MemberItemLevel> results
    )
    {
        var content = GameState.ContentFinderConditionData;

        var title = Lang.Get
        (
            "AutoCheckItemLevel-Notification-Title",
            new Dictionary<string, object>
            {
                ["memberCount"] = results.Count,
            }
        );
        
        var minIL = 0U;
        if (content.ItemLevelRequired > 0)
            minIL = content.ItemLevelRequired;
        else if (content.ClassJobLevelRequired > 0)
        {
            minIL = Sheets.Gears.Values
                          .Where
                          (x => x.LevelEquip      != 1                             &&
                                x.LevelEquip      == content.ClassJobLevelRequired &&
                                x.LevelItem.RowId != 1
                          )
                          .OrderBy(x => x.LevelItem.RowId)
                          .FirstOrDefault()
                          .LevelItem.RowId;
        }
        
        var hasOutput = false;

        using var rented        = new RentedSeStringBuilder();
        using var payloadRented = new RentedSeStringBuilder();

        foreach (var result in results)
        {
            var member = result.Member;

            var isLevelAbnormal = content.ClassJobLevelRequired < content.ClassJobLevelSync &&
                                  member->Level                 <= content.ClassJobLevelRequired;
            var isMinILAbnormal = result.LowestItemLevel <= minIL;
            if (content.ClassJobLevelSync     == 0 ||
                content.ClassJobLevelRequired == content.ClassJobLevelSync)
                isLevelAbnormal = false;

            payloadRented.AppendIcon((uint)LuminaGetter.GetRowOrDefault<ClassJob>(member->ClassJob).ToBitmapFontIcon())
                         .Append(ReadOnlySeString.CreatePlayer(member->NameString, member->HomeWorld));

            var playerLink = ReadOnlySeString.CreatePlayerLink
            (
                member->NameString,
                member->HomeWorld,
                payloadRented.ToReadOnlySeString()
            );
            payloadRented.Clear();

            var levelText = !isLevelAbnormal ?
                                member->Level.ToString() :
                                payloadRented.PushColorType(COLOR_TYPE_LEVEL_LOW)
                                             .Append(member->Level.ToString())
                                             .PopColorType()
                                             .ToReadOnlySeString();
            payloadRented.Clear();
            
            var minILText = !isMinILAbnormal ?
                                result.LowestItemLevel.ToString() :
                                payloadRented.PushColorType(COLOR_TYPE_LEVEL_LOW)
                                             .Append(result.LowestItemLevel.ToString())
                                             .PopColorType()
                                             .ToReadOnlySeString();
            payloadRented.Clear();

            var message = Lang.GetSe
            (
                "AutoCheckItemLevel-Notification-Message",
                new Dictionary<string, object>
                {
                    ["levelText"]  = levelText,
                    ["player"] = playerLink,
                    ["minILText"]  = minILText,
                    ["avgIL"]  = result.AverageItemLevel
                }
            );

            if (!hasOutput)
                rented.Append(title);

            rented.AppendNewLine();

            hasOutput = true;
            rented.Append(message);
        }

        if (hasOutput)
            NotifyHelper.Instance().Chat(rented.ToReadOnlySeString(), false);
    }

    private readonly struct MemberItemLevel
    (
        PartyMember* member,
        uint         averageItemLevel,
        uint         lowestItemLevel
    )
    {
        public readonly PartyMember* Member           = member;
        public readonly uint         AverageItemLevel = averageItemLevel;
        public readonly uint         LowestItemLevel  = lowestItemLevel;
    }

    #region 常量

    private static readonly uint[] ValidContentJobCategories = [108, 142, 146];
    private static readonly uint[] HaveOffHandJobCategories  = [2, 7, 8, 20];

    private const uint COLOR_TYPE_LEVEL_LOW = 32;

    #endregion
}
