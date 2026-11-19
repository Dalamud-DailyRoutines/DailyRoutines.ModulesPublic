using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Hooking;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.Interop.Game.Lumina;
using InstanceContentDirector = FFXIVClientStructs.Expansion.FFXIV.Client.Game.InstanceContent.InstanceContentDirector;

namespace DailyRoutines.ModulesPublic;

internal unsafe class AutoPlayerCommend : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoPlayerCommendTitle"),
        Description = Lang.Get("AutoPlayerCommendDescription"),
        Category    = ModuleCategory.Duty
    };

    public override string ModuleName => nameof(AutoPlayerCommend);

    private Config config = null!;

    private Hook<InstanceContentDirector.Delegates.SetCommendationCandidates> SetCommendationCandidatesHook;

    private AssignPlayerCommendationMenu menuItem           = null!;
    private ContentSelectCombo           contentSelectCombo = null!;

    private ulong assignedContentID;

    protected override void Init()
    {
        menuItem           = new(this);
        contentSelectCombo = new("Content");

        config = Config.Load(this) ?? new();

        contentSelectCombo.SelectedIDs = config.BlacklistContents;

        SetCommendationCandidatesHook ??= IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(InstanceContentDirector.MemberFunctionPointers),
            nameof(InstanceContentDirector.SetCommendationCandidates),
            (InstanceContentDirector.Delegates.SetCommendationCandidates)SetCommendationCandidatesDetour
        );
        SetCommendationCandidatesHook.Enable();

        IClientState.Instance().TerritoryChanged += OnZoneChanged;

        ContextMenuManager.Instance().Reg(menuItem);

        TryCommend((InstanceContentDirector*)EventFramework.Instance()->GetInstanceContentDirector());
    }

    protected override void Uninit()
    {
        IClientState.Instance().TerritoryChanged -= OnZoneChanged;

        ContextMenuManager.Instance().Unreg(menuItem);

        assignedContentID = 0;
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoPlayerCommend-BlacklistContents")}");

        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(300f * GlobalUIScale);

            if (contentSelectCombo.DrawCheckbox())
            {
                config.BlacklistContents = [.. contentSelectCombo.SelectedIDs];
                config.Save(this);
            }
        }

        ImGui.NewLine();

        if (ImGui.Checkbox($"{Lang.Get("AutoPlayerCommend-BlockBlacklistPlayers")}", ref config.AutoIgnoreBlacklistPlayers))
            config.Save(this);
    }

    private void OnZoneChanged
    (
        uint territoryID
    ) =>
        assignedContentID = 0;

    private void SetCommendationCandidatesDetour(InstanceContentDirector* director, ulong* contentIDs)
    {
        SetCommendationCandidatesHook.Original(director, contentIDs);
        TryCommend(director);
    }

    private void TryCommend(InstanceContentDirector* director)
    {
        if (director == null                                                    ||
            !director->IsCommendationAvailable()                                ||
            config.BlacklistContents.Contains(GameState.ContentFinderCondition) ||
            assignedContentID == LocalPlayerState.ContentID)
            return;

        var group = GroupManager.Instance()->GetGroupWithCheck();
        if (group == null)
            return;

        var             candidates         = director->CommendationCandidates[..director->CommendationCandidateCount];
        Span<Candidate> eligibleCandidates = stackalloc Candidate[candidates.Length];
        Span<byte>      jobCounts          = stackalloc byte[byte.MaxValue + 1];
        jobCounts.Clear();
        
        var candidateCount = 0;
        foreach (var contentID in candidates)
        {
            if (contentID == LocalPlayerState.ContentID)
                continue;

            var member = group->GetPartyMemberByContentId(contentID);
            if (member              == null                                      ||
                (member->Flags & 1) == 0                                         ||
                !LuminaGetter.TryGetRow<ClassJob>(member->ClassJob, out var job) ||
                job.RowId == 0)
                continue;

            if (config.AutoIgnoreBlacklistPlayers &&
                InfoProxyBlacklist.Instance()->GetBlockResultType(member->AccountId, contentID) != InfoProxyBlacklist.BlockResultType.NotBlocked)
                continue;

            var role = GetPlayerRole(job);
            if (role == PlayerRole.None)
                continue;

            eligibleCandidates[candidateCount++] = new(contentID, member->ClassJob, role);
            jobCounts[member->ClassJob]++;
        }

        var selected = SelectCandidate(eligibleCandidates[..candidateCount], jobCounts);
        if (!selected.HasValue)
            return;

        var candidate      = selected.Value;
        var selectedMember = group->GetPartyMemberByContentId(candidate.ContentID);
        var selectedJob    = LuminaGetter.GetRowOrDefault<ClassJob>(candidate.ClassJob);
        var playerName     = selectedMember->NameString;
        var homeWorld      = selectedMember->HomeWorld;

        director->GiveCommendation(selectedMember->ContentId);

        if (director->CommendationCandidateCount != 0)
            return;

        using var rented = new RentedSeStringBuilder();
        rented.AppendIcon(selectedJob.ToBitmapFontIcon())
              .Append(ReadOnlySeString.CreatePlayer(playerName, homeWorld));

        var message = Lang.GetSe
        (
            "AutoPlayerCommend-Notification-Given",
            new Dictionary<string, object>
            {
                ["player"] = ReadOnlySeString.CreatePlayerLink(playerName, homeWorld, rented.ToReadOnlySeString())
            }
        );

        NotifyHelper.Instance().Chat(message);
        NotifyHelper.Toast(message);
    }

    private Candidate? SelectCandidate(ReadOnlySpan<Candidate> candidates, ReadOnlySpan<byte> jobCounts)
    {
        // 手动指定优先于全部自动选择规则；指定对象不在可推荐名单内时继续自动选择。
        foreach (var candidate in candidates)
        {
            if (candidate.ContentID == assignedContentID)
                return candidate;
        }

        var selfJob = LocalPlayerState.ClassJobData;

        // 自动选择先查找同职业队友，不对候选人评分或两两比较。
        foreach (var candidate in candidates)
        {
            if (candidate.ClassJob == selfJob.RowId)
                return candidate;
        }

        return GetPlayerRole(selfJob) switch
        {
            // 坦克：同职能 > 治疗 > 近战 > 远程物理 > 远程魔法
            PlayerRole.Tank => FindRole
            (
                candidates,
                jobCounts,
                PlayerRole.Tank,
                PlayerRole.Healer,
                PlayerRole.Melee,
                PlayerRole.PhysicalRanged,
                PlayerRole.MagicalRanged
            ),

            // 治疗：同职能 > 坦克 > 近战 > 远程物理 > 远程魔法
            PlayerRole.Healer => FindRole
            (
                candidates,
                jobCounts,
                PlayerRole.Healer,
                PlayerRole.Tank,
                PlayerRole.Melee,
                PlayerRole.PhysicalRanged,
                PlayerRole.MagicalRanged
            ),

            // 近战：同职能 > 远程物理 > 远程魔法 > 治疗 > 坦克
            PlayerRole.Melee => FindRole
            (
                candidates,
                jobCounts,
                PlayerRole.Melee,
                PlayerRole.PhysicalRanged,
                PlayerRole.MagicalRanged,
                PlayerRole.Healer,
                PlayerRole.Tank
            ),

            // 远程物理：同职能 > 远程魔法 > 近战 > 治疗 > 坦克
            PlayerRole.PhysicalRanged => FindRole
            (
                candidates,
                jobCounts,
                PlayerRole.PhysicalRanged,
                PlayerRole.MagicalRanged,
                PlayerRole.Melee,
                PlayerRole.Healer,
                PlayerRole.Tank
            ),

            // 远程魔法：同职能 > 远程物理 > 近战 > 治疗 > 坦克
            PlayerRole.MagicalRanged => FindRole
            (
                candidates,
                jobCounts,
                PlayerRole.MagicalRanged,
                PlayerRole.PhysicalRanged,
                PlayerRole.Melee,
                PlayerRole.Healer,
                PlayerRole.Tank
            ),

            _ => null
        };
    }

    private static Candidate? FindRole
    (
        ReadOnlySpan<Candidate>         candidates,
        ReadOnlySpan<byte>              jobCounts,
        params ReadOnlySpan<PlayerRole> roleOrder
    )
    {
        foreach (var role in roleOrder)
        {
            Candidate? fallback = null;

            foreach (var candidate in candidates)
            {
                if (candidate.Role != role)
                    continue;

                // 同一输出职能内先选未重复的职业；均有重复时保留服务器名单中的第一人。
                fallback ??= candidate;

                if (role is PlayerRole.Tank or PlayerRole.Healer || jobCounts[candidate.ClassJob] == 1)
                    return candidate;
            }

            if (fallback.HasValue)
                return fallback;
        }

        return null;
    }

    private static PlayerRole GetPlayerRole(ClassJob job) =>
        job.JobType switch
        {
            1      => PlayerRole.Tank,
            2 or 6 => PlayerRole.Healer,
            3      => PlayerRole.Melee,
            4      => PlayerRole.PhysicalRanged,
            5      => PlayerRole.MagicalRanged,
            _      => PlayerRole.None
        };

    private readonly record struct Candidate
    (
        ulong      ContentID,
        byte       ClassJob,
        PlayerRole Role
    );

    private class Config : ModuleConfig
    {
        public bool          AutoIgnoreBlacklistPlayers = true;
        public HashSet<uint> BlacklistContents          = [];
    }

    private sealed class AssignPlayerCommendationMenu
    (
        AutoPlayerCommend module
    ) : ContextMenuEntry
    {
        public override string Identifier =>
            nameof(AutoPlayerCommend);

        public override unsafe ContextMenuItem? Create
        (
            ContextMenuOpenedArgs args
        )
        {
            var group = GroupManager.Instance()->GetGroupWithCheck();
            if (GameState.ContentFinderCondition == 0    ||
                group                            == null ||
                group->MemberCount               < 2)
                return null;

            var targetCharacter = args.TargetCharacter;
            if (targetCharacter            == null ||
                targetCharacter->ContentId == 0)
                return null;

            var contentID    = targetCharacter->ContentId;
            var isTargetSelf = contentID                == LocalPlayerState.ContentID;
            var isTargetSame = module.assignedContentID == contentID;

            if (isTargetSame)
            {
                return new()
                {
                    Name = Lang.Get("AutoPlayerCommend-ContextMenu-Auto"),
                    OnClicked = _ =>
                    {
                        module.assignedContentID = 0;

                        var message = Lang.Get("AutoPlayerCommend-Notification-Auto");
                        NotifyHelper.Instance().Chat(message);
                        NotifyHelper.Toast(message);
                    }
                };
            }

            if (isTargetSelf)
            {
                return new()
                {
                    Name = Lang.Get("AutoPlayerCommend-ContextMenu-AssignNobody"),
                    OnClicked = _ =>
                    {
                        module.assignedContentID = LocalPlayerState.ContentID;

                        var message = Lang.Get("AutoPlayerCommend-Notification-AssignedNobody");
                        NotifyHelper.Instance().Chat(message);
                        NotifyHelper.Toast(message);
                    }
                };
            }

            var playerName = targetCharacter->NameString;
            var homeWorld  = targetCharacter->HomeWorld;
            var job        = LuminaGetter.GetRowOrDefault<ClassJob>(targetCharacter->Job);

            return new()
            {
                Name = Lang.Get("AutoPlayerCommend-ContextMenu-AssignPlayer"),
                OnClicked = _ =>
                {
                    module.assignedContentID = contentID;

                    using var rented = new RentedSeStringBuilder();
                    rented.AppendIcon(job.ToBitmapFontIcon())
                          .Append(ReadOnlySeString.CreatePlayer(playerName, homeWorld));

                    var message = Lang.GetSe
                    (
                        "AutoPlayerCommend-Notification-AssignedPlayer",
                        new Dictionary<string, object>
                        {
                            ["player"] = ReadOnlySeString.CreatePlayerLink
                            (
                                playerName,
                                homeWorld,
                                rented.ToReadOnlySeString()
                            )
                        }
                    );

                    NotifyHelper.Instance().Chat(message);
                    NotifyHelper.Toast(message);
                }
            };
        }
    }

    #region 枚举

    private enum PlayerRole : byte
    {
        None,
        Tank,
        Melee,
        PhysicalRanged,
        MagicalRanged,
        Healer
    }

    #endregion
}
