using System.Collections.Concurrent;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using KamiToolKit.Nodes;
using OmenTools.Dalamud.Attributes;
using OmenTools.Info.Lumina;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;
using OmenTools.Threading.TaskHelper;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe partial class OptimizedFriendList : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = Lang.Get("OptimizedFriendListTitle"),
        Description         = Lang.Get("OptimizedFriendListDescription"),
        Category            = ModuleCategory.Interface,
        ModulesPrerequisite = ["FastWorldTravel"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private Config config = null!;

    private ModifyInfoMenuItem          modifyInfoItem        = null!;
    private QueryUsedNameMenuItem       queryUsedNameMenuItem = null!;
    private TeleportFriendZoneMenuItem  teleportZoneItem      = null!;
    private TeleportFriendWorldMenuItem teleportWorldItem     = null!;

    private TextInputNode?     searchInputNode;
    private TextureButtonNode? searchSettingButtonNode;

    private DRFriendlistRemarkEdit?    remarkEditAddon;
    private DRFriendlistSearchSetting? searchSettingAddon;
    private DRFriendlistUsedNames?     usedNamesAddon;

    private string searchString = string.Empty;

    private readonly List<IDisposable> infoTokens = [];

    protected override void Init()
    {
        config     =   Config.Load(this) ?? new();
        TaskHelper ??= new();

        modifyInfoItem        = new(this, TaskHelper);
        queryUsedNameMenuItem = new(this);
        teleportZoneItem      = new();
        teleportWorldItem     = new();

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup,          "FriendList", OnAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreRequestedUpdate, "FriendList", OnAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize,        "FriendList", OnAddon);
        if (FriendList->IsAddonAndNodesReady())
            OnAddon(AddonEvent.PostSetup, null);

        ContextMenuManager.Instance().Reg(modifyInfoItem);
        ContextMenuManager.Instance().Reg(queryUsedNameMenuItem);
        ContextMenuManager.Instance().Reg(teleportZoneItem);
        ContextMenuManager.Instance().Reg(teleportWorldItem);
    }

    protected override void Uninit()
    {
        ContextMenuManager.Instance().Unreg(modifyInfoItem);
        ContextMenuManager.Instance().Unreg(queryUsedNameMenuItem);
        ContextMenuManager.Instance().Unreg(teleportZoneItem);
        ContextMenuManager.Instance().Unreg(teleportWorldItem);

        IAddonLifecycle.Instance().UnregisterListener(OnAddon);

        searchInputNode?.Dispose();
        searchInputNode = null;

        searchSettingButtonNode?.Dispose();
        searchSettingButtonNode = null;

        foreach (var x in infoTokens)
            x.Dispose();
        infoTokens.Clear();

        remarkEditAddon?.Dispose();
        remarkEditAddon = null;

        searchSettingAddon?.Dispose();
        searchSettingAddon = null;

        usedNamesAddon?.Dispose();
        usedNamesAddon = null;

        if (FriendList->IsAddonAndNodesReady())
            InfoProxyFriendList.Instance()->RequestData();
    }

    private void OnAddon
    (
        AddonEvent type,
        AddonArgs? args
    )
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                if (FriendList != null)
                {
                    searchInputNode ??= new()
                    {
                        Position      = new(10f, 425f),
                        Size          = new(200.0f, 35f),
                        MaxCharacters = 20,
                        ShowLimitText = true,
                        OnInputReceived = x =>
                        {
                            searchString = x.ToString();
                            ApplySearchFilter(searchString, TaskHelper);
                        },
                        OnInputComplete = x =>
                        {
                            searchString = x.ToString();
                            ApplySearchFilter(searchString, TaskHelper);
                        }
                    };

                    searchInputNode.CursorNode.ScaleY        =  1.4f;
                    searchInputNode.CurrentTextNode.FontSize =  14;
                    searchInputNode.CurrentTextNode.Y        += 3f;

                    searchInputNode.AttachNode(FriendList->GetNodeById(20));

                    searchSettingButtonNode ??= new()
                    {
                        Position    = new(215f, 430f),
                        Size        = new(25f, 25f),
                        IsChecked   = config.SearchName,
                        IsEnabled   = true,
                        TexturePath = "ui/uld/CircleButtons_hr1.tex",
                        TextureSize = new(28, 28),
                        OnClick = () =>
                        {
                            if (searchSettingAddon is { IsOpen: true } openedAddon)
                            {
                                openedAddon.Close();
                                return;
                            }

                            var previousAddon = searchSettingAddon;

                            if (previousAddon is not null)
                            {
                                previousAddon.Dispose();
                                searchSettingAddon = null;
                            }

                            searchSettingAddon = DRFriendlistSearchSetting.Open(this, TaskHelper);
                        }
                    };

                    searchSettingButtonNode.AttachNode(FriendList->GetNodeById(20));

                    searchString = string.Empty;
                }

                if (Throttler.Shared.Throttle("OptimizedFriendList-OnRequestFriendList", 10_000))
                {
                    var agent = AgentFriendlist.Instance();
                    if (agent == null) return;

                    var info = InfoProxyFriendList.Instance();
                    if (info == null || info->EntryCount == 0) return;

                    var validCounter = 0;

                    for (var i = 0; i < info->CharDataSpan.Length; i++)
                    {
                        var chara = info->CharDataSpan[i];
                        if (chara.ContentId == 0) continue;

                        IFramework.Instance().RunOnTick
                        (
                            () =>
                            {
                                if (FriendList == null) return;

                                agent->RequestFriendInfo(chara.ContentId);
                            },
                            TimeSpan.FromMilliseconds(10 * validCounter)
                        );

                        validCounter++;
                    }

                    if (validCounter > 0)
                    {
                        IFramework.Instance().RunOnTick
                        (
                            () =>
                            {
                                if (FriendList == null) return;

                                ApplyDisplayModification(TaskHelper);
                            },
                            TimeSpan.FromMilliseconds(10 * (validCounter + 1))
                        );
                    }
                }

                ApplyDisplayModification(TaskHelper);
                break;

            case AddonEvent.PreRequestedUpdate:
                ApplySearchFilter(searchString, TaskHelper);
                ApplyDisplayModification(TaskHelper);
                break;

            case AddonEvent.PreFinalize:
                searchInputNode         = null;
                searchSettingButtonNode = null;
                infoTokens.Clear();
                break;
        }
    }

    private class Config : ModuleConfig
    {
        public ConcurrentDictionary<ulong, PlayerInfo> PlayerInfos = [];

        public bool[] IgnoredGroup = new bool[8];

        public bool SearchName     = true;
        public bool SearchNickname = true;
        public bool SearchRemark   = true;
    }


    private sealed class QueryUsedNameMenuItem
    (
        OptimizedFriendList module
    ) : ContextMenuEntry
    {
        public override string Identifier =>
            nameof(OptimizedFriendList);

        public override ContextMenuItem? Create
        (
            ContextMenuOpenedArgs args
        )
        {
            if (args.AddonName != "FriendList") 
                return null;
            
            var contentID = AgentFriendlist.Instance()->SelectedContentId;
            if (contentID == 0) 
                return null;
            
            var name    = args.TargetName;
            var worldID = (uint)args.TargetHomeWorldID;
            if (string.IsNullOrEmpty(name) ||
                !Sheets.Worlds.ContainsKey(worldID))
            {
                if (!TryGetPlayerInfoByContentID(contentID, out var playerInfo))
                    return null;

                name    = playerInfo.Name;
                worldID = playerInfo.WorldID;
            }

            return new()
            {
                Name = Lang.Get("OptimizedFriendList-ContextMenu-QueryUsedNames"),
                OnClicked = _ =>
                {
                    module.usedNamesAddon?.Dispose();
                    module.usedNamesAddon = DRFriendlistUsedNames.Open
                    (
                        contentID,
                        name,
                        LuminaWrapper.GetWorldName(worldID)
                    );
                }
            };
        }
    }

    private sealed class ModifyInfoMenuItem
    (
        OptimizedFriendList instance,
        TaskHelper          taskHelper
    ) : ContextMenuEntry
    {
        public override string Identifier =>
            nameof(OptimizedFriendList);

        public override ContextMenuItem? Create
        (
            ContextMenuOpenedArgs args
        )
        {
            if (args.AddonName != "FriendList") 
                return null;

            var contentID = AgentFriendlist.Instance()->SelectedContentId;
            if (contentID == 0) 
                return null;
            
            var name    = args.TargetName;
            var worldID = (uint)args.TargetHomeWorldID;
            if (string.IsNullOrEmpty(name) ||
                !Sheets.Worlds.ContainsKey(worldID))
            {
                if (!TryGetPlayerInfoByContentID(contentID, out var playerInfo))
                    return null;

                name    = playerInfo.Name;
                worldID = playerInfo.WorldID;
            }

            return new()
            {
                Name = Lang.Get("OptimizedFriendList-ContextMenu-NicknameAndRemark"),
                OnClicked = _ =>
                {
                    instance.remarkEditAddon?.Dispose();
                    instance.remarkEditAddon = DRFriendlistRemarkEdit.Open
                    (
                        instance,
                        contentID,
                        name,
                        LuminaWrapper.GetWorldName(worldID)
                    );
                    instance.ApplySearchFilter(instance.searchString, taskHelper);
                }
            };
        }
    }

    private sealed class TeleportFriendZoneMenuItem : ContextMenuEntry
    {
        public override string Identifier =>
            nameof(OptimizedFriendList);

        public override ContextMenuItem? Create
        (
            ContextMenuOpenedArgs args
        )
        {
            if (args.AddonName != "FriendList")
                return null;

            var targetCharacter = args.TargetCharacter;
            if (targetCharacter == null)
                return null;

            if (!TryGetAetheryteID(targetCharacter->Location, out var aetheryteID)) 
                return null;

            return new()
            {
                Name      = Lang.Get("OptimizedFriendList-ContextMenu-TeleportToFriendZone"),
                OnClicked = _ => Telepo.Instance()->Teleport(aetheryteID, 0)
            };
        }

        private static bool TryGetAetheryteID
        (
            uint     zoneID,
            out uint aetheryteID
        )
        {
            aetheryteID = 0;
            if (zoneID == 0 || zoneID == GameState.TerritoryType) return false;

            zoneID = zoneID switch
            {
                128 => 129,
                133 => 132,
                131 => 130,
                399 => 478,
                _   => zoneID
            };
            if (zoneID == GameState.TerritoryType) return false;

            aetheryteID = IAetheryteList.Instance()
                                        .Where(aetheryte => aetheryte.TerritoryID == zoneID)
                                        .Select(aetheryte => aetheryte.AetheryteID)
                                        .FirstOrDefault();

            return aetheryteID > 0;
        }
    }

    private sealed class TeleportFriendWorldMenuItem : ContextMenuEntry
    {
        public override string Identifier =>
            nameof(OptimizedFriendList);

        public override ContextMenuItem? Create
        (
            ContextMenuOpenedArgs args
        )
        {
            if (!(ModuleManager.Instance().IsModuleEnabled("FastWorldTravel") ?? false)) return null;
            if (args.AddonName != "FriendList") return null;

            var targetCharacter = args.TargetCharacter;
            if (targetCharacter == null) return null;

            var targetWorldID = (uint)targetCharacter->CurrentWorld;
            if (targetWorldID == GameState.CurrentWorld) return null;

            return new()
            {
                Name      = Lang.Get("OptimizedFriendList-ContextMenu-TeleportToFriendWorld"),
                OnClicked = _ => ChatManager.Instance().SendMessage($"/pdr worldtravel {LuminaWrapper.GetWorldName(targetWorldID)}")
            };
        }
    }

    private class PlayerInfo
    {
        public ulong  ContentID { get; set; }
        public string Name      { get; set; } = string.Empty;
        public string Nickname  { get; set; } = string.Empty;
        public string Remark    { get; set; } = string.Empty;
    }

    #region 常量

    [IPCProvider("DailyRoutines.Modules.OptimizedFriendlist.GetRemarkByContentID")]
    private string GetRemarkByContentID
    (
        ulong contentID
    ) =>
        config.PlayerInfos.TryGetValue(contentID, out var info) ?
            !string.IsNullOrWhiteSpace(info.Remark) ?
                info.Remark :
                string.Empty :
            string.Empty;

    [IPCProvider("DailyRoutines.Modules.OptimizedFriendlist.GetNicknameByContentID")]
    private string GetNicknameByContentID
    (
        ulong contentID
    ) =>
        config.PlayerInfos.TryGetValue(contentID, out var info) ?
            !string.IsNullOrWhiteSpace(info.Nickname) ?
                info.Nickname :
                string.Empty :
            string.Empty;

    #endregion
}
