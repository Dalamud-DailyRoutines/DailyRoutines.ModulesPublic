using System.Collections.Concurrent;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Common.RemoteInteraction.Helpers;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using DailyRoutines.RemoteInteraction.UsedNames;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using OmenTools.Dalamud;
using OmenTools.Dalamud.Attributes;
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

    private string searchString = string.Empty;

    private readonly List<IDisposable> infoTokens = [];
    
    protected override void Init()
    {
        config     =   Config.Load(this) ?? new();
        TaskHelper ??= new();
        
        modifyInfoItem        = new(this, TaskHelper);
        queryUsedNameMenuItem = new();
        teleportZoneItem      = new();
        teleportWorldItem     = new();

        remarkEditAddon ??= new(this)
        {
            InternalName = "DRFriendlistRemarkEdit",
            Title        = Lang.Get("OptimizedFriendList-Addon-Title"),
            Size         = new(460f, 310f)
        };

        searchSettingAddon ??= new(this, TaskHelper)
        {
            InternalName = "DRFriendlistSearchSetting",
            Title        = Lang.Get("OptimizedFriendList-Addon-SearchSetting"),
            Size         = new(230f, 350f)
        };

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
                        OnClick     = () => searchSettingAddon.Toggle()
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

    private static class OptimizedFriendListAsyncHelper
    {
        public static Task QueryUsedNamesAsync
        (
            ulong       contentID,
            string      name,
            uint homeWorldID
        ) =>
            RemoteUsedNames.GetFreshAsync(contentID, WorldRegionResolver.Resolve(GameState.HomeWorld)).AsTask().ContinueWith
            (
                task =>
                {
                    if (task.IsFaulted)
                    {
                        DLog.Error("获取好友曾用名时发生错误", task.Exception?.GetBaseException() ?? new InvalidOperationException("未提供异常信息"));
                        return Task.CompletedTask;
                    }

                    if (task.IsCanceled)
                        return Task.CompletedTask;

                    var data = task.Result;
                    return IFramework.Instance().RunOnTick
                    (() =>
                        {
                            if (data.Count == 0)
                            {
                                var message = Lang.GetSe("OptimizedFriendList-FriendUseNamesNotFound", new PlayerPayload(name, homeWorldID));
                                NotifyHelper.ToastError(message);
                                NotifyHelper.Instance().ChatError(message);
                                return;
                            }

                            // TODO：准备改成一个单独的 Addon 显示
                            NotifyHelper.Instance().Chat($"{Lang.Get("OptimizedFriendList-FriendUseNamesFound", name)}:");
                            var counter = 1;

                            foreach (var nameChange in data)
                            {
                                NotifyHelper.Instance().Chat($"{counter}. {nameChange.ChangedTime}:");
                                NotifyHelper.Instance().Chat($"     {nameChange.BeforeName} -> {nameChange.AfterName}:");
                                counter++;
                            }
                        }
                    );
                },
                TaskScheduler.Default
            ).Unwrap();
    }

    private class DRFriendlistSearchSetting
    (
        OptimizedFriendList instance,
        TaskHelper          taskHelper
    ) : NativeAddon
    {
        private OptimizedFriendList Instance   { get; init; } = instance;
        private TaskHelper          TaskHelper { get; init; } = taskHelper;

        protected override void OnSetup
        (
            AtkUnitBase*   addon,
            Span<AtkValue> atkValues
        )
        {
            var searchTypeTitleNode = new TextNode
            {
                String    = Lang.Get("OptimizedFriendList-SearchType"),
                FontSize  = 16,
                TextFlags = TextFlags.AutoAdjustNodeSize,
                Position  = new(10f, 42f)
            };
            searchTypeTitleNode.AttachNode(this);

            var searchTypeLayoutNode = new VerticalListNode
            {
                Position  = new(20f, searchTypeTitleNode.Position.Y + 28f),
                Alignment = VerticalListAlignment.Left
            };

            var nameCheckboxNode = new CheckboxNode
            {
                Size      = new(80f, 20f),
                IsChecked = Instance.config.SearchName,
                IsEnabled = true,
                String    = Lang.Get("Name"),
                OnClick = newState =>
                {
                    Instance.config.SearchName = newState;
                    Instance.config.Save(Instance);

                    Instance.ApplySearchFilter(Instance.searchString, TaskHelper);
                }
            };
            searchTypeLayoutNode.Height += searchTypeTitleNode.Height;

            var nicknameCheckboxNode = new CheckboxNode
            {
                Size      = new(80f, 20f),
                IsChecked = Instance.config.SearchNickname,
                IsEnabled = true,
                String    = LuminaWrapper.GetAddonText(15207),
                OnClick = newState =>
                {
                    Instance.config.SearchNickname = newState;
                    Instance.config.Save(Instance);

                    Instance.ApplySearchFilter(Instance.searchString, TaskHelper);
                }
            };
            searchTypeLayoutNode.Height += nicknameCheckboxNode.Height;

            var remarkCheckboxNode = new CheckboxNode
            {
                Size      = new(80f, 20f),
                IsChecked = Instance.config.SearchRemark,
                IsEnabled = true,
                String    = LuminaWrapper.GetAddonText(13294).TrimEnd(':'),
                OnClick = newState =>
                {
                    Instance.config.SearchRemark = newState;
                    Instance.config.Save(Instance);

                    Instance.ApplySearchFilter(Instance.searchString, TaskHelper);
                }
            };
            searchTypeLayoutNode.Height += remarkCheckboxNode.Height;

            searchTypeLayoutNode.AddNode([nameCheckboxNode, nicknameCheckboxNode, remarkCheckboxNode]);
            searchTypeLayoutNode.AttachNode(this);

            var searchGroupIgnoreTitleNode = new TextNode
            {
                String    = Lang.Get("OptimizedFriendList-SearchIgnoreGroup"),
                FontSize  = 16,
                TextFlags = TextFlags.AutoAdjustNodeSize,
                Position  = new(10f, searchTypeLayoutNode.Position.Y + searchTypeLayoutNode.Height + 12f)
            };
            searchGroupIgnoreTitleNode.AttachNode(this);

            var searchGroupIgnoreLayoutNode = new VerticalListNode
            {
                Position  = new(20f, searchGroupIgnoreTitleNode.Position.Y + 28f),
                Alignment = VerticalListAlignment.Left
            };


            for (var i = 0; i < 8; i++)
            {
                var index = i;

                var groupFormatText = ISeStringEvaluator.Instance().EvaluateFromAddon(12925, [index + 1]);
                var groupCheckboxNode = new CheckboxNode
                {
                    Size      = new(80f, 20f),
                    IsChecked = Instance.config.IgnoredGroup[i],
                    IsEnabled = true,
                    String    = groupFormatText,
                    OnClick = newState =>
                    {
                        Instance.config.IgnoredGroup[index] = newState;
                        Instance.config.Save(Instance);

                        Instance.ApplySearchFilter(Instance.searchString, TaskHelper);
                    }
                };

                searchGroupIgnoreLayoutNode.Height += groupCheckboxNode.Height;
                searchGroupIgnoreLayoutNode.AddNode(groupCheckboxNode);
            }

            searchGroupIgnoreLayoutNode.AttachNode(this);
        }

        protected override void OnUpdate
        (
            AtkUnitBase* addon
        )
        {
            if (FriendList == null)
                Close();
        }
    }

    private sealed class QueryUsedNameMenuItem : ContextMenuEntry
    {
        public override string Identifier =>
            nameof(OptimizedFriendList);

        public override ContextMenuItem? Create
        (
            ContextMenuOpenedArgs args
        )
        {
            if (args.AddonName != "FriendList") return null;

            var contentID = args.TargetContentID;
            if (contentID == 0 || string.IsNullOrEmpty(args.TargetName)) return null;

            return new()
            {
                Name = Lang.Get("OptimizedFriendList-ContextMenu-QueryUsedNames"),
                OnClicked = _ =>
                {
                    var targetName    = args.TargetName;
                    var targetWorldID = (uint)args.TargetHomeWorldID;

                    OptimizedFriendListAsyncHelper.QueryUsedNamesAsync(contentID, targetName, targetWorldID);
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
            if (args.AddonName != "FriendList") return null;

            var contentID = args.TargetContentID;
            if (contentID == 0 || string.IsNullOrWhiteSpace(args.TargetName)) return null;

            return new()
            {
                Name = Lang.Get("OptimizedFriendList-ContextMenu-NicknameAndRemark"),
                OnClicked = _ =>
                {
                    var targetName    = args.TargetName;
                    var targetWorldID = (uint)args.TargetHomeWorldID;

                    if (instance.remarkEditAddon.IsOpen)
                    {
                        instance.remarkEditAddon.Close();

                        taskHelper.DelayNext(100);
                        taskHelper.Enqueue(() => !instance.remarkEditAddon.IsOpen);
                        taskHelper.Enqueue(() => instance.remarkEditAddon.OpenWithData(contentID, targetName, LuminaWrapper.GetWorldName(targetWorldID)));
                    }
                    else
                        instance.remarkEditAddon.OpenWithData(contentID, targetName, LuminaWrapper.GetWorldName(targetWorldID));

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
            if (args.AddonName != "FriendList") return null;

            var targetCharacter = args.TargetCharacter;
            if (targetCharacter == null) return null;

            if (!TryGetAetheryteID(targetCharacter->Location, out var aetheryteID)) return null;

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
