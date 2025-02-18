using System;
using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;

namespace DailyRoutines.Modules;

public unsafe class AutoPlayerCommend : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoPlayerCommendTitle"),
        Description = GetLoc("AutoPlayerCommendDescription"),
        Category = ModuleCategories.Combat,
    };
    
    private static readonly AssignPlayerCommendationMenu AssignPlayerCommendationItem = new();
    
    private static Config ModuleConfig = null!;
    
    private static string ContentSearchInput = string.Empty;

    private static ulong AssignedCommendationContentID;

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        TaskHelper ??= new TaskHelper { TimeLimitMS = 10_000 };
        
        DService.ClientState.TerritoryChanged += OnZoneChanged;
        DService.ContextMenu.OnMenuOpened  += OnMenuOpen;
        DService.DutyState.DutyCompleted   += OnDutyComplete;
    }
    
    public override void ConfigUI()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("AutoPlayerCommend-BlacklistContents")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(300f * GlobalFontScale);
        ContentSelectCombo(ref ModuleConfig.BlacklistContentZones, ref ContentSearchInput);
        
        ImGui.Spacing();
        
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("AutoPlayerCommend-BlockBlacklistPlayers")}:");
        
        ImGui.SameLine();
        ImGui.Checkbox("###AutoIgnoreBlacklistPlayers", ref ModuleConfig.AutoIgnoreBlacklistPlayers);
    }
    
    private void OnZoneChanged(ushort zone) => AssignedCommendationContentID = 0;
    
    private static void OnMenuOpen(IMenuOpenedArgs args)
    {
        if (!AssignPlayerCommendationItem.IsDisplay(args)) return;
        args.AddMenuItem(AssignPlayerCommendationItem.Get());
    }

    private void OnDutyComplete(object? sender, ushort dutyZoneID)
    {
        if (InterruptByConflictKey(TaskHelper, this)) return;
        if (ModuleConfig.BlacklistContentZones.Contains(dutyZoneID)) return;
        if (DService.PartyList.Length <= 1) return;

        var orig = DService.GameConfig.UiConfig.GetUInt("MipDispType");
        TaskHelper.Enqueue(() => SetMIPDisplayType(),     "设置最优队员推荐不显示列表");
        TaskHelper.Enqueue(OpenCommendWindow,             "打开最优队员推荐列表");
        TaskHelper.Enqueue(EnqueueCommendation,           "给予最优队员推荐");
        TaskHelper.Enqueue(() => SetMIPDisplayType(orig), "还原原始最优队友推荐设置");
    }

    private static bool? OpenCommendWindow()
    {
        var notification    = GetAddonByName("_Notification");
        var notificationMvp = GetAddonByName("_NotificationIcMvp");
        if (notification == null && notificationMvp == null) return true;

        Callback(notification, true, 0, 11);
        return true;
    }
    
    private static bool? EnqueueCommendation()
    {
        if (!IsAddonAndNodesReady(VoteMvp)) return false;
        if (!AgentModule.Instance()->GetAgentByInternalId(AgentId.ContentsMvp)->IsAgentActive()) return false;
        if (DService.ClientState.LocalPlayer is not { } localPlayer) return false;
        
        var hudMembers = AgentHUD.Instance()->PartyMembers.ToArray();
        Dictionary<(string Name, uint HomeWorld, uint ClassJob, byte RoleRaw, PlayerRole Role, ulong ContentID), int>
            partyMembers = [];
        foreach (var member in DService.PartyList)
        {
            if ((ulong)member.ContentId == localPlayer.ToStruct()->ContentId) continue;

            var index = Math.Clamp(hudMembers.IndexOf(x => x.ContentId == (ulong)member.ContentId) - 1, 0, 6);
            
            var rawRole = member.ClassJob.Value.Role;
            partyMembers[
                (member.Name.ExtractText(), member.World.RowId, member.ClassJob.RowId, rawRole,
                    GetCharacterJobRole(rawRole), (ulong)member.ContentId)] = index;
        }
        
        if (partyMembers.Count == 0) return true;

        var blacklistPlayers = GetBlacklistPlayerContentIDs();
        // 优先级排序
        var playersToCommend = partyMembers
                               .Where(x => !ModuleConfig.AutoIgnoreBlacklistPlayers || 
                                           !blacklistPlayers.Contains(x.Key.ContentID))
                               // 优先已指定、职业相同或职能相同
                               .OrderByDescending(x =>
                               {
                                   if (AssignedCommendationContentID != 0 &&
                                       x.Key.ContentID               == AssignedCommendationContentID)
                                       return 2;
                                   if (localPlayer.ClassJob.RowId            == x.Key.ClassJob ||
                                       localPlayer.ClassJob.Value.Role == x.Key.RoleRaw)
                                       return 1;
                                   return 0;
                               })
                               // 计算对位
                               .ThenByDescending(x => GetCharacterJobRole(localPlayer.ClassJob.Value.Role) switch
                               {
                                   PlayerRole.Tank or PlayerRole.Healer
                                       => x.Key.Role
                                              is PlayerRole.Tank or PlayerRole.Healer
                                              ? 1
                                              : 0,
                                   PlayerRole.DPS => x.Key.Role switch
                                   {
                                       PlayerRole.DPS    => 3,
                                       PlayerRole.Healer => 2,
                                       PlayerRole.Tank   => 1,
                                       _                 => 0,
                                   },
                                   _ => 0,
                               })
                               .Select(x => new
                               {
                                   x.Key.Name,
                                   x.Key.ClassJob
                               })
                               .ToList();
        if (playersToCommend.Count == 0) return true;
        
        foreach (var memberInfo in playersToCommend)
        {
            if (!TryFindPlayerIndex(memberInfo.Name, memberInfo.ClassJob, out var playerIndex)) continue;
            if (!LuminaCache.TryGetRow<ClassJob>(memberInfo.ClassJob, out var job)) continue;
            
            SendEvent(AgentId.ContentsMvp, 0, 0, playerIndex);
            Chat(GetSLoc("AutoPlayerCommend-NoticeMessage", 
                         job.ToBitmapFontIcon(), job.Name.ExtractText(), memberInfo.Name));
            return true;
        }

        ChatError(GetLoc("AutoPlayerCommend-ErrorWhenGiveCommendationMessage"));
        return true;

        bool TryFindPlayerIndex(string playerName, uint playerJob, out int playerIndex)
        {
            playerIndex = -1;

            var count = VoteMvp->AtkValues[1].UInt;
            for (var i = 0; i < count; i++)
            {
                var isEnabled = VoteMvp->AtkValues[16 + i].UInt == 1;
                if (!isEnabled) continue;
                
                var name      = string.Empty;
                try { name = SeString.Parse(VoteMvp->AtkValues[9 + i].String).ExtractText(); }
                catch { name = string.Empty; }
                if (string.IsNullOrWhiteSpace(name) || name != playerName) continue;
                
                var classJob  = VoteMvp->AtkValues[2 + i].UInt - 62100;
                if (classJob <= 0 || classJob != playerJob) continue;
                
                playerIndex = i;
                return true;
            }
            
            return false;
        }
    }

    private static void SetMIPDisplayType(uint type = 0)
        => DService.GameConfig.UiConfig.Set("MipDispType", type);
    
    private static HashSet<ulong> GetBlacklistPlayerContentIDs()
        => InfoProxyBlacklist.Instance()->BlockedCharacters.ToArray().Select(x => x.Id).ToHashSet();

    private static PlayerRole GetCharacterJobRole(byte rawRole) =>
        rawRole switch
        {
            1 => PlayerRole.Tank,
            2 or 3 => PlayerRole.DPS,
            4 => PlayerRole.Healer,
            _ => PlayerRole.None,
        };

    public override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        DService.ContextMenu.OnMenuOpened -= OnMenuOpen;
        DService.DutyState.DutyCompleted  -= OnDutyComplete;

        AssignedCommendationContentID = 0;
        
        base.Uninit();
    }

    private enum PlayerRole
    {
        Tank,
        Healer,
        DPS,
        None,
    }

    private class Config : ModuleConfiguration
    {
        public HashSet<uint>    BlacklistContentZones    = [];

        public bool AutoIgnoreBlacklistPlayers = true;
    }
    
    private class AssignPlayerCommendationMenu : MenuItemBase
    {
        public override string Name { get; protected set; } = GetLoc("AutoPlayerCommend-AssignPlayerCommend");

        public override bool IsDisplay(IMenuOpenedArgs args)
        {
            if (!DService.Condition[ConditionFlag.BoundByDuty]) return false;
            if (args.MenuType != ContextMenuType.Default    ||
                args.Target is not MenuTargetDefault target ||
                (target.TargetCharacter == null && target.TargetContentId == 0)) return false;

            return true;
        }

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            if (args.Target is not MenuTargetDefault target) return;
            if (target.TargetCharacter == null && target.TargetContentId == 0) return;
            
            var contentID = target.TargetCharacter != null ? target.TargetCharacter.ContentId : target.TargetContentId;
            var playerName = target.TargetCharacter != null ? target.TargetCharacter.Name : target.TargetName;
            var playerWorld = target.TargetCharacter != null ? target.TargetCharacter.HomeWorld : target.TargetHomeWorld;

            NotificationInfo(GetLoc("AutoPlayerCommend-AssignPlayerCommendMessage", playerName,
                                    playerWorld.Value.Name.ExtractText()));
            AssignedCommendationContentID = contentID;
        }
    }
}
