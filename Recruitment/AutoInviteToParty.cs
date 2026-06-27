using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using OmenTools;
using OmenTools.Extensions;
using OmenTools.Info.Game;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using static OmenTools.Global.Globals;

namespace DailyRoutines.ModulesPublic;

// 自动邀请组队: 监听聊天, 命中关键词/正则时自动向发言者发送组队邀请
// 原理参考: https://github.com/Bluefissure/Inviter (Hook RaptureLogModule.AddMsgSourceEntry + InfoProxyPartyInvite)
public unsafe class AutoInviteToParty : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "自动邀请组队",
        Description = "监听聊天频道, 当有人发送指定关键词 (或正则) 时, 自动向其发送组队邀请。模仿 Inviter 插件。",
        Category    = ModuleCategory.Recruitment,
        Author      = ["黑川启太"]
    };

    private const string CommandName = "autoinvite";

    private static Config config = null!;

    private Hook<RaptureLogModule.Delegates.AddMsgSourceEntry>? addMsgSourceEntryHook;

    private long nextInviteAt;

    // 可选监听频道
    private static readonly (XivChatType Type, string Label)[] AvailableChannels =
    [
        (XivChatType.Say,             "说话"),
        (XivChatType.Yell,            "呼喊"),
        (XivChatType.Shout,           "喊话"),
        (XivChatType.TellIncoming,    "悄悄话"),
        (XivChatType.Party,           "小队"),
        (XivChatType.Alliance,        "团队"),
        (XivChatType.FreeCompany,     "部队"),
        (XivChatType.NoviceNetwork,   "新人频道"),
        (XivChatType.Ls1,             "LS1"),
        (XivChatType.Ls2,             "LS2"),
        (XivChatType.Ls3,             "LS3"),
        (XivChatType.Ls4,             "LS4"),
        (XivChatType.Ls5,             "LS5"),
        (XivChatType.Ls6,             "LS6"),
        (XivChatType.Ls7,             "LS7"),
        (XivChatType.Ls8,             "LS8"),
        (XivChatType.CrossLinkShell1, "CWLS1"),
        (XivChatType.CrossLinkShell2, "CWLS2"),
        (XivChatType.CrossLinkShell3, "CWLS3"),
        (XivChatType.CrossLinkShell4, "CWLS4"),
        (XivChatType.CrossLinkShell5, "CWLS5"),
        (XivChatType.CrossLinkShell6, "CWLS6"),
        (XivChatType.CrossLinkShell7, "CWLS7"),
        (XivChatType.CrossLinkShell8, "CWLS8")
    ];

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        addMsgSourceEntryHook ??= DService.Instance().Hook.HookFromMemberFunction<RaptureLogModule.Delegates.AddMsgSourceEntry>
        (
            typeof(RaptureLogModule.MemberFunctionPointers),
            nameof(RaptureLogModule.MemberFunctionPointers.AddMsgSourceEntry),
            AddMsgSourceEntryDetour
        );
        addMsgSourceEntryHook.Enable();

        CommandManager.Instance().AddSubCommand(CommandName, new(OnCommand)
        {
            HelpMessage = "切换 / 开启 / 关闭自动邀请组队: /pdr autoinvite [on|off|toggle]"
        });
    }

    protected override void Uninit()
    {
        CommandManager.Instance().RemoveSubCommand(CommandName);

        addMsgSourceEntryHook?.Dispose();
        addMsgSourceEntryHook = null;
    }

    private void OnCommand(string command, string args)
    {
        var arg = args.Trim().ToLowerInvariant();
        config.Enabled = arg switch
        {
            "on"  => true,
            "off" => false,
            _     => !config.Enabled // 空 / toggle / 其它 -> 切换
        };
        config.Save(this);

        NotifyHelper.Instance().NotificationInfo($"自动邀请组队: {(config.Enabled ? "已开启" : "已关闭")}");
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox("启用自动邀请", ref config.Enabled))
            config.Save(this);
        ImGui.SameLine();
        ImGui.TextDisabled("(也可用指令 /pdr autoinvite [on|off|toggle] 切换)");

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), "触发关键词");
        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(220f * GlobalUIScale);
            if (ImGui.InputText("###Pattern", ref config.TextPattern, 256))
                config.Save(this);

            if (ImGui.Checkbox("使用正则表达式匹配", ref config.RegexMatch))
                config.Save(this);
        }

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), "监听频道");
        using (ImRaii.PushIndent())
        {
            for (var i = 0; i < AvailableChannels.Length; i++)
            {
                var (type, label) = AvailableChannels[i];

                var enabled = config.ListenChannels.Contains(type);
                if (ImGui.Checkbox($"{label}###Channel{(int)type}", ref enabled))
                {
                    if (enabled) config.ListenChannels.Add(type);
                    else         config.ListenChannels.Remove(type);
                    config.Save(this);
                }

                if (i % 4 != 3 && i != AvailableChannels.Length - 1)
                    ImGui.SameLine();
            }
        }

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), "邀请间隔 (毫秒)");
        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(200f * GlobalUIScale);
            if (ImGui.SliderInt("###RateLimit", ref config.RateLimit, 200, 5000))
                config.RateLimit = Math.Clamp(config.RateLimit, 200, 60000);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);
        }

        ImGui.NewLine();

        if (ImGui.Checkbox("发送邀请时在本地聊天打印提示", ref config.PrintMessage))
            config.Save(this);

        ImGui.TextDisabled("说明: 满员、或在队中但你不是队长、或对方已在队内时, 均会自动跳过。");
    }

    private void AddMsgSourceEntryDetour(
        RaptureLogModule* thisPtr, ulong contentID, ulong accountID, int messageIndex, ushort worldID, ushort chatType)
    {
        addMsgSourceEntryHook!.Original(thisPtr, contentID, accountID, messageIndex, worldID, chatType);

        try
        {
            TryInvite(contentID, messageIndex, chatType);
        }
        catch
        {
            // 忽略单条消息处理异常, 不影响游戏聊天
        }
    }

    private void TryInvite(ulong contentID, int messageIndex, ushort chatType)
    {
        if (!config.Enabled) return;
        if (!config.ListenChannels.Contains((XivChatType)chatType)) return;

        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return;
        if (DService.Instance().Condition[ConditionFlag.BetweenAreas] ||
            DService.Instance().Condition[ConditionFlag.BetweenAreas51])
            return;

        if (!RaptureLogModule.Instance()->GetLogMessageDetail(messageIndex, out var sender, out var rawMessage, out _, out _, out _, out _))
            return;

        var message = SeString.Parse(rawMessage.AsSpan()).TextValue;

        bool matched;
        if (!config.RegexMatch)
            matched = message.Contains(config.TextPattern, StringComparison.OrdinalIgnoreCase);
        else
        {
            try { matched = Regex.IsMatch(message, config.TextPattern, RegexOptions.IgnoreCase); }
            catch { return; } // 无效正则
        }

        if (!matched) return;

        // 队伍状态检查
        var group = GroupManager.Instance()->GetGroup();
        if (group->MemberCount >= 8) return; // 满员
        if (group->MemberCount > 0 && !GroupManager.Instance()->MainGroup.IsEntityIdPartyLeader(localPlayer.EntityID))
            return; // 在队中但不是队长

        // 已在队伍内
        foreach (var member in DService.Instance().PartyList)
        {
            if ((ulong)member.ContentId == contentID)
                return;
        }

        if (SeString.Parse(sender.AsSpan()).Payloads.FirstOrDefault(p => p is PlayerPayload) is not PlayerPayload playerPayload)
            return;

        // 频率限制
        var now = Environment.TickCount64;
        if (now <= nextInviteAt) return;
        nextInviteAt = now + config.RateLimit;

        if (config.PrintMessage)
            NotifyHelper.Instance().Chat($"[自动邀请组队] 正在邀请 {playerPayload.PlayerName}");

        if (InInvitableInstance())
            InfoProxyPartyInvite.Instance()->InviteToPartyInInstanceByContentId(contentID);
        else
        {
            fixed (byte* namePtr = ToTerminatedBytes(playerPayload.PlayerName))
                InfoProxyPartyInvite.Instance()->InviteToParty(contentID, namePtr, (ushort)playerPayload.World.RowId);
        }
    }

    // 副本内 (跨副本邀请) 判定, 同 Inviter
    private static bool InInvitableInstance() =>
        DService.Instance().Condition[ConditionFlag.BoundByDuty56] &&
        LuminaGetter.TryGetRow<TerritoryType>(GameState.TerritoryType, out var territory) &&
        territory.TerritoryIntendedUse.RowId is 41 or 47 or 48 or 52 or 53 or 61;

    private static byte[] ToTerminatedBytes(string text)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(text) + 1];
        Encoding.UTF8.GetBytes(text, 0, text.Length, bytes, 0);
        bytes[^1] = 0;
        return bytes;
    }

    private class Config : ModuleConfig
    {
        public bool   Enabled = true;              // 自动邀请的运行开关 (可用指令 / 勾选切换)
        public string TextPattern = "111|求组队";  // 默认: 正则匹配 "111" 或 "求组队"
        public bool   RegexMatch  = true;
        public int    RateLimit   = 500;           // 两次邀请最小间隔 (毫秒)
        public bool   PrintMessage;

        public HashSet<XivChatType> ListenChannels = [XivChatType.Shout]; // 默认仅喊话频道
    }
}
