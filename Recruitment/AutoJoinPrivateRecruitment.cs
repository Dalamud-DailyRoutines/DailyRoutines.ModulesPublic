using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Dalamud;
using OmenTools.OmenService;
using OmenTools.Interop.Game.AddonEvent;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoJoinPrivateRecruitment : ModuleBase
{
    #region 模块状态

    public override ModuleInfo Info { get; } = new()
    {
        Title = Lang.Get("AutoJoinPrivateRecruitmentTitle"),
        Description = Lang.Get("AutoJoinPrivateRecruitmentDescription"),
        Category = ModuleCategory.Recruitment,
        Author = ["MnFeN"]
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private const long PendingTimeoutMS = 30000;

    // 内存会话记录：当前 DR 插件生命周期内有效
    private static readonly HashSet<ulong> FailedAccounts = new();

    private Config config = null!;
    private Hook<AtkUnitBase.Delegates.FireCallback>? callbackHook;
    private Dialog? dialog;
    private Attempt? pending;
    private Attempt? retry;
    private List<(int Password, bool IsFromText)>? dispatchCandidates;
    private int dispatchIndex;
    private long detailRevision;

    #endregion

    #region 初始化设置

    protected override void Init()
    {
        config = Config.Load(this) ?? new();
        config.Passwords ??= new();

        callbackHook = IGameInteropProvider.Instance().HookFromAddress<AtkUnitBase.Delegates.FireCallback>(
            (nint)AtkUnitBase.MemberFunctionPointers.FireCallback, OnFireCallback);
        callbackHook.Enable();

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup, "LookingForGroupPrivate", OnPasswordAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize, "LookingForGroupPrivate", OnPasswordAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup, "LookingForGroupDetail", OnDetail);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostRefresh, "LookingForGroupDetail", OnDetail);
        LogMessageManager.Instance().RegPost(OnLogMessage);
        FrameworkManager.Instance().Reg(OnUpdate);
    }

    protected override void Uninit()
    {
        FrameworkManager.Instance().Unreg(OnUpdate);
        LogMessageManager.Instance().Unreg(OnLogMessage);
        IAddonLifecycle.Instance().UnregisterListener(OnPasswordAddon, OnDetail);

        callbackHook?.Dispose();
        callbackHook = null;
        dialog = null;
        pending = null;
        retry = null;
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("AutoJoinPrivateRecruitment-ParsePasswordFromListing"), ref config.ParsePasswordFromListing))
            config.Save(this);
        ImGuiOm.HelpMarker(Lang.Get("AutoJoinPrivateRecruitment-ParsePasswordFromListingHelp"));

        if (ImGui.Checkbox(Lang.Get("AutoJoinPrivateRecruitment-CacheSuccessfulPasswords"), ref config.CacheSuccessfulPasswords))
            config.Save(this);
        ImGuiOm.HelpMarker(Lang.Get("AutoJoinPrivateRecruitment-CacheSuccessfulPasswordsHelp"));

        ImGui.TextUnformatted(Lang.Get("AutoJoinPrivateRecruitment-StoredPasswordCount", new Dictionary<string, object>
        {
            ["count"] = config.Passwords.Count
        }));

        if (ImGui.Button(Lang.Get("AutoJoinPrivateRecruitment-ClearStoredPasswords")))
        {
            config.Passwords.Clear();
            config.Save(this);
        }
    }

    #endregion

    #region PF 密码窗口 UI

    private void OnPasswordAddon(AddonEvent type, AddonArgs args)
    {
        if (type == AddonEvent.PreFinalize)
        {
            // 提交会关闭窗口；等待中的入队结果必须保留。
            dialog = null;
            return;
        }

        dialog = null;

        // 新密码窗口开始一轮独立尝试，防止上一轮尚未确认的结果串到新招募。
        pending = null;

        var agent = AgentLookingForGroup.Instance();
        if (agent == null || !agent->IsAgentActive()) return;

        var listing = agent->LastViewedListing;
        if (listing.LeaderAccountId == 0 || listing.LeaderContentId == 0 || listing.ListingId == 0 ||
            agent->ListingContentId != listing.LeaderContentId || listing.LeaderContentId == LocalPlayerState.ContentID)
            return;

        // 此窗口本身证明是密码招募，不依赖可能随版本变化的 Private 标志解释。
        dialog = new Dialog
        {
            Addon = (nint)LookingForGroupPrivate,
            ListingID = listing.ListingId,
            AccountID = listing.LeaderAccountId,
            LeaderContentID = listing.LeaderContentId,
            PlayerContentID = LocalPlayerState.ContentID,
            Comment = listing.CommentString
        };

        if (retry is { } next && (!IsSameTarget(dialog, next.Target) || next.Stage != RetryStage.WaitPassword))
            retry = null;
    }

    private void OnUpdate(IFramework framework)
    {
        var now = Environment.TickCount64;

        if (pending is { } attempt)
        {
            if (!GameState.IsLoggedIn || LocalPlayerState.ContentID != attempt.Target.PlayerContentID ||
                now - attempt.StartedAt >= PendingTimeoutMS)
            {
                pending = null;
            }
            else if (attempt.JoinMessageReceived && IsLeaderInParty(attempt.Target.LeaderContentID))
            {
                pending = null;
                retry = null;
                FailedAccounts.Remove(attempt.Target.AccountID);
                if (config.CacheSuccessfulPasswords)
                    SavePassword(attempt.Target.AccountID, attempt.Password);
            }
        }

        if (retry != null) AdvanceRetry(now);

        var current = dialog;
        if (current == null || current.IsAutoHandled) return;

        var addon = LookingForGroupPrivate;
        if ((nint)addon != current.Addon || !addon->IsAddonAndNodesReady()) return;

        current.IsAutoHandled = true;
        if (!IsCurrentListing(current) || pending != null || IsLeaderInParty(current.LeaderContentID)) return;

        // 本轮备用尝试是失败名单的唯一例外；重新点开的普通窗口仍保持完全手动。
        if (retry is { Stage: RetryStage.WaitPassword } next && IsSameTarget(current, next.Target))
        {
            retry = null;
            SubmitAuto(current, next.Candidates!, next.Index);
            return;
        }

        if (FailedAccounts.Contains(current.AccountID)) return;

        var candidates = new List<(int Password, bool IsFromText)>();

        if (config.ParsePasswordFromListing && TryExtractPasswordText(current.Comment, out var passwordText))
            candidates.Add((passwordText, true));

        if (config.Passwords.TryGetValue(current.AccountID, out var saved))
            foreach (var password in saved)
                if (candidates.All(x => x.Password != password))
                    candidates.Add((password, false));

        if (candidates.Count > 0) SubmitAuto(current, candidates, 0);
    }

    private void SubmitAuto(Dialog target, List<(int Password, bool IsFromText)> candidates, int index)
    {
        if (!IsCurrentListing(target) || (nint)LookingForGroupPrivate != target.Addon) return;

        dispatchCandidates = candidates;
        dispatchIndex = index;

        try
        {
            LookingForGroupPrivate->Callback(0, candidates[index].Password);
        }
        finally
        {
            dispatchCandidates = null;
            dispatchIndex = 0;
        }
    }

    private bool OnFireCallback(AtkUnitBase* addon, uint count, AtkValue* values, bool close)
    {
        try
        {
            var current = dialog;
            if (current != null && (nint)addon == current.Addon && addon->NameString == "LookingForGroupPrivate")
            {
                current.IsAutoHandled = true;

                // 用户接管后不再自动重试。
                if (dispatchCandidates == null) retry = null;

                if (count >= 2 && values != null && values[0].Type == AtkValueType.Int && values[0].Int == 0 &&
                    values[1].Type == AtkValueType.Int && values[1].Int is >= 0 and <= 9999 && IsCurrentListing(current))
                {
                    current.IsSubmitted = true;

                    pending = IsLeaderInParty(current.LeaderContentID) ? null : new Attempt
                    {
                        Target = current,
                        Password = values[1].Int,
                        StartedAt = Environment.TickCount64,
                        Candidates = dispatchCandidates,
                        Index = dispatchIndex
                    };

                    if (dispatchCandidates != null)
                    {
                        var sourceText = Lang.Get(dispatchCandidates[dispatchIndex].IsFromText ?
                            "AutoJoinPrivateRecruitment-SourceListing" : "AutoJoinPrivateRecruitment-SourceCache");
                        var message = Lang.Get("AutoJoinPrivateRecruitment-TryPassword", new Dictionary<string, object>
                        {
                            ["password"] = values[1].Int.ToString("D4"),
                            ["source"] = sourceText
                        });
                        NotifyHelper.Instance().Chat(message);
                    }
                }
                else if (!current.IsSubmitted)
                {
                    pending = null;
                }
            }
        }
        catch (Exception ex)
        {
            pending = null;
            retry = null;
            DLog.Error(Lang.Get("AutoJoinPrivateRecruitment-CallbackError"), ex);
        }

        return callbackHook!.Original(addon, count, values, close);
    }

    #endregion

    #region 密码提交 / 缓存

    private void OnLogMessage(uint id, LogMessageQueueItem item)
    {
        var attempt = pending;
        if (attempt == null) return;

        if (!GameState.IsLoggedIn || LocalPlayerState.ContentID != attempt.Target.PlayerContentID ||
            Environment.TickCount64 - attempt.StartedAt >= PendingTimeoutMS)
        {
            pending = null;
            return;
        }

        if (id is
            61 or   // 加入了某玩家的小队（服务器内招募）
            7446 or // 加入了跨服小队
            7455 or // 加入了某玩家组建的「任务名称：具体内容」队伍
            7456 or // 加入了某玩家组建的「任务名称」队伍
            7465 or // 加入了队员招募用的团队
            7466 or // 加入了某玩家组建的「任务名称：具体内容」队员招募团队中的指定队伍
            7467 or // 加入了某玩家组建的「任务名称」队员招募团队中的指定队伍
            7473)   // 加入了团队
        {
            attempt.JoinMessageReceived = true;
            return;
        }

        if (id != 7440) return; // 密码错误，无法加入。

        pending = null;
        retry = null;

        // 手动提交失败或密码以外的入队错误，均不新增失败记录，也不删除密码缓存。
        if (attempt.Candidates is not { } candidates) return;

        // 密码错误后继续尝试下一候选密码；
        if (attempt.Index + 1 < candidates.Count)
        {
            retry = attempt;
            retry.Index++;
            retry.StartedAt = Environment.TickCount64;
            retry.Stage = RetryStage.Reopen;
        }
        // 所有候选密码都错误后记录该 AID（下次点开不再自动填写）。
        else
        {
            FailedAccounts.Add(attempt.Target.AccountID);
        }
    }

    private void SavePassword(ulong accountID, int password)
    {
        if (!config.Passwords.TryGetValue(accountID, out var passwords))
            config.Passwords[accountID] = passwords = new();

        passwords.Remove(password);
        passwords.Insert(0, password);
        if (passwords.Count > 2) passwords.RemoveAt(2);
        config.Save(this);
    }

    #endregion

    #region 密码错误 → 自动重试

    private void OnDetail(AddonEvent type, AddonArgs args) => detailRevision++;

    private void AdvanceRetry(long now)
    {
        var next = retry!;

        if (!GameState.IsLoggedIn || LocalPlayerState.ContentID != next.Target.PlayerContentID ||
            now - next.StartedAt >= PendingTimeoutMS || LocalPlayerState.IsInAnyParty)
        {
            retry = null;
            return;
        }

        var agent = AgentLookingForGroup.Instance();
        if (agent == null)
        {
            retry = null;
            return;
        }

        // 玩家主动查看另一位队长时停止重试
        if (agent->ListingContentId != 0 && agent->ListingContentId != next.Target.LeaderContentID)
        {
            retry = null;
            return;
        }

        switch (next.Stage)
        {
            case RetryStage.Reopen:
                if (now - next.StartedAt < 500) return;
                if (SelectYesno->IsAddonAndNodesReady()) return;

                // 某些版本输错后保留密码窗口；只关闭本轮已提交的窗口。
                if (LookingForGroupPrivate->IsAddonAndNodesReady())
                {
                    if (dialog != next.Target || !next.Target.IsSubmitted ||
                        (nint)LookingForGroupPrivate != next.Target.Addon)
                    {
                        retry = null;
                        return;
                    }

                    LookingForGroupPrivate->Close(true);
                    return;
                }

                if (LookingForGroupDetail->IsAddonAndNodesReady())
                    LookingForGroupDetail->Close(true);

                next.Revision = detailRevision;
                next.Stage = RetryStage.WaitDetail;

                if (!agent->OpenListing(next.Target.ListingID))
                    retry = null;

                break;

            case RetryStage.WaitDetail:
                if (detailRevision == next.Revision || !LookingForGroupDetail->IsAddonAndNodesReady()) return;

                if (!IsCurrentListing(next.Target))
                {
                    retry = null;
                    return;
                }

                // 团队招募存在多个分队加入按钮，尚未实现原分队的识别与重新选择。
                if (LookingForGroupDetail->AtkValuesCount <= 19 || LookingForGroupDetail->AtkValues[19].UInt != 1)
                {
                    retry = null;
                    return;
                }

                // FastJoinAnotherPartyRecruitment 用的是 LookingForGroupDetail->GetComponentButtonById(109) 但这里用 FFCS 定义了
                var button = ((AddonLookingForGroupDetail*)LookingForGroupDetail)->JoinPartyButton;
                if (button == null || !button->IsEnabled) return;

                next.Stage = RetryStage.WaitPassword;
                button->Click();
                break;

            case RetryStage.WaitPassword:
                if (!IsCurrentListing(next.Target))
                {
                    retry = null;
                    return;
                }

                // 只确认该招募 Agent 所属的对话框，不点击无关的 SelectYesno。
                if (!next.Confirmed && SelectYesno->IsAddonAndNodesReady() &&
                    agent->JoinConfirmAddonId != 0 && SelectYesno->Id == agent->JoinConfirmAddonId)
                {
                    next.Confirmed = AddonSelectYesnoEvent.ClickYes();
                }

                break;
        }
    }

    #endregion

    #region 文本解析规则

    private const string DigitChars = "0-9０-９零〇一二三四五六七八九壹贰貳叁參肆伍陆陸柒捌玖";

    private static readonly Regex RepeatPasswordRegex = new(
        $@"(?<![{DigitChars}])[4４四肆][个個](?<digit>[{DigitChars}])(?![{DigitChars}])",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex LabeledPasswordRegex = new(
        $@"密[码碼][=\s\p{{P}}]*(?<digits>[{DigitChars}]{{1,4}})(?![{DigitChars}])",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex FourDigitPasswordRegex = new(
        $@"(?<![{DigitChars}])[{DigitChars}]{{4}}(?![{DigitChars}]|\s*[WwＷｗ万萬/])",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private static bool TryExtractPasswordText(string comment, out int password)
    {
        password = 0;

        // "四个4" "四個五" "４个６"
        // 此分类识别后直接返回
        var match = RepeatPasswordRegex.Match(comment);
        if (match.Success)
        {
            password = ParsePassword(new string(match.Groups["digit"].Value[0], 4));
            return true;
        }

        // "密码1234" "密碼 123"
        // 此分类识别后直接返回
        match = LabeledPasswordRegex.Match(comment);
        if (match.Success)
        {
            password = ParsePassword(match.Groups["digits"].Value);
            return true;
        }

        // "1234" "１２３４" "一二三四"
        // 不接受："1000 W" "1200万" "1500 / 1cd" 等疑似表示价格的数字
        match = FourDigitPasswordRegex.Match(comment);
        if (!match.Success) return false;

        // 四位数字必须仅有一处匹配
        var remaining = comment.Remove(match.Index, match.Length).Insert(match.Index, "□");
        if (FourDigitPasswordRegex.IsMatch(remaining)) return false;

        password = ParsePassword(match.Value);
        return true;
    }

    private static int ParsePassword(string text)
    {
        var value = 0;

        foreach (var c in text)
        {
            var digit = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= '０' and <= '９' => c - '０',
                '零' or '〇' => 0,
                '一' or '壹' => 1,
                '二' or '贰' or '貳' => 2,
                '三' or '叁' or '參' => 3,
                '四' or '肆' => 4,
                '五' or '伍' => 5,
                '六' or '陆' or '陸' => 6,
                '七' or '柒' => 7,
                '八' or '捌' => 8,
                '九' or '玖' => 9,
                _ => throw new ArgumentException("不支持的密码字符", nameof(text))
            };

            value = value * 10 + digit;
        }

        if (value is < 0 or > 9999)
            throw new ArgumentOutOfRangeException(nameof(text), value, "密码超出 0-9999 范围");
        return value;
    }

    #endregion

    #region 招募 / 队伍状态校验

    private static bool IsSameTarget(Dialog a, Dialog b) =>
        a.ListingID == b.ListingID &&
        a.AccountID == b.AccountID &&
        a.LeaderContentID == b.LeaderContentID &&
        a.PlayerContentID == b.PlayerContentID;

    private static bool IsCurrentListing(Dialog target)
    {
        var agent = AgentLookingForGroup.Instance();

        return agent != null &&
               LocalPlayerState.ContentID == target.PlayerContentID &&
               target.PlayerContentID != 0 &&
               agent->ListingContentId == target.LeaderContentID &&
               agent->LastViewedListing.ListingId == target.ListingID &&
               agent->LastViewedListing.LeaderAccountId == target.AccountID;
    }

    private static bool IsLeaderInParty(ulong cid)
    {
        if (cid == 0) return false;

        var crossRealm = InfoProxyCrossRealm.Instance();
        if (crossRealm != null && crossRealm->IsInCrossRealmParty &&
            InfoProxyCrossRealm.GetMemberByContentId(cid) != null)
            return true;

        var manager = GroupManager.Instance();
        if (manager == null) return false;

        var group = &manager->MainGroup;
        for (var i = 0; i < group->MemberCount && i < group->PartyMembers.Length; i++)
        {
            if (group->PartyMembers[i].ContentId == cid)
                return true;
        }

        // 检查团队中其他分队
        if (group->IsAlliance)
        {
            for (var i = 0; i < group->AllianceMembers.Length; i++)
            {
                if (group->AllianceMembers[i].ContentId == cid)
                    return true;
            }
        }

        return false;
    }

    #endregion

    #region 数据结构

    private sealed class Dialog
    {
        public nint Addon;
        public ulong ListingID;
        public ulong AccountID;
        public ulong LeaderContentID;
        public ulong PlayerContentID;
        public string Comment = string.Empty;
        public bool IsAutoHandled;
        public bool IsSubmitted;
    }

    private sealed class Attempt
    {
        public required Dialog Target;
        public int Password;
        public long StartedAt;
        public bool JoinMessageReceived;
        public List<(int Password, bool IsFromText)>? Candidates;
        public int Index;
        public long Revision;
        public RetryStage Stage;
        public bool Confirmed;
    }

    private enum RetryStage
    {
        Reopen,
        WaitDetail,
        WaitPassword
    }

    private class Config : ModuleConfig
    {
        public bool ParsePasswordFromListing = true;
        public bool CacheSuccessfulPasswords = true;
        public Dictionary<ulong, List<int>> Passwords = new();
    }

    #endregion

}
