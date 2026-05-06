using System.Collections.Concurrent;
using DailyRoutines.Common.Info.Abstractions;
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
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using OmenTools.Dalamud;
using OmenTools.Dalamud.Attributes;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;
using OmenTools.Threading.TaskHelper;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedFriendList : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = Lang.Get("OptimizedFriendListTitle"),
        Description         = Lang.Get("OptimizedFriendListDescription"),
        Category            = ModuleCategory.UIOptimization,
        ModulesPrerequisite = ["FastWorldTravel"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private Config config = null!;

    private readonly ModifyInfoMenuItem          modifyInfoItem;
    private readonly TeleportFriendZoneMenuItem  teleportZoneItem  = new();
    private readonly TeleportFriendWorldMenuItem teleportWorldItem = new();
    
    private TextInputNode?     searchInputNode;
    private TextureButtonNode? searchSettingButtonNode;

    private DRFriendlistRemarkEdit?    remarkEditAddon;
    private DRFriendlistSearchSetting? searchSettingAddon;

    private string searchString = string.Empty;

    private readonly List<nint>        utf8Strings = [];
    private readonly List<IDisposable> infoTokens  = [];

    public OptimizedFriendList() =>
        modifyInfoItem = new(this, TaskHelper);

    protected override void Init()
    {
        config     =   Config.Load(this) ?? new();
        TaskHelper ??= new();

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
        
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,          "FriendList", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, "FriendList", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize,        "FriendList", OnAddon);
        if (FriendList->IsAddonAndNodesReady())
            OnAddon(AddonEvent.PostSetup, null);

        DService.Instance().ContextMenu.OnMenuOpened += OnContextMenu;
    }

    protected override void Uninit()
    {
        DService.Instance().ContextMenu.OnMenuOpened -= OnContextMenu;

        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
        OnAddon(AddonEvent.PreFinalize, null);

        remarkEditAddon?.Dispose();
        remarkEditAddon = null;

        searchSettingAddon?.Dispose();
        searchSettingAddon = null;

        if (FriendList->IsAddonAndNodesReady())
            InfoProxyFriendList.Instance()->RequestData();
    }

    private void ReplaceAtkString(int index, Utf8String* newString)
    {
        if (newString == null) return;

        utf8Strings.Add((nint)newString);
        AtkStage.Instance()->GetStringArrayData(StringArrayType.FriendList)->StringArray[index] = newString->StringPtr;
    }

    private void ApplyDisplayModification(TaskHelper? taskHelper)
    {
        var addon = FriendList;
        if (!addon->IsAddonAndNodesReady()) return;

        var info = InfoProxyFriendList.Instance();

        var isAnyUpdate = false;

        for (var i = 0; i < info->EntryCount; i++)
        {
            var data = info->CharDataSpan[i];

            var existedName = SeString.Parse(AtkStage.Instance()->GetStringArrayData(StringArrayType.FriendList)->StringArray[0 + 5 * i].Value).TextValue;

            if (existedName == LuminaWrapper.GetAddonText(964))
            {
                isAnyUpdate = true;
                RestoreEntryData(i, data.ContentId, taskHelper);
            }

            if (!config.PlayerInfos.TryGetValue(data.ContentId, out var configInfo)) continue;

            if (!string.IsNullOrWhiteSpace(configInfo.Nickname) && existedName != configInfo.Nickname)
            {
                isAnyUpdate = true;

                var nicknameBuilder = new SeStringBuilder();
                nicknameBuilder.AddUiForeground($"{configInfo.Nickname}", 37);

                var nicknameString = Utf8String.FromSequence(nicknameBuilder.Build().EncodeWithNullTerminator());

                // 名字
                ReplaceAtkString(0 + 5 * i, nicknameString);
            }

            var ptr           = AtkStage.Instance()->GetStringArrayData(StringArrayType.FriendList)->StringArray[3 + 5 * i];
            var existedRemark = SeString.Parse(ptr.Value).TextValue;

            if (!string.IsNullOrWhiteSpace(configInfo.Remark))
            {
                var remarkText = $"{LuminaWrapper.GetAddonText(13294).TrimEnd(':')}: {configInfo.Remark}" +
                                 (string.IsNullOrWhiteSpace(configInfo.Nickname)
                                      ? string.Empty
                                      : $"\n{LuminaWrapper.GetAddonText(9818)}: {data.NameString}");

                if (remarkText == existedRemark) continue;
                isAnyUpdate = true;

                var remarkString = Utf8String.FromString(remarkText);

                // 在线状态
                ReplaceAtkString(3 + 5 * i, remarkString);
            }
        }

        if (!isAnyUpdate || taskHelper == null) return;

        RequestInfoUpdate(taskHelper);
    }

    private static void RequestInfoUpdate(TaskHelper taskHelper)
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

    private bool MatchesSearch(string filter)
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

    protected void ApplySearchFilter(string filter, TaskHelper? taskHelper)
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
                entry->ExtraFlags = entry->ExtraFlags & 0xFFFF | (uint)(1 & 0xFF) << 16; // 添加隐藏标记
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
                entry->ExtraFlags = entry->ExtraFlags & 0xFFFF | (uint)(1 & 0xFF) << 16;
        }

        info->ApplyFilters();
        info->FilterGroup = resetFilterGroup;

        foreach (var pair in resets)
        {
            var entry = info->GetEntryByContentId(pair.Key);
            entry->ExtraFlags = pair.Value;
        }
    }

    private void RestoreEntryData(int index, ulong contentID, TaskHelper? taskHelper, Action<string>? onNameResolved = null)
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

                var nameBuilder = new SeStringBuilder();
                nameBuilder.AddUiForeground($"{playerInfo.Name}", 32);

                var nameString = Utf8String.FromSequence(nameBuilder.Build().EncodeWithNullTerminator());
                ReplaceAtkString(0 + 5 * index, nameString);

                var worldBuilder = new SeStringBuilder();
                worldBuilder.AddText($"{LuminaWrapper.GetWorldName(playerInfo.WorldID)}");
                worldBuilder.AddIcon(BitmapFontIcon.CrossWorld);
                worldBuilder.AddText($"{LuminaWrapper.GetWorldDCName(playerInfo.WorldID)}");

                var worldString = Utf8String.FromSequence(worldBuilder.Build().EncodeWithNullTerminator());
                ReplaceAtkString(1 + 5 * index, worldString);

                var onlineStatusString = Utf8String.FromString(LuminaWrapper.GetAddonText(1351));
                ReplaceAtkString(3 + 5 * index, onlineStatusString);

                onNameResolved?.Invoke(playerInfo.Name);

                if (taskHelper != null)
                    RequestInfoUpdate(taskHelper);
            }
        );
        infoTokens.Add(observer);
    }
    
    #region 事件

    private void OnContextMenu(IMenuOpenedArgs args)
    {
        if (modifyInfoItem.IsDisplay(args))
            args.AddMenuItem(modifyInfoItem.Get());

        if (teleportZoneItem.IsDisplay(args))
            args.AddMenuItem(teleportZoneItem.Get());

        if (teleportWorldItem.IsDisplay(args))
            args.AddMenuItem(teleportWorldItem.Get());
    }

    private void OnAddon(AddonEvent type, AddonArgs? args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                if (FriendList != null)
                {
                    searchInputNode ??= new()
                    {
                        IsVisible     = true,
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
                        IsVisible   = true,
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

                        DService.Instance().Framework.RunOnTick
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
                        DService.Instance().Framework.RunOnTick
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
                searchInputNode?.Dispose();
                searchInputNode = null;

                searchSettingButtonNode?.Dispose();
                searchSettingButtonNode = null;

                infoTokens.ForEach(static x => x.Dispose());
                infoTokens.Clear();

                utf8Strings.ForEach
                (x =>
                    {
                        var ptr = (Utf8String*)x;
                        if (ptr == null) return;

                        ptr->Dtor(true);
                    }
                );
                utf8Strings.Clear();
                break;
        }
    }

    #endregion

    private class Config : ModuleConfig
    {
        public bool[]                                  IgnoredGroup = new bool[8];
        public ConcurrentDictionary<ulong, PlayerInfo> PlayerInfos  = [];

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

        protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
        {
            if (ContentID == 0 || string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(WorldName))
            {
                Close();
                return;
            }

            var existedNickname = Instance.config.PlayerInfos.GetValueOrDefault(ContentID, new()).Nickname;
            var existedRemark   = Instance.config.PlayerInfos.GetValueOrDefault(ContentID, new()).Remark;

            playerNameNode = new()
            {
                IsVisible = true,
                Position  = new(10, 36),
                Size      = new(100, 48),
                String = new SeStringBuilder()
                         .Append(Name)
                         .AddIcon(BitmapFontIcon.CrossWorld)
                         .Append(WorldName)
                         .Build()
                         .Encode(),
                FontSize      = 24,
                AlignmentType = AlignmentType.Left,
                TextFlags     = TextFlags.Bold
            };
            playerNameNode.AttachNode(this);

            nicknameNode = new()
            {
                IsVisible     = true,
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
                IsVisible     = true,
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
                IsVisible     = true,
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
                IsVisible     = true,
                Position      = new(10, 168),
                MaxCharacters = 1024,
                MaxLines      = 5,
                ShowLimitText = true,
                AutoSelectAll = false,
                String        = existedRemark
            };

            remarkInputNode.Size = new(440, remarkInputNode.CurrentTextNode.LineSpacing * 5 + 20);

            remarkInputNode.AttachNode(this);

            confirmButtonNode = new()
            {
                Position  = new(10, 264),
                Size      = new(140, 28),
                IsVisible = true,
                String    = Lang.Get("Confirm"),
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
                Position  = new(160, 264),
                Size      = new(140, 28),
                IsVisible = true,
                String    = Lang.Get("Clear"),
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
                Position  = new(310, 264),
                Size      = new(140, 28),
                IsVisible = true,
                String    = Lang.Get("OptimizedFriendList-ObtainUsedNames"),
                OnClick = () =>
                {
                    var contentID = ContentID;
                    var name      = Name;
                    var region    = WorldRegionResolver.Resolve(GameState.HomeWorld);
                    _ = OptimizedFriendListAsyncHelper.QueryUsedNamesAsync(contentID, name, region);
                }
            };
            quertUsedNameButtonNode.AttachNode(this);
        }

        protected override void OnUpdate(AtkUnitBase* addon)
        {
            if (!FriendList->IsAddonAndNodesReady())
                Close();
        }

        protected override void OnFinalize(AtkUnitBase* addon)
        {
            ContentID = 0;
            Name      = string.Empty;
            WorldName = string.Empty;
        }

        public void OpenWithData(ulong contentID, string name, string worldName)
        {
            ContentID = contentID;
            Name      = name;
            WorldName = worldName;

            Open();
        }
    }

    private static class OptimizedFriendListAsyncHelper
    {
        public static Task QueryUsedNamesAsync(ulong contentID, string name, WorldRegion region) =>
            RemoteUsedNames.GetFreshAsync(contentID, region).AsTask().ContinueWith
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
                    return DService.Instance().Framework.RunOnTick
                    (() =>
                        {
                            if (data.Count == 0)
                            {
                                NotifyHelper.Instance().Chat(Lang.Get("OptimizedFriendList-FriendUseNamesNotFound", name));
                                return;
                            }

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

        protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
        {
            var searchTypeTitleNode = new TextNode
            {
                IsVisible = true,
                String    = Lang.Get("OptimizedFriendList-SearchType"),
                FontSize  = 16,
                TextFlags = TextFlags.AutoAdjustNodeSize,
                Position  = new(10f, 42f)
            };
            searchTypeTitleNode.AttachNode(this);

            var searchTypeLayoutNode = new VerticalListNode
            {
                IsVisible = true,
                Position  = new(20f, searchTypeTitleNode.Position.Y + 28f),
                Alignment = VerticalListAlignment.Left
            };

            var nameCheckboxNode = new CheckboxNode
            {
                Size      = new(80f, 20f),
                IsVisible = true,
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
                IsVisible = true,
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
                IsVisible = true,
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
                IsVisible = true,
                String    = Lang.Get("OptimizedFriendList-SearchIgnoreGroup"),
                FontSize  = 16,
                TextFlags = TextFlags.AutoAdjustNodeSize,
                Position  = new(10f, searchTypeLayoutNode.Position.Y + searchTypeLayoutNode.Height + 12f)
            };
            searchGroupIgnoreTitleNode.AttachNode(this);

            var searchGroupIgnoreLayoutNode = new VerticalListNode
            {
                IsVisible = true,
                Position  = new(20f, searchGroupIgnoreTitleNode.Position.Y + 28f),
                Alignment = VerticalListAlignment.Left
            };

            var groupFormatText = LuminaWrapper.GetAddonTextSeString(12925);

            for (var i = 0; i < 8; i++)
            {
                var index = i;

                groupFormatText.Payloads[1] = new TextPayload($"{index + 1}");
                var groupCheckboxNode = new CheckboxNode
                {
                    Size      = new(80f, 20f),
                    IsVisible = true,
                    IsChecked = Instance.config.IgnoredGroup[i],
                    IsEnabled = true,
                    String    = groupFormatText.Encode(),
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

        protected override void OnUpdate(AtkUnitBase* addon)
        {
            if (FriendList == null)
                Close();
        }
    }

    private class ModifyInfoMenuItem
    (
        OptimizedFriendList instance,
        TaskHelper          taskHelper
    ) : MenuItemBase
    {
        public override string Name       { get; protected set; } = Lang.Get("OptimizedFriendList-ContextMenu-NicknameAndRemark");
        public override string Identifier { get; protected set; } = nameof(OptimizedFriendList);

        public override bool IsDisplay(IMenuOpenedArgs args) =>
            args is { AddonName: "FriendList", Target: MenuTargetDefault target } &&
            target.TargetContentId != 0                                           &&
            !string.IsNullOrWhiteSpace(target.TargetName);

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            if (args.Target is not MenuTargetDefault target) return;

            if (instance.remarkEditAddon.IsOpen)
            {
                instance.remarkEditAddon.Close();

                taskHelper.DelayNext(100);
                taskHelper.Enqueue(() => !instance.remarkEditAddon.IsOpen);
                taskHelper.Enqueue(() => instance.remarkEditAddon.OpenWithData(target.TargetContentId, target.TargetName, target.TargetHomeWorld.Value.Name.ToString()));
            }
            else
                instance.remarkEditAddon.OpenWithData(target.TargetContentId, target.TargetName, target.TargetHomeWorld.Value.Name.ToString());

            instance.ApplySearchFilter(instance.searchString, taskHelper);
        }
    }

    private class TeleportFriendZoneMenuItem : MenuItemBase
    {
        private         uint   aetheryteID;
        public override string Name       { get; protected set; } = Lang.Get("OptimizedFriendList-ContextMenu-TeleportToFriendZone");
        public override string Identifier { get; protected set; } = nameof(OptimizedFriendList);

        protected override void OnClicked(IMenuItemClickedArgs args) =>
            Telepo.Instance()->Teleport(aetheryteID, 0);

        public override bool IsDisplay(IMenuOpenedArgs args) =>
            args is { AddonName : "FriendList", Target: MenuTargetDefault { TargetCharacter: not null } target } &&
            GetAetheryteID(target.TargetCharacter.Location.RowId, out aetheryteID);

        private static bool GetAetheryteID(uint zoneID, out uint aetheryteID)
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

            aetheryteID = DService.Instance().AetheryteList
                                  .Where(aetheryte => aetheryte.TerritoryID == zoneID)
                                  .Select(aetheryte => aetheryte.AetheryteID)
                                  .FirstOrDefault();

            return aetheryteID > 0;
        }
    }

    private class TeleportFriendWorldMenuItem : MenuItemBase
    {
        private         uint   friendWorldID;
        public override string Name       { get; protected set; } = Lang.Get("OptimizedFriendList-ContextMenu-TeleportToFriendWorld");
        public override string Identifier { get; protected set; } = nameof(OptimizedFriendList);

        public override bool IsDisplay(IMenuOpenedArgs args)
        {
            if ((ModuleManager.Instance().IsModuleEnabled("FastWorldTravel") ?? false)                                                              &&
                args is { AddonName: "FriendList", Target: MenuTargetDefault { TargetCharacter.CurrentWorld.RowId: var targetWorldID } } &&
                targetWorldID != GameState.CurrentWorld)
            {
                friendWorldID = targetWorldID;
                return true;
            }

            return false;
        }

        protected override void OnClicked(IMenuItemClickedArgs args) =>
            ChatManager.Instance().SendMessage($"/pdr worldtravel {LuminaWrapper.GetWorldName(friendWorldID)}");
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
    private string GetRemarkByContentID(ulong contentID) =>
        config.PlayerInfos.TryGetValue(contentID, out var info) ? !string.IsNullOrWhiteSpace(info.Remark) ? info.Remark : string.Empty : string.Empty;

    [IPCProvider("DailyRoutines.Modules.OptimizedFriendlist.GetNicknameByContentID")]
    private string GetNicknameByContentID(ulong contentID) =>
        config.PlayerInfos.TryGetValue(contentID, out var info) ? !string.IsNullOrWhiteSpace(info.Nickname) ? info.Nickname : string.Empty : string.Empty;

    #endregion
}
