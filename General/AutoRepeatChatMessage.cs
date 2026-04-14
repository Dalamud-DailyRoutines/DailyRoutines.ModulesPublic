using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Internal;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using KamiToolKit.Classes;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoRepeatChatMessage : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoRepeatChatMessageTitle"),
        Description = Lang.Get("AutoRepeatChatMessageDescription", "\ue04e \ue090"),
        Category    = ModuleCategory.General
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };
    
    private Config config = null!;
    
    private readonly Dictionary<uint, (int Channel, byte[] Message, string Sender)> savedPayload = [];
    
    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        DService.Instance().Chat.ChatMessage += OnChat;
    }
    
    protected override void Uninit()
    {
        DService.Instance().Chat.ChatMessage -= OnChat;
        savedPayload.Clear();
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("AutoRepeatChatMessage-AutoSwitchChannel"), ref config.AutoSwitchChannel))
            config.Save(this);

        if (config.AutoSwitchChannel)
        {
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("AutoRepeatChatMessage-ColorPreview"));

            using (ImRaii.PushIndent())
            {
                ImGui.TextColored(ColorHelper.GetColor(34), "\ue04e \ue090");

                ImGui.SameLine();
                ImGui.TextUnformatted($": {Lang.Get("AutoRepeatChatMessage-ColorAble")}");

                ImGui.TextColored(ColorHelper.GetColor(32), "\ue04e \ue090");

                ImGui.SameLine();
                ImGui.TextUnformatted($": {Lang.Get("AutoRepeatChatMessage-ColorUnable")}");
            }

            ImGui.Spacing();

            if (ImGui.Checkbox(Lang.Get("AutoRepeatChatMessage-AutoSwitchOrigChannel"), ref config.AutoSwitchOrigChannel))
                config.Save(this);
        }

        if (ImGui.Checkbox(Lang.Get("AutoRepeatChatMessage-UseTrigger"), ref config.UseTrigger))
            config.Save(this);
    }

    private void OnChat(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        if (isHandled) return;
        if (!ChatTypesToChannel.TryGetValue(type, out var channel)) return;

        var senderStr = string.Empty;

        foreach (var senderPayload in sender.Payloads)
        {
            if (senderPayload is PlayerPayload playerPayload)
                senderStr = $"{playerPayload.PlayerName}@{playerPayload.World.Value.Name.ToString()}";
        }

        var linkPayload = LinkPayloadManager.Instance().Reg(OnClickRepeat, out var id);
        savedPayload.TryAdd(id, (channel, message.Encode(), senderStr));

        message.Append(new UIForegroundPayload(24))
               .Append(new TextPayload(" ["))
               .Append(new UIForegroundPayload(0))
               .Append(RawPayload.LinkTerminator)
               .Append(linkPayload)
               .Append(new UIForegroundPayload((ushort)(channel != -1 ? 34 : 32)))
               .Append(new TextPayload("\ue04e \ue090"))
               .Append(new UIForegroundPayload(0))
               .Append(RawPayload.LinkTerminator)
               .Append(new UIForegroundPayload(24))
               .Append(new TextPayload("]"))
               .Append(new UIForegroundPayload(0));
    }

    private void OnClickRepeat(uint id, SeString message)
    {
        var triggerCheck = !config.UseTrigger || PluginConfig.Instance().ConflictKeyBinding.IsPressed();
        if (!triggerCheck) return;

        if (!savedPayload.TryGetValue(id, out var info)) return;

        var instance = RaptureShellModule.Instance();
        if (instance == null) return;

        var agent = AgentChatLog.Instance();
        if (agent == null) return;

        var origChannel    = (int)agent->CurrentChannel;
        var origShellIndex = ChatChannelToLinkshellIndex((uint)origChannel);
        var linkshellIndex = ChatChannelToLinkshellIndex((uint)info.Channel);

        if (info.Channel != -1 && config.AutoSwitchChannel)
        {
            switch (info.Channel)
            {
                case 0:
                    ChatManager.Instance().SendMessage($"/tell {info.Sender}");
                    break;
                default:
                    instance->ChangeChatChannel(info.Channel, linkshellIndex, Utf8String.FromString(string.Empty), true);
                    break;
            }
        }

        ChatManager.Instance().SendCommand(info.Message);

        if (info.Channel != -1 &&
            config is { AutoSwitchChannel: true, AutoSwitchOrigChannel: true })
            instance->ChangeChatChannel(origChannel, origShellIndex, Utf8String.FromString(string.Empty), false);
    }

    private static uint ChatChannelToLinkshellIndex(uint channel) =>
        channel switch
        {
            >= 9 and <= 16  => channel - 9,
            >= 19 and <= 26 => channel - 19,
            _               => 0
        };
    
    private class Config : ModuleConfig
    {
        public bool AutoSwitchChannel     = true;
        public bool AutoSwitchOrigChannel = true;
        public bool UseTrigger;
    }
    
    #region 常量
    
    private static FrozenDictionary<XivChatType, int> ChatTypesToChannel { get; } = new Dictionary<XivChatType, int>
    {
        [XivChatType.TellIncoming]    = 0,
        [XivChatType.TellOutgoing]    = 0,
        [XivChatType.Say]             = 1,
        [XivChatType.CrossParty]      = 2,
        [XivChatType.Party]           = 2,
        [XivChatType.Alliance]        = 3,
        [XivChatType.Yell]            = 4,
        [XivChatType.Shout]           = 5,
        [XivChatType.FreeCompany]     = 6,
        [XivChatType.PvPTeam]         = 7,
        [XivChatType.NoviceNetwork]   = 8,
        [XivChatType.CrossLinkShell1] = 9,
        [XivChatType.CrossLinkShell2] = 10,
        [XivChatType.CrossLinkShell3] = 11,
        [XivChatType.CrossLinkShell4] = 12,
        [XivChatType.CrossLinkShell5] = 13,
        [XivChatType.CrossLinkShell6] = 14,
        [XivChatType.CrossLinkShell7] = 15,
        [XivChatType.CrossLinkShell8] = 16,
        [XivChatType.Ls1]             = 19,
        [XivChatType.Ls2]             = 20,
        [XivChatType.Ls3]             = 21,
        [XivChatType.Ls4]             = 22,
        [XivChatType.Ls5]             = 23,
        [XivChatType.Ls6]             = 24,
        [XivChatType.Ls7]             = 25,
        [XivChatType.Ls8]             = 26
    }.ToFrozenDictionary();
    
    #endregion
}
