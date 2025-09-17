using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

namespace DailyRoutines.Modules;

public class AutoOpenMapLinks : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("AutoOpenMapLinksTitle"),
        Description = GetLoc("AutoOpenMapLinksDescription"),
        Category = ModuleCategories.General,
        Author = ["KirisameVanilla"],
    };

    private static readonly AutoOpenMapLinksMenuItem AutoOpenMapLinksItem = new();

    private static Config ModuleConfig = null!;
    private static readonly HashSet<XivChatType> ValidChatTypes = [.. Enum.GetValues<XivChatType>()];

    public class Config : ModuleConfiguration
    {
        public HashSet<string>      WhitelistPlayer  = [];
        public HashSet<XivChatType> WhitelistChannel = [];
        public bool                 IsFlagCentered;
    }

    protected override void Init()
    {
        ModuleConfig = new Config().Load(this);

        DService.Chat.ChatMessage += HandleChatMessage;
        DService.ContextMenu.OnMenuOpened += OnMenuOpen;
    }

    protected override void Uninit()
    {
        DService.Chat.ChatMessage -= HandleChatMessage;
        DService.ContextMenu.OnMenuOpened -= OnMenuOpen;
    }

    private static void OnMenuOpen(IMenuOpenedArgs args)
    {
        if (!AutoOpenMapLinksItem.IsDisplay(args)) return;
        args.AddMenuItem(AutoOpenMapLinksItem.Get());
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("AutoOpenMapLinks-AutoFocusFlag"), ref ModuleConfig.IsFlagCentered))
            ModuleConfig.Save(this);
        
        ImGui.Spacing();
        using (ImRaii.PushId("PlayerWhitelist"))
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(LightSkyBlue, GetLoc("AutoOpenMapLinks-TargetPlayer"));

            ImGui.Spacing();

            using (ImRaii.PushIndent())
            {
                using var combo = ImRaii.Combo("###WhitelistPlayerCombo",
                                               GetLoc("AutoOpenMapLinks-AlreadyAddedPlayerCount", ModuleConfig.WhitelistPlayer.Count),
                                               ImGuiComboFlags.HeightLarge);
                if (combo)
                {
                    if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, GetLoc("Add")))
                    {
                        ModuleConfig.WhitelistPlayer.Add(string.Empty);
                        ModuleConfig.Save(this);
                    }

                    const string pattern = @"^.+@[^\s@]+$";
                    var source = ModuleConfig.WhitelistPlayer.ToList();
                    for (var i = 0; i < source.Count; i++)
                    {
                        var whitelistName = source[i];
                        var input = whitelistName;
                        using var id = ImRaii.PushId($"{whitelistName}_{i}_Name");

                        if (ImGuiOm.ButtonIcon("Delete", FontAwesomeIcon.TrashAlt, GetLoc("Delete")))
                        {
                            ModuleConfig.WhitelistPlayer.Remove(whitelistName);
                            ModuleConfig.Save(this);
                        }

                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(-1f);
                        ImGui.InputText($"###Name{whitelistName}-{i}", ref input, 128);

                        if (ImGui.IsItemDeactivatedAfterEdit())
                        {
                            if (Regex.IsMatch(input, pattern))
                            {
                                ModuleConfig.WhitelistPlayer.Remove(whitelistName);
                                ModuleConfig.WhitelistPlayer.Add(input);
                                ModuleConfig.Save(this);
                            }
                            else
                                NotificationError(GetLoc("InvalidName"));
                        }
                    }
                }
            }
            ImGui.SameLine();
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Eraser, GetLoc("Clear")))
            {
                ModuleConfig.WhitelistPlayer.Clear();
                ModuleConfig.Save(this);
            }
        }

        ImGui.Spacing();

        using (ImRaii.PushId("ChannelWhitelist"))
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(LightSkyBlue, GetLoc("AutoOpenMapLinks-WhitelistChannels"));
            
            ImGui.Spacing();

            using (ImRaii.PushIndent())
            {
                using var combo = ImRaii.Combo("###WhitelistChannelCombo",
                                               GetLoc("AutoOpenMapLinks-AlreadyAddedChannelCount", ModuleConfig.WhitelistChannel.Count),
                                               ImGuiComboFlags.HeightLarge);
                if (combo)
                {
                    foreach (var chatType in ValidChatTypes)
                    {
                        if (ImGui.Selectable(chatType.ToString(), ModuleConfig.WhitelistChannel.Contains(chatType),
                                             ImGuiSelectableFlags.DontClosePopups))
                        {
                            if (!ModuleConfig.WhitelistChannel.Remove(chatType))
                                ModuleConfig.WhitelistChannel.Add(chatType);
                            ModuleConfig.Save(this);
                        }
                    }
                }
            }
            ImGui.SameLine();
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Eraser, GetLoc("Clear")))
            {
                ModuleConfig.WhitelistChannel.Clear();
                ModuleConfig.Save(this);
            }
        }
    }

    private static void HandleChatMessage(
        XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        if (!ValidChatTypes.Contains(type)) return;
        if (ModuleConfig.WhitelistPlayer.Count == 0 && ModuleConfig.WhitelistChannel.Count == 0) return;
        if (message.Payloads.OfType<MapLinkPayload>().FirstOrDefault() is not { } mapPayload) return;

        var territoryID = mapPayload.TerritoryType.RowId;
        var mapID       = mapPayload.Map.RowId;
        if (ModuleConfig.WhitelistChannel.Contains(type))
        {
            SetFlag(territoryID, mapID, mapPayload.RawX, mapPayload.RawY);
            return;
        }

        if (sender.Payloads.Count == 0) return;

        foreach (var payload in sender.Payloads)
        {
            if (payload is PlayerPayload playerPayload)
            {
                var senderName = $"{playerPayload.PlayerName}@{playerPayload.World.Value.Name.ExtractText()}";
                if (ModuleConfig.WhitelistPlayer.Contains(senderName))
                {
                    SetFlag(territoryID, mapID, mapPayload.RawX, mapPayload.RawY);
                    return;
                }
            }
        }
    }

    private static unsafe void SetFlag(uint territoryID, uint mapID, int x, int y)
    {
        if (!ModuleConfig.IsFlagCentered)
            DService.Gui.OpenMapWithMapLink(new(territoryID, mapID, x, y));
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

    private class AutoOpenMapLinksMenuItem : MenuItemBase
    {
        public override string Name { get; protected set; } = Lang.Get("AutoOpenMapLinks-ClickMenu");

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            if (args.Target is not MenuTargetDefault target) return;
            if (target.TargetCharacter == null && string.IsNullOrWhiteSpace(target.TargetName) &&
                target.TargetHomeWorld.ValueNullable == null) return;

            var playerName = target.TargetCharacter != null ? target.TargetCharacter.Name : target.TargetName;
            var playerWorld = target.TargetCharacter != null ? target.TargetCharacter.HomeWorld : target.TargetHomeWorld;

            var id = $"{playerName}@{playerWorld.ValueNullable?.Name}";
            if(!ModuleConfig.WhitelistPlayer.Add(id))
                NotificationWarning(GetLoc("AutoOpenMapLinks-AlreadyExistedInList"));
        }

        public override bool IsDisplay(IMenuOpenedArgs args)
        {
            if (args.Target is not MenuTargetDefault target) return false;

            return args.AddonName switch
            {
                null or "LookingForGroup" or "PartyMemberList" or "FriendList" or "FreeCompany" or "SocialList"
                    or "ContactList" or "ChatLog" or "_PartyList" or "LinkShell" or "CrossWorldLinkshell"
                    or "ContentMemberList" or "BeginnerChatList" or "CircleBook" =>
                    target.TargetName != string.Empty && PresetSheet.Worlds.ContainsKey(target.TargetHomeWorld.RowId),
                _ => false
            };
        }
    }
}
