using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OmenTools.Info.Game.Data;
using OmenTools.Info.Lumina;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoOpenMapLinks : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoOpenMapLinksTitle"),
        Description = Lang.Get("AutoOpenMapLinksDescription"),
        Category    = ModuleCategory.System,
        Author      = ["KirisameVanilla"]
    };

    private Config config = null!;

    private AutoOpenMapLinksMenuItem autoOpenMapLinksItem = null!;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        autoOpenMapLinksItem = new(this);

        IChatGui.Instance().ChatMessage += HandleChatMessage;
        ContextMenuManager.Instance().Reg(autoOpenMapLinksItem);
    }

    protected override void Uninit()
    {
        IChatGui.Instance().ChatMessage -= HandleChatMessage;
        ContextMenuManager.Instance().Unreg(autoOpenMapLinksItem);
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("AutoOpenMapLinks-AutoOpenMap"), ref config.AutoOpenMap))
            config.Save(this);

        ImGui.NewLine();

        using (ImRaii.Heading1
               (
                   Lang.Get("AutoOpenMapLinks-WhitelistChannels"),
                   Lang.Get("AutoOpenMapLinks-WhitelistChannels-Help")
               ))
        {
            using var combo = ImRaii.Combo
            (
                "###WhitelistChannelCombo",
                Lang.Get("AutoOpenMapLinks-AlreadyAddedChannelCount", config.WhitelistChannel.Count),
                ImGuiComboFlags.HeightLarge
            );

            if (combo)
            {
                foreach (var chatType in ValidChatTypes)
                {
                    if (ImGui.Selectable
                        (
                            XIVChatTypes.ChatTypeToAddonText.GetValueOrDefault(chatType),
                            config.WhitelistChannel.Contains(chatType),
                            ImGuiSelectableFlags.DontClosePopups
                        ))
                    {
                        if (!config.WhitelistChannel.Remove(chatType))
                            config.WhitelistChannel.Add(chatType);
                        config.Save(this);
                    }
                }
            }
        }
    }

    private unsafe void HandleChatMessage
    (
        IHandleableChatMessage message
    )
    {
        if (!ValidChatTypes.Contains(message.LogKind)) 
            return;
        if (message.Message.Payloads.OfType<MapLinkPayload>().FirstOrDefault() is not { } mapPayload) 
            return;
        
        var territoryID = mapPayload.TerritoryType.RowId;
        var mapID       = mapPayload.Map.RowId;
        var position    = new Vector3(mapPayload.RawX / 1000f, 0, mapPayload.RawY / 1000f);

        if (config.WhitelistChannel.Contains(message.LogKind))
        {
            if (config.AutoOpenMap)
                AgentMap.Instance()->SetMapFlagAndOpen(mapID, position);
            else
                AgentMap.Instance()->SetFlagMapMarker(territoryID, mapID, position);
                
            return;
        }
        
        if (config.WhitelistChannel.Count == 0)
            return;
        if (message.Sender.Payloads.Count == 0) 
            return;

        foreach (var payload in message.Sender.Payloads)
        {
            if (payload is PlayerPayload playerPayload)
            {
                var senderName = $"{playerPayload.PlayerName}@{playerPayload.World.Value.Name.ToString()}";

                if (config.WhitelistPlayer.Contains(senderName))
                {
                    if (config.AutoOpenMap)
                        AgentMap.Instance()->SetMapFlagAndOpen(mapID, position);
                    else
                        AgentMap.Instance()->SetFlagMapMarker(territoryID, mapID, position);
                    
                    return;
                }
            }
        }
    }

    private class Config : ModuleConfig
    {
        public bool                 AutoOpenMap      = true;
        public HashSet<XivChatType> WhitelistChannel = [];
        public HashSet<string>      WhitelistPlayer  = [];
    }

    private class AutoOpenMapLinksMenuItem
    (
        AutoOpenMapLinks module
    ) : ContextMenuEntry
    {
        public override string Identifier =>
            nameof(AutoOpenMapLinks);

        public override ContextMenuItem? Create
        (
            ContextMenuOpenedArgs args
        )
        {
            if (args.AddonName == null || 
                ValidAddonNames.Contains(args.AddonName))
            {
                if (string.IsNullOrEmpty(args.TargetName) ||
                    !Sheets.Worlds.ContainsKey((uint)args.TargetHomeWorldID))
                    return null;

                var player   = $"{args.TargetName}@{LuminaWrapper.GetWorldName((uint)args.TargetHomeWorldID)}";
                var isOnList = module.config.WhitelistPlayer.Contains(player);
                return new()
                {
                    Name = Lang.Get
                    (
                        !isOnList ?
                            "AutoOpenMapLinks-ContextMenu-Subscribe" :
                            "AutoOpenMapLinks-ContextMenu-Unsubscribe"
                    ),
                    OnClicked = _ =>
                    {
                        var playerPayload = new PlayerPayload(args.TargetName, (uint)args.TargetHomeWorldID);
                        
                        if (module.config.WhitelistPlayer.Add(player))
                        {
                            var message = Lang.GetSe
                            (
                                "AutoOpenMapLinks-Notification-PlayerAdded",
                                playerPayload
                            );
                            
                            NotifyHelper.Instance().Chat(message);
                            NotifyHelper.Toast(message);
                            
                            module.config.Save(module);
                        }
                        else if (module.config.WhitelistPlayer.Remove(player))
                        {
                            var message = Lang.GetSe
                            (
                                "AutoOpenMapLinks-Notification-PlayerRemoved",
                                playerPayload
                            );
                            
                            NotifyHelper.Instance().Chat(message);
                            NotifyHelper.Toast(message);
                            
                            module.config.Save(module);
                        }
                    }
                };
            }

            return null;
        }
    }

    #region 常量

    private static readonly FrozenSet<XivChatType> ValidChatTypes =
    [
        .. XIVChatTypes.ChatTypeToAddonText.Keys
    ];

    private static readonly FrozenSet<string> ValidAddonNames =
    [
        "LookingForGroup",
        "PartyMemberList",
        "FriendList",
        "FreeCompany",
        "SocialList",
        "ContactList",
        "ChatLog",
        "_PartyList",
        "LinkShell",
        "CrossWorldLinkshell",
        "ContentMemberList",
        "BeginnerChatList",
        "CircleBook"
    ];

    #endregion
}
