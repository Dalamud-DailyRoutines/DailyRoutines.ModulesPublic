using System.Collections.Concurrent;
using DailyRoutines.Common.KamiToolKit.Nodes;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Common.RemoteInteraction.Enums;
using DailyRoutines.Common.RemoteInteraction.Helpers;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using DailyRoutines.RemoteInteraction.PlayerInfo;
using DailyRoutines.RemoteInteraction.UsedNames;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using Lumina.Text.ReadOnly;
using OmenTools.Dalamud;
using OmenTools.Dalamud.Attributes;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;
using OmenTools.Threading.TaskHelper;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe class OptimizedFriendList : ModuleBase
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

    private ModifyInfoMenuItem          modifyInfoItem    = null!;
    private TeleportFriendZoneMenuItem  teleportZoneItem  = null!;
    private TeleportFriendWorldMenuItem teleportWorldItem = null!;

    private TextInputNode?     searchInputNode;
    private TextureButtonNode? searchSettingButtonNode;

    private DRFriendlistRemarkEdit?    remarkEditAddon;
    private DRFriendlistSearchSetting? searchSettingAddon;

    private string searchString = string.Empty;

    private readonly List<IDisposable> infoTokens = [];

    public OptimizedFriendList() =>
        modifyInfoItem = new(this, TaskHelper);

    protected override void Init()
    {
        config     =   Config.Load(this) ?? new();
        TaskHelper ??= new();

        modifyInfoItem    = new(this, TaskHelper);
        teleportZoneItem  = new();
        teleportWorldItem = new();

        remarkEditAddon ??= new(this)
        {
            InternalName = "DRFriendlistRemarkEdit",
            Title        = Lang.Get("OptimizedFriendList-ContextMenu-NicknameAndRemark"),
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
        ContextMenuManager.Instance().Reg(teleportZoneItem);
        ContextMenuManager.Instance().Reg(teleportWorldItem);
    }

    protected override void Uninit()
    {
        ContextMenuManager.Instance().Unreg(modifyInfoItem);
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

    private void ReplaceAtkString
    (
        int              index,
        ReadOnlySeString str
    )
    {
        using var utf8String = new Utf8String(str);
        AtkStage.Instance()->GetStringArrayData(StringArrayType.FriendList)->SetValue(index, utf8String.StringPtr);
    }

    private void ApplyDisplayModification
    (
        TaskHelper? taskHelper
    )
    {
        var addon = FriendList;
        if (!addon->IsAddonAndNodesReady()) return;

        var info = InfoProxyFriendList.Instance();

        var isAnyUpdate = false;

        for (var i = 0; i < info->EntryCount; i++)
        {
            var data = info->CharDataSpan[i];

            var existedName = AtkStage.Instance()->GetStringArrayData(StringArrayType.FriendList)->StringArray[0 + (5 * i)].ToString();

            if (existedName == LuminaWrapper.GetAddonText(964))
            {
                isAnyUpdate = true;
                RestoreEntryData(i, data.ContentId, taskHelper);
            }

            if (!config.PlayerInfos.TryGetValue(data.ContentId, out var configInfo)) continue;

            if (!string.IsNullOrWhiteSpace(configInfo.Nickname) && existedName != configInfo.Nickname)
            {
                isAnyUpdate = true;

                using var nicknameBuilder = new RentedSeStringBuilder();
                nicknameBuilder.Builder
                               .PushColorType(37)
                               .Append($"{configInfo.Nickname}")
                               .PopColorType();

                // 名字
                ReplaceAtkString(0 + (5 * i), nicknameBuilder.Builder.ToReadOnlySeString());
            }

            var existedRemark = AtkStage.Instance()->GetStringArrayData(StringArrayType.FriendList)->StringArray[3 + (5 * i)].ToString();

            if (!string.IsNullOrWhiteSpace(configInfo.Remark))
            {
                var remarkText = $"{LuminaWrapper.GetAddonText(13294).TrimEnd(':')}: {configInfo.Remark}" +
                                 (string.IsNullOrWhiteSpace(configInfo.Nickname) ?
                                      string.Empty :
                                      $"\n{LuminaWrapper.GetAddonText(9818)}: {data.NameString}");

                if (remarkText == existedRemark) continue;
                isAnyUpdate = true;

                // 在线状态
                ReplaceAtkString(3 + (5 * i), remarkText);
            }
        }

        if (!isAnyUpdate || taskHelper == null) return;

        RequestInfoUpdate(taskHelper);
    }

    private static void RequestInfoUpdate
    (
        TaskHelper taskHelper
    )
    {
        taskHelper.Abort();

        if (FriendList == null) return;

        taskHelper.Enqueue
        (() =>
            {
                if (FriendList == null) return;
                FriendList->OnRequestedUpdate(AtkStage.Instance()->GetNumberArrayData(), AtkStage.Instance()->GetStringArrayData());
            }
        );
        taskHelper.DelayNext(100);
        taskHelper.Enqueue
        (() =>
            {
                if (FriendList == null) return;
                FriendList->OnRequestedUpdate(AtkStage.Instance()->GetNumberArrayData(), AtkStage.Instance()->GetStringArrayData());
            }
        );
    }

    private bool MatchesSearch
    (
        string filter
    )
    {
        if (string.IsNullOrWhiteSpace(searchString))
            return true;

        if (string.IsNullOrWhiteSpace(filter))
            return false;

        if (searchString.StartsWith('^'))
            return filter.StartsWith(searchString[1..], StringComparison.InvariantCultureIgnoreCase);

        if (searchString.EndsWith('$'))
            return filter.EndsWith(searchString[..^1], StringComparison.InvariantCultureIgnoreCase);

        return filter.Contains(searchString, StringComparison.InvariantCultureIgnoreCase);
    }

    protected void ApplySearchFilter
    (
        string      filter,
        TaskHelper? taskHelper
    )
    {
        var info = InfoProxyFriendList.Instance();

        if (string.IsNullOrWhiteSpace(filter))
        {
            info->ApplyFilters();
            return;
        }

        var resets           = new Dictionary<ulong, uint>();
        var resetFilterGroup = info->FilterGroup;
        info->FilterGroup = InfoProxyCommonList.DisplayGroup.None;

        var entryCount = info->GetEntryCount();

        for (var i = 0; i < entryCount; i++)
        {
            var entry = info->GetEntry((uint)i);
            if (entry == null) continue;

            var data = info->CharDataSpan[i];
            resets.Add(entry->ContentId, entry->ExtraFlags);

            if (config.IgnoredGroup[(int)entry->Group])
            {
                entry->ExtraFlags = (entry->ExtraFlags & 0xFFFF) | ((uint)(1 & 0xFF) << 16); // 添加隐藏标记
                continue;
            }

            var        matchResult = false;
            PlayerInfo configInfo  = null;

            if (config.SearchName)
            {
                var entryNameString = entry->NameString;
                if (string.IsNullOrEmpty(entry->NameString)) // 搜索会导致非本大区角色被重新刷新为（无法获得角色情报） 需要重新配置
                    RestoreEntryData(i, data.ContentId, taskHelper, name => entryNameString = name);

                matchResult |= MatchesSearch(entryNameString);
            }

            if (config.SearchNickname)
            {
                if (config.PlayerInfos.TryGetValue(data.ContentId, out configInfo))
                    matchResult |= MatchesSearch(configInfo.Nickname);
            }

            if (config.SearchRemark)
            {
                if (config.PlayerInfos.TryGetValue(data.ContentId, out configInfo))
                    matchResult |= MatchesSearch(configInfo.Remark);
            }

            if ((resetFilterGroup == InfoProxyCommonList.DisplayGroup.All || entry->Group == resetFilterGroup) && matchResult)
                entry->ExtraFlags &= 0xFFFF; // 去除隐藏标记
            else
                entry->ExtraFlags = (entry->ExtraFlags & 0xFFFF) | ((uint)(1 & 0xFF) << 16);
        }

        info->ApplyFilters();
        info->FilterGroup = resetFilterGroup;

        foreach (var pair in resets)
        {
            var entry = info->GetEntryByContentId(pair.Key);
            entry->ExtraFlags = pair.Value;
        }
    }

    private void RestoreEntryData
    (
        int             index,
        ulong           contentID,
        TaskHelper?     taskHelper,
        Action<string>? onNameResolved = null
    )
    {
        var region = WorldRegionResolver.Resolve(GameState.HomeWorld);
        _ = RemotePlayerInfo.GetOrRequest(contentID, region);

        var observer = RemotePlayerInfo.Observe
        (
            contentID,
            region,
            snapshot =>
            {
                if (!snapshot.HasValue || snapshot.Value is not { } playerInfo)
                    return;

                if (FriendList == null) return;

                using var nameBuilder = new RentedSeStringBuilder();
                nameBuilder.Builder
                           .PushColorType(32)
                           .Append(playerInfo.Name)
                           .PopColorType();

                ReplaceAtkString(0 + (5 * index), nameBuilder.Builder.ToReadOnlySeString());

                using var worldBuilder = new RentedSeStringBuilder();
                worldBuilder.Builder
                            .Append(LuminaWrapper.GetWorldName(playerInfo.WorldID))
                            .AppendIcon((uint)BitmapFontIcon.CrossWorld)
                            .Append(LuminaWrapper.GetWorldDCName(playerInfo.WorldID));

                ReplaceAtkString(1 + (5 * index), worldBuilder.Builder.ToReadOnlySeString());

                ReplaceAtkString(3 + (5 * index), LuminaWrapper.GetAddonText(1351));

                onNameResolved?.Invoke(playerInfo.Name);

                if (taskHelper != null)
                    RequestInfoUpdate(taskHelper);
            }
        );
        infoTokens.Add(observer);
    }

    #region 事件

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

    #endregion

    private class Config : ModuleConfig
    {
        public ConcurrentDictionary<ulong, PlayerInfo> PlayerInfos = [];

        public bool[] IgnoredGroup = new bool[8];

        public bool SearchName     = true;
        public bool SearchNickname = true;
        public bool SearchRemark   = true;
    }

    private class DRFriendlistRemarkEdit
    (
        OptimizedFriendList instance
    ) : NativeAddon
    {
        private TextButtonNode clearButtonNode;

        private TextButtonNode confirmButtonNode;
        private TextInputNode  nicknameInputNode;

        private TextNode nicknameNode;

        private TextNode               playerNameNode;
        private TextButtonNode         quertUsedNameButtonNode;
        private TextMultiLineInputNode remarkInputNode;

        private TextNode remarkNode;
        public  ulong    ContentID { get; private set; }
        public  string   Name      { get; private set; } = string.Empty;
        public  string   WorldName { get; private set; } = string.Empty;

        private OptimizedFriendList Instance { get; init; } = instance;

        protected override void OnSetup
        (
            AtkUnitBase*   addon,
            Span<AtkValue> atkValues
        )
        {
            if (ContentID == 0 || string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(WorldName))
            {
                Close();
                return;
            }

            var existedNickname = Instance.config.PlayerInfos.GetValueOrDefault(ContentID, new()).Nickname;
            var existedRemark   = Instance.config.PlayerInfos.GetValueOrDefault(ContentID, new()).Remark;

            using var rented = new RentedSeStringBuilder();
            rented.Builder
                  .Append(Name)
                  .AppendIcon((uint)BitmapFontIcon.CrossWorld)
                  .Append(WorldName);

            playerNameNode = new()
            {
                Position      = new(10, 36),
                Size          = new(100, 48),
                String        = rented.Builder.ToReadOnlySeString(),
                FontSize      = 24,
                AlignmentType = AlignmentType.Left,
                TextFlags     = TextFlags.Bold
            };
            playerNameNode.AttachNode(this);

            nicknameNode = new()
            {
                Position      = new(10, 80),
                Size          = new(100, 28),
                String        = $"{LuminaWrapper.GetAddonText(15207)}",
                FontSize      = 14,
                AlignmentType = AlignmentType.Left,
                TextFlags     = TextFlags.Bold
            };
            nicknameNode.AttachNode(this);

            nicknameInputNode = new()
            {
                Position      = new(10, 108),
                Size          = new(440, 28),
                MaxCharacters = 64,
                ShowLimitText = true,
                AutoSelectAll = false,
                String        = existedNickname
            };
            nicknameInputNode.AttachNode(this);

            remarkNode = new()
            {
                Position      = new(10, 140),
                Size          = new(100, 28),
                String        = $"{LuminaWrapper.GetAddonText(13294).TrimEnd(':')}",
                FontSize      = 14,
                AlignmentType = AlignmentType.Left,
                TextFlags     = TextFlags.Bold
            };

            remarkNode.AttachNode(this);

            remarkInputNode = new()
            {
                Position      = new(10, 168),
                MaxCharacters = 1024,
                MaxLines      = 5,
                ShowLimitText = true,
                AutoSelectAll = false,
                String        = existedRemark
            };
            remarkInputNode.Flags |= TextInputFlags.MultiLine;

            remarkInputNode.Size = new(440, (remarkInputNode.CurrentTextNode.LineSpacing * 5) + 20);

            remarkInputNode.AttachNode(this);

            confirmButtonNode = new()
            {
                Position = new(10, 264),
                Size     = new(140, 28),
                String   = Lang.Get("Confirm"),
                OnClick = () =>
                {
                    Instance.config.PlayerInfos[ContentID] = new()
                    {
                        ContentID = ContentID,
                        Name      = Name,
                        Nickname  = nicknameInputNode.String.ToString(),
                        Remark    = remarkInputNode.String.ToString()
                    };
                    Instance.config.Save(Instance);

                    InfoProxyFriendList.Instance()->RequestData();
                    Close();
                }
            };
            confirmButtonNode.AttachNode(this);

            clearButtonNode = new()
            {
                Position = new(160, 264),
                Size     = new(140, 28),
                String   = Lang.Get("Clear"),
                OnClick = () =>
                {
                    Instance.config.PlayerInfos.TryRemove(ContentID, out _);
                    Instance.config.Save(Instance);

                    InfoProxyFriendList.Instance()->RequestData();
                    Close();
                }
            };
            clearButtonNode.AttachNode(this);

            quertUsedNameButtonNode = new()
            {
                Position = new(310, 264),
                Size     = new(140, 28),
                String   = Lang.Get("OptimizedFriendList-ObtainUsedNames"),
                OnClick = () =>
                {
                    var contentID = ContentID;
                    var name      = Name;
                    _ = OptimizedFriendListAsyncHelper.QueryUsedNamesAsync(contentID, name, GameState.HomeWorld);
                }
            };
            quertUsedNameButtonNode.AttachNode(this);
        }

        protected override void OnUpdate
        (
            AtkUnitBase* addon
        )
        {
            if (!FriendList->IsAddonAndNodesReady())
                Close();
        }

        protected override void OnFinalize
        (
            AtkUnitBase* addon
        )
        {
            ContentID = 0;
            Name      = string.Empty;
            WorldName = string.Empty;
        }

        public void OpenWithData
        (
            ulong  contentID,
            string name,
            string worldName
        )
        {
            ContentID = contentID;
            Name      = name;
            WorldName = worldName;

            Open();
        }
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
