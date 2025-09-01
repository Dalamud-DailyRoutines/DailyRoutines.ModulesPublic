using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoCheckItemLevel : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoCheckItemLevelTitle"),
        Description = GetLoc("AutoCheckItemLevelDescription"),
        Category    = ModuleCategories.Combat,
    };

    private static readonly HashSet<uint> ValidContentJobCategories = [108, 142, 146];
    private static readonly HashSet<uint> HaveOffHandJobCategories  = [2, 7, 8, 20];

    protected override void Init()
    {
        TaskHelper ??= new() { TimeLimitMS = 20_000 };

        DService.ClientState.TerritoryChanged += OnZoneChanged;
    }

    private void OnZoneChanged(ushort zone)
    {
        TaskHelper.Abort();
        
        if (GameState.IsInPVPArea || GameState.ContentFinderCondition == 0) return;
        if (GameState.ContentFinderConditionData.PvP                                                               ||
            !ValidContentJobCategories.Contains(GameState.ContentFinderConditionData.AcceptClassJobCategory.RowId) ||
            GameState.ContentFinderConditionData.ContentMemberType.Value.MeleesPerParty == 0)
            return;
        
        TaskHelper.Enqueue(() => !BetweenAreas && DService.ObjectTable.LocalPlayer != null, "WaitForEnteringDuty");
        TaskHelper.Enqueue(() => CheckMembersItemLevel([LocalPlayerState.EntityID]));
    }

    private bool CheckMembersItemLevel(HashSet<ulong> checkedMembers)
    {
        var agent        = AgentHUD.Instance();
        var agentInspect = AgentInspect.Instance();
        if (agent == null || agentInspect == null || agent->PartyMemberCount <= 1)
        {
            TaskHelper.Abort();
            return true;
        }

        if (BetweenAreas) return false;

        if (CharacterInspect != null)
        {
            CharacterInspect->Close(true);
            return false;
        }
        
        var members = agent->PartyMembers.ToArray();
        foreach (var member in members)
        {
            if (member.EntityId  == 0                         ||
                member.ContentId == 0                         ||
                member.EntityId  == LocalPlayerState.EntityID ||
                !checkedMembers.Add(member.EntityId))
                continue;

            TaskHelper.Enqueue(() =>
            {
                if (CharacterInspect != null && agentInspect->CurrentEntityId == member.EntityId) return true;

                if (Throttler.Throttle("AutoCheckItemLevel-OpenExamine"))
                {
                    if (CharacterInspect != null)
                    {
                        CharacterInspect->Close(true);
                        Throttler.Throttle("AutoCheckItemLevel-OpenExamine", 10, true);
                    }
                    else
                        agentInspect->ExamineCharacter(member.EntityId);
                }
                
                return false;
            }, "打开检视界面");

            TaskHelper.Enqueue(() =>
            {
                if (member.Object == null) return false;
                if (!TryGetInventoryItems([InventoryType.Examine], _ => true, out var list)) return false;

                while (list.Count < 13)
                    list.Add(new());
                
                uint totalIL        = 0U, lowestIL = 9999U;
                var  itemSlotAmount = 11;

                for (var i = 0; i < 13; i++)
                {
                    var slot   = list[i];
                    var itemID = slot.ItemId;

                    if (!LuminaGetter.TryGetRow(itemID, out Item item)) continue;

                    switch (i)
                    {
                        case 0:
                        {
                            var category = item.ClassJobCategory.RowId;
                            if (HaveOffHandJobCategories.Contains(category))
                                itemSlotAmount++;

                            break;
                        }
                        case 1 when itemSlotAmount != 12:
                        case 5: // 腰带
                            continue;
                    }

                    if (item.LevelItem.RowId < lowestIL)
                        lowestIL = item.LevelItem.RowId;

                    totalIL += item.LevelItem.RowId;
                }

                var avgItemLevel = (uint)(totalIL / itemSlotAmount);
                
                SendNotification(member, avgItemLevel, lowestIL);
                
                CharacterInspect->Close(true);
                agentInspect->FetchCharacterDataStatus = 0;
                agentInspect->FetchSearchCommentStatus = 0;
                agentInspect->FetchCharacterDataStatus = 0;
                
                return true;
            }, "检查装等");

            var checkedCount = checkedMembers.Count - 1;
            if (checkedCount != 0 && checkedCount % 3 == 0)
                TaskHelper.DelayNext(1000, "等待 1 秒");
            
            TaskHelper.Enqueue(() => CheckMembersItemLevel(checkedMembers), "进入新循环");
            return true;
        }
        
        TaskHelper.Abort();
        return true;
    }

    private static void SendNotification(HudPartyMember partyMember, uint avgIL, uint lowIL)
    {
        if (partyMember.Object == null) return;
        
        var content = GameState.ContentFinderConditionData;
        if (content.RowId == 0) return;
        
        var ssb = new SeStringBuilder();

        ssb.AddUiForeground(25)
           .Add(new PlayerPayload(partyMember.Name.ExtractText(), partyMember.Object->HomeWorld))
           .AddUiForegroundOff();
        
        ssb.Append($" ({LuminaWrapper.GetJobName(partyMember.Object->ClassJob)})");

        var level = partyMember.Object->Level;
        ssb.Append($" {GetLoc("Level")}: ")
           .AddUiForeground(level.ToString(), (ushort)(level >= content.ClassJobLevelSync ? 43 : 17));

        ssb.Add(new NewLinePayload());
        
        ssb.Append($" {GetLoc("ILAverage")}: ")
           .AddUiForeground(avgIL.ToString(), (ushort)(avgIL > content.ItemLevelSync ? 43 : 17));

        ssb.Append($" {GetLoc("ILMinimum")}: ")
           .AddUiForeground(lowIL.ToString(), (ushort)(lowIL > content.ItemLevelRequired ? 43 : 17));

        ssb.Add(new NewLinePayload());

        Chat(ssb.Build());
    }

    protected override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnZoneChanged;

        base.Uninit();
    }
}
