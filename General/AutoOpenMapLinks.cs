using System.Collections.Frozen;
using System.Numerics;
using System.Text.RegularExpressions;
using DailyRoutines.Common.Info.Abstractions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OmenTools.Info.Game.Data;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public partial class AutoOpenMapLinks : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoOpenMapLinksTitle"),
        Description = Lang.Get("AutoOpenMapLinksDescription"),
        Category    = ModuleCategory.General,
        Author      = ["KirisameVanilla"]
    };

    private Config config = null!;
    
    private readonly AutoOpenMapLinksMenuItem autoOpenMapLinksItem;

    public AutoOpenMapLinks() =>
        autoOpenMapLinksItem = new(this);

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        DService.Instance().Chat.ChatMessage         += HandleChatMessage;
        DService.Instance().ContextMenu.OnMenuOpened += OnMenuOpen;
    }

    protected override void Uninit()
    {
        DService.Instance().Chat.ChatMessage         -= HandleChatMessage;
        DService.Instance().ContextMenu.OnMenuOpened -= OnMenuOpen;
    }
    
    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("AutoOpenMapLinks-AutoFocusFlag"), ref config.IsFlagCentered))
            config.Save(this);

        ImGui.Spacing();

        using (ImRaii.PushId("PlayerWhitelist"))
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("AutoOpenMapLinks-TargetPlayer"));

            ImGui.Spacing();

            using (ImRaii.PushIndent())
            {
                using var combo = ImRaii.Combo
                (
                    "###WhitelistPlayerCombo",
                    Lang.Get("AutoOpenMapLinks-AlreadyAddedPlayerCount", config.WhitelistPlayer.Count),
                    ImGuiComboFlags.HeightLarge
                );

                if (combo)
                {
                    if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, Lang.Get("Add")))
                    {
                        config.WhitelistPlayer.Add(string.Empty);
                        config.Save(this);
                    }

                    var source = config.WhitelistPlayer.ToList();

                    for (var i = 0; i < source.Count; i++)
                    {
                        var       whitelistName = source[i];
                        var       input         = whitelistName;
                        using var id            = ImRaii.PushId($"{whitelistName}_{i}_Name");

                        if (ImGuiOm.ButtonIcon("Delete", FontAwesomeIcon.TrashAlt, Lang.Get("Delete")))
                        {
                            config.WhitelistPlayer.Remove(whitelistName);
                            config.Save(this);
                        }

                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(-1f);
                        ImGui.InputText($"###Name{whitelistName}-{i}", ref input, 128);

                        if (ImGui.IsItemDeactivatedAfterEdit())
                        {
                            if (PlayerNameRegex().IsMatch(input))
                            {
                                config.WhitelistPlayer.Remove(whitelistName);
                                config.WhitelistPlayer.Add(input);
                                config.Save(this);
                            }
                            else
                                NotifyHelper.Instance().NotificationError(Lang.Get("InvalidName"));
                        }
                    }
                }
            }

            ImGui.SameLine();

            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Eraser, Lang.Get("Clear")))
            {
                config.WhitelistPlayer.Clear();
                config.Save(this);
            }
        }

        ImGui.Spacing();

        using (ImRaii.PushId("ChannelWhitelist"))
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("AutoOpenMapLinks-WhitelistChannels"));

            ImGui.Spacing();

            using (ImRaii.PushIndent())
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
                                chatType.ToString(),
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

            ImGui.SameLine();

            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Eraser, Lang.Get("Clear")))
            {
                config.WhitelistChannel.Clear();
                config.Save(this);
            }
        }
    }

    private void OnMenuOpen(IMenuOpenedArgs args)
    {
        if (!autoOpenMapLinksItem.IsDisplay(args)) return;
        args.AddMenuItem(autoOpenMapLinksItem.Get());
    }

    private void HandleChatMessage
    (
        XivChatType  type,
        int          timestamp,
        ref SeString sender,
        ref SeString message,
        ref bool     isHandled
    )
    {
        if (!ValidChatTypes.Contains(type)) return;
        if (config.WhitelistPlayer.Count == 0 && config.WhitelistChannel.Count == 0) return;
        if (message.Payloads.OfType<MapLinkPayload>().FirstOrDefault() is not { } mapPayload) return;

        var territoryID = mapPayload.TerritoryType.RowId;
        var mapID       = mapPayload.Map.RowId;

        if (config.WhitelistChannel.Contains(type))
        {
            SetFlag(territoryID, mapID, mapPayload.RawX, mapPayload.RawY);
            return;
        }

        if (sender.Payloads.Count == 0) return;

        foreach (var payload in sender.Payloads)
        {
            if (payload is PlayerPayload playerPayload)
            {
                var senderName = $"{playerPayload.PlayerName}@{playerPayload.World.Value.Name.ToString()}";

                if (config.WhitelistPlayer.Contains(senderName))
                {
                    SetFlag(territoryID, mapID, mapPayload.RawX, mapPayload.RawY);
                    return;
                }
            }
        }
    }

    private unsafe void SetFlag(uint territoryID, uint mapID, int x, int y)
    {
        if (!config.IsFlagCentered)
            DService.Instance().GameGUI.OpenMapWithMapLink(new(territoryID, mapID, x, y));
        else
        {
            var agentMap = AgentMap.Instance();
            // 个人学习用
            // agentMap->FlagMapMarker.MapMarker +44\+46的两个short 是 地图上旗子坐标的位置，0到65535，0在地图最中间
            // agentMap->FlagMapMarker.XFloat\YFloat 是 真实的<flag>坐标，格式WorldPos
            // MapLinkPayload里面的 RawX和 RawY 是worldPos * 1000
            if (agentMap == null) return;
            if (!agentMap->IsAgentActive() || agentMap->SelectedMapId != mapID)
                agentMap->OpenMap(mapID, territoryID);
            agentMap->SetFlagMapMarker(territoryID, mapID, new Vector3(x / 1000f, 0f, y / 1000f));
        }
    }

    [GeneratedRegex(@"^.+@[^\s@]+$")]
    private static partial Regex PlayerNameRegex();

    private class Config : ModuleConfig
    {
        public bool                 IsFlagCentered;
        public HashSet<XivChatType> WhitelistChannel = [];
        public HashSet<string>      WhitelistPlayer  = [];
    }

    private class AutoOpenMapLinksMenuItem(AutoOpenMapLinks module) : MenuItemBase
    {
        public override string Name       { get; protected set; } = Lang.Get("AutoOpenMapLinks-ClickMenu");
        public override string Identifier { get; protected set; } = nameof(AutoOpenMapLinks);

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            if (args.Target is not MenuTargetDefault target) 
                return;
            if (target.TargetCharacter == null               &&
                string.IsNullOrWhiteSpace(target.TargetName) &&
                target.TargetHomeWorld.ValueNullable == null) 
                return;

            var playerName  = target.TargetCharacter != null ? target.TargetCharacter.Name : target.TargetName;
            var playerWorld = target.TargetCharacter?.HomeWorld ?? target.TargetHomeWorld;

            var id = $"{playerName}@{playerWorld.ValueNullable?.Name}";
            if (!module.config.WhitelistPlayer.Add(id))
                NotifyHelper.Instance().NotificationWarning(Lang.Get("AutoOpenMapLinks-AlreadyExistedInList"));
        }

        public override bool IsDisplay(IMenuOpenedArgs args)
        {
            if (args.Target is not MenuTargetDefault target) return false;

            return args.AddonName switch
            {
                null or "LookingForGroup" or "PartyMemberList" or "FriendList" or "FreeCompany" or "SocialList"
                    or "ContactList" or "ChatLog" or "_PartyList" or "LinkShell" or "CrossWorldLinkshell"
                    or "ContentMemberList" or "BeginnerChatList" or "CircleBook" =>
                    target.TargetName != string.Empty && Sheets.Worlds.ContainsKey(target.TargetHomeWorld.RowId),
                _ => false
            };
        }
    }
    
    #region 常量
    
    private static FrozenSet<XivChatType> ValidChatTypes { get; } = [.. Enum.GetValues<XivChatType>()];
    
    #endregion
}
