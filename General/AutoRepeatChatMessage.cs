using System.Collections.Generic;
using DailyRoutines.Abstracts;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using KamiToolKit.Classes;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoRepeatChatMessage : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoRepeatChatMessageTitle"),
        Description = GetLoc("AutoRepeatChatMessageDescription", "\ue04e \ue090"),
        Category    = ModuleCategories.General
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private static readonly Dictionary<XivChatType, int> ChatTypesToChannel = new()
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
        [XivChatType.Ls8]             = 26,
    };

    private static readonly Dictionary<uint, (int Channel, byte[] Message, string Sender)> SavedPayload = [];

    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        DService.Chat.ChatMessage += OnChat;
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("AutoRepeatChatMessage-AutoSwitchChannel"), ref ModuleConfig.AutoSwitchChannel))
            SaveConfig(ModuleConfig);

        if (ModuleConfig.AutoSwitchChannel)
        {
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("AutoRepeatChatMessage-ColorPreview"));
            using (ImRaii.PushIndent())
            {
                ImGui.TextColored(ColorHelper.GetColor(34), "\ue04e \ue090");
                
                ImGui.SameLine();
                ImGui.Text($": {GetLoc("AutoRepeatChatMessage-ColorAble")}");
                
                ImGui.TextColored(ColorHelper.GetColor(32), "\ue04e \ue090");
                
                ImGui.SameLine();
                ImGui.Text($": {GetLoc("AutoRepeatChatMessage-ColorUnable")}");
            }
            
            ImGui.Spacing();
            
            if (ImGui.Checkbox(GetLoc("AutoRepeatChatMessage-AutoSwitchOrigChannel"), ref ModuleConfig.AutoSwitchOrigChannel))
                SaveConfig(ModuleConfig);
        }
        
        if (ImGui.Checkbox(GetLoc("AutoRepeatChatMessage-UseTrigger"), ref ModuleConfig.UseTrigger))
            SaveConfig(ModuleConfig);
    }

    private static void OnChat(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        if (isHandled) return;
        if (!ChatTypesToChannel.TryGetValue(type, out var channel)) return;

        var senderStr   = string.Empty;
        foreach (var senderPayload in sender.Payloads)
        {
            if (senderPayload is PlayerPayload playerPayload)
                senderStr = $"{playerPayload.PlayerName}@{playerPayload.World.Value.Name.ExtractText()}";
        }

        var linkPayload = LinkPayloadManager.Register(OnClickRepeat, out var id);
        SavedPayload.TryAdd(id, (channel, message.Encode(), senderStr));
        
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

    private static void OnClickRepeat(uint id, SeString message)
    {
        var triggerCheck = !ModuleConfig.UseTrigger || IsConflictKeyPressed();
        if (!triggerCheck) return;

        if (!SavedPayload.TryGetValue(id, out var info)) return;

        var instance = RaptureShellModule.Instance();
        if (instance == null) return;

        var agent = AgentChatLog.Instance();
        if (agent == null) return;
        
        var origChannel = (int)agent->CurrentChannel;
        var origShellIndex = ChatChannelToLinkshellIndex((uint)origChannel);
        var linkshellIndex = ChatChannelToLinkshellIndex((uint)info.Channel);
        
        if (info.Channel != -1 && ModuleConfig.AutoSwitchChannel)
        {
            switch (info.Channel)
            {
                case 0:
                    ChatManager.SendMessage($"/tell {info.Sender}");
                    break;
                default:
                    instance->ChangeChatChannel(info.Channel, linkshellIndex, Utf8String.FromString(string.Empty), true);
                    break;
            }
        }
        
        ChatManager.SendMessage(message.Encode());
        
        if (info.Channel != -1                                                     && 
            ModuleConfig is { AutoSwitchChannel: true, AutoSwitchOrigChannel: true })
            instance->ChangeChatChannel(origChannel, origShellIndex, Utf8String.FromString(string.Empty), false);
    }

    private static uint ChatChannelToLinkshellIndex(uint channel) => 
        channel switch
        {
            >= 9 and <= 16  => channel - 9,
            >= 19 and <= 26 => channel - 19,
            _               => 0
        };

    protected override void Uninit()
    {
        DService.Chat.ChatMessage -= OnChat;
        SavedPayload.Clear();
    }

    private class Config : ModuleConfiguration
    {
        public bool AutoSwitchChannel     = true;
        public bool AutoSwitchOrigChannel = true;
        public bool UseTrigger;
    }
}
