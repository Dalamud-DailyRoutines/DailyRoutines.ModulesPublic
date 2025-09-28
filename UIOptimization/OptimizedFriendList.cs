using System;
using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
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
using KamiToolKit.Addon;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedFriendList : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = GetLoc("OptimizedFriendListTitle"),
        Description         = GetLoc("OptimizedFriendListDescription"),
        Category            = ModuleCategories.UIOptimization,
        ModulesPrerequisite = ["WorldTravelCommand"]
    };
    
    private static          ModifyInfoMenuItem          ModifyInfoItem    = null!;
    private static readonly TeleportFriendZoneMenuItem  TeleportZoneItem  = new();
    private static readonly TeleportFriendWorldMenuItem TeleportWorldItem = new();

    private static Config ModuleConfig = null!;

    private static TextInputNode?      SearchInputNode;
    private static TextureButtonNode?  SearchSettingButtonNode;

    private static DRFriendlistRemarkEdit?    RemarkEditAddon;
    private static DRFriendlistSearchSetting? SearchSettingAddon;
    
    private static string SearchString = string.Empty;
    
    private static readonly List<nint>                             Utf8Strings = [];
    private static readonly List<PlayerUsedNamesSubscriptionToken> Tokens      = [];
    private static readonly List<PlayerInfoSubscriptionToken>      InfoTokens  = [];

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        TaskHelper ??= new();

        RemarkEditAddon ??= new(this)
        {
            InternalName          = "DRFriendlistRemarkEdit",
            Title                 = GetLoc("OptimizedFriendList-ContextMenu-NicknameAndRemark"),
            Size                  = new(460f, 255f),
            Position              = new(800f, 350f),
            NativeController      = Service.AddonController,
            RememberClosePosition = true
        };
        
        SearchSettingAddon ??= new(this)
        {
            InternalName          = "DRFriendlistSearchSetting",
            Title                 = GetLoc("OptimizedFriendList-Addon-SearchSetting"),
            Size                  = new(230f, 350f),
            Position              = new(800f, 350f),
            NativeController      = Service.AddonController,
            RememberClosePosition = true
        };

        ModifyInfoItem = new(TaskHelper);
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,           "FriendList", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "FriendList", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate,  "FriendList", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize,         "FriendList", OnAddon);
        if (IsAddonAndNodesReady(FriendList)) 
            OnAddon(AddonEvent.PostSetup, null);

        DService.ContextMenu.OnMenuOpened += OnContextMenu;
    }

    private static void OnContextMenu(IMenuOpenedArgs args)
    {
        if (ModifyInfoItem.IsDisplay(args))
            args.AddMenuItem(ModifyInfoItem.Get());
        
        if (TeleportZoneItem.IsDisplay(args))
            args.AddMenuItem(TeleportZoneItem.Get());

        if (TeleportWorldItem.IsDisplay(args))
            args.AddMenuItem(TeleportWorldItem.Get());
    }

    private void OnAddon(AddonEvent type, AddonArgs? args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                if (FriendList != null)
                {
                    SearchInputNode ??= new()
                    {
                        IsVisible     = true,
                        Position      = new(10f, 425f),
                        Size          = new(200.0f, 35f),
                        MaxCharacters = 20,
                        ShowLimitText = true,
                        OnInputReceived = x =>
                        {
                            SearchString = x.TextValue;
                            ApplyFilters(SearchString);
                        },
                        OnInputComplete = x =>
                        {
                            SearchString = x.TextValue;
                            ApplyFilters(SearchString);
                        },
                    };

                    SearchInputNode.CursorNode.ScaleY        =  1.4f;
                    SearchInputNode.CurrentTextNode.FontSize =  14;
                    SearchInputNode.CurrentTextNode.Y        += 3f;

                    Service.AddonController.AttachNode(SearchInputNode, FriendList->GetNodeById(20));

                    SearchSettingButtonNode ??= new()
                    {
                        Position    = new(215f, 430f),
                        Size        = new(25f, 25f),
                        IsVisible   = true,
                        IsChecked   = ModuleConfig.SearchName,
                        IsEnabled   = true,
                        TexturePath = "ui/uld/CircleButtons_hr1.tex",
                        TextureSize = new(28, 28),
                        OnClick     = () => SearchSettingAddon.Toggle(),
                    };

                    Service.AddonController.AttachNode(SearchSettingButtonNode, FriendList->GetNodeById(20));

                    SearchString = string.Empty;
                }
                
                if (Throttler.Throttle("OptimizedFriendList-OnRequestFriendList", 10_000))
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
                        
                        DService.Framework.RunOnTick(() =>
                        {
                            if (FriendList == null) return;
                            
                            agent->RequestFriendInfo(chara.ContentId);
                        }, TimeSpan.FromMilliseconds(10 * validCounter));

                        validCounter++;
                    }
                    
                    if (validCounter > 0)
                    {
                        DService.Framework.RunOnTick(() =>
                        {
                            if (FriendList == null) return;

                            Modify(TaskHelper);
                        }, TimeSpan.FromMilliseconds(10 * (validCounter + 1)));
                    }
                }
                
                Modify(TaskHelper);
                break;
            case AddonEvent.PostRequestedUpdate:
                Modify(TaskHelper);
                break;
            case AddonEvent.PreRequestedUpdate:
                ApplyFilters(SearchString);
                break;
            case AddonEvent.PreFinalize:
                Service.AddonController.DetachNode(SearchInputNode);
                SearchInputNode = null;

                Service.AddonController.DetachNode(SearchSettingButtonNode);
                SearchSettingButtonNode = null;

                Tokens.ForEach(x => OnlineDataManager.GetRequest<PlayerUsedNamesRequest>().Unsubscribe(x));
                Tokens.Clear();
                
                InfoTokens.ForEach(x => OnlineDataManager.GetRequest<PlayerInfoRequest>().Unsubscribe(x));
                InfoTokens.Clear();
                
                Utf8Strings.ForEach(x =>
                {
                    var ptr = (Utf8String*)x;
                    if (ptr == null) return;
                    
                    ptr->Dtor(true);
                });
                Utf8Strings.Clear();
                break;
        }
    }

    private static void Modify(TaskHelper taskHelper)
    {
        var addon = FriendList;
        if (!IsAddonAndNodesReady(addon)) return;

        var info = InfoProxyFriendList.Instance();
        
        var isAnyUpdate = false;
        for (var i = 0; i < info->EntryCount; i++)
        {
            var data = info->CharDataSpan[i];

            var existedName = SeString.Parse(AtkStage.Instance()->GetStringArrayData(StringArrayType.FriendList)->StringArray[0 + (5 * i)].Value).TextValue;
            if (existedName == LuminaWrapper.GetAddonText(964))
            {
                isAnyUpdate = true;

                var index = i;
                var token = OnlineDataManager.GetRequest<PlayerInfoRequest>().Subscribe(data.ContentId, OnlineDataManager.GetWorldRegion(GameState.HomeWorld),
                                                                                             (name, worldID) =>
                {
                    if (FriendList == null) return;
                    
                    var nameBuilder = new SeStringBuilder();
                    nameBuilder.AddUiForeground($"{name}", 32);
                    
                    var nameString = Utf8String.FromSequence(nameBuilder.Build().Encode());
                    Utf8Strings.Add((nint)nameString);
                    
                    AtkStage.Instance()->GetStringArrayData(StringArrayType.FriendList)->StringArray[0 + (5 * index)] = nameString->StringPtr;
                
                    var worldBuilder = new SeStringBuilder();
                    worldBuilder.AddIcon(BitmapFontIcon.CrossWorld);
                    worldBuilder.Append($"{LuminaWrapper.GetWorldName(worldID)} ({LuminaWrapper.GetWorldDCName(worldID)})");
                    
                    var worldString = Utf8String.FromSequence(worldBuilder.Build().Encode());
                    Utf8Strings.Add((nint)worldString);
                    
                    AtkStage.Instance()->GetStringArrayData(StringArrayType.FriendList)->StringArray[1 + (5 * index)] = worldString->StringPtr;
                    
                    var onlineStatusString = Utf8String.FromString(LuminaWrapper.GetAddonText(1351));
                    Utf8Strings.Add((nint)onlineStatusString);
                    
                    AtkStage.Instance()->GetStringArrayData(StringArrayType.FriendList)->StringArray[3 + (5 * index)] = onlineStatusString->StringPtr;

                    RequestInfoUpdate(taskHelper);
                });
                InfoTokens.Add(token);
            }
            
            if (!ModuleConfig.PlayerInfos.TryGetValue(data.ContentId, out var configInfo)) continue;
            
            if (!string.IsNullOrWhiteSpace(configInfo.Nickname) && existedName != configInfo.Nickname)
            {
                isAnyUpdate = true;
                
                var nicknameBuilder = new SeStringBuilder();
                nicknameBuilder.AddUiForeground($"{configInfo.Nickname}", 37);
                
                var nicknameString = Utf8String.FromSequence(nicknameBuilder.Build().Encode());
                Utf8Strings.Add((nint)nicknameString);
                
                // 名字
                AtkStage.Instance()->GetStringArrayData(StringArrayType.FriendList)->StringArray[0 + (5 * i)] = nicknameString->StringPtr;
            }

            var ptr           = AtkStage.Instance()->GetStringArrayData(StringArrayType.FriendList)->StringArray[3 + (5 * i)];
            var existedRemark = SeString.Parse(ptr.Value).TextValue;
            if (!string.IsNullOrWhiteSpace(configInfo.Remark))
            {
                var remarkString = Utf8String.FromString($"{LuminaWrapper.GetAddonText(13294).TrimEnd(':')}: {configInfo.Remark}" +
                                                         (string.IsNullOrWhiteSpace(configInfo.Nickname)
                                                              ? string.Empty
                                                              : $"\n{LuminaWrapper.GetAddonText(9818)}: {data.NameString}"));
                Utf8Strings.Add((nint)remarkString);
                
                if (remarkString->ExtractText() == existedRemark) continue;
                isAnyUpdate = true;
                
                // 在线状态
                AtkStage.Instance()->GetStringArrayData(StringArrayType.FriendList)->StringArray[3 + (5 * i)] = remarkString->StringPtr;
            }
        }
        
        if (!isAnyUpdate) return;

        RequestInfoUpdate(taskHelper);
    }

    private static void RequestInfoUpdate(TaskHelper taskHelper)
    {
        taskHelper.Abort();
        
        if (FriendList == null) return;
        
        taskHelper.Enqueue(() =>
        {
            if (FriendList == null) return;
            FriendList->OnRequestedUpdate(AtkStage.Instance()->GetNumberArrayData(), AtkStage.Instance()->GetStringArrayData());
        });
        taskHelper.DelayNext(100);
        taskHelper.Enqueue(() =>
        {
            if (FriendList == null) return;
            FriendList->OnRequestedUpdate(AtkStage.Instance()->GetNumberArrayData(), AtkStage.Instance()->GetStringArrayData());
        });
    }

    private static bool MatchesSearch(string filter)
    {
        if (string.IsNullOrWhiteSpace(SearchString)) 
            return true;
        
        if (string.IsNullOrWhiteSpace(filter)) 
            return false;
        
        if (SearchString.StartsWith('^')) 
            return filter.StartsWith(SearchString[1..], StringComparison.InvariantCultureIgnoreCase);
        
        if (SearchString.EndsWith('$')) 
            return filter.EndsWith(SearchString[..^1], StringComparison.InvariantCultureIgnoreCase);
        
        return filter.Contains(SearchString, StringComparison.InvariantCultureIgnoreCase);
    }

    protected static void ApplyFilters(string filter)
    {
        var info = InfoProxyFriendList.Instance();
        if (string.IsNullOrWhiteSpace(filter))
        {
            info->ApplyFilters();
            return;
        }

        var resets = new Dictionary<ulong, uint>();
        var resetFilterGroup = info->FilterGroup;
        info->FilterGroup = InfoProxyCommonList.DisplayGroup.None;
        
        var entryCount = info->GetEntryCount();
        for (var i = 0; i < entryCount; i++)
        {
            var entry = info->GetEntry((uint)i);
            if (entry == null) continue;
            
            var data = info->CharDataSpan[i];
            resets.Add(entry->ContentId, entry->ExtraFlags);

            if (ModuleConfig.IgnoredGroup[(int)entry->Group])
            {
                entry->ExtraFlags = (entry->ExtraFlags & 0xFFFF) | ((uint)(1 & 0xFF) << 16); // 添加隐藏标记
                continue;
            }

            var matchResult = false;
            PlayerInfo configInfo = null;
            
            if (ModuleConfig.SearchName)
            {
                var entryNameString = entry->NameString;
                if (string.IsNullOrEmpty(entry->NameString)) // 搜索会导致非本大区角色被重新刷新为（无法获得角色情报） 需要重新配置
                {
                    var request = OnlineDataManager.GetRequest<PlayerInfoRequest>();
                    var index   = i;
                    var token = request.Subscribe(data.ContentId, OnlineDataManager.GetWorldRegion(GameState.HomeWorld),
                                                  (name, worldID) =>
                                                  {
                                                      var nameBuilder = new SeStringBuilder();
                                                      nameBuilder.AddUiForeground($"{name}", 32);

                                                      var nameString = Utf8String.FromSequence(nameBuilder.Build().Encode());
                                                      Utf8Strings.Add((nint)nameString);

                                                      AtkStage.Instance()->GetStringArrayData(StringArrayType.FriendList)->StringArray[0 + (5 * index)] =
                                                          nameString->StringPtr;

                                                      var worldBuilder = new SeStringBuilder();
                                                      worldBuilder.AddIcon(BitmapFontIcon.CrossWorld);
                                                      worldBuilder.Append($"{LuminaWrapper.GetWorldName(worldID)} ({LuminaWrapper.GetWorldDCName(worldID)})");

                                                      var worldString = Utf8String.FromSequence(worldBuilder.Build().Encode());
                                                      Utf8Strings.Add((nint)worldString);

                                                      AtkStage.Instance()->GetStringArrayData(StringArrayType.FriendList)->StringArray[1 + (5 * index)] =
                                                          worldString->StringPtr;

                                                      var onlineStatusString = Utf8String.FromString(LuminaWrapper.GetAddonText(1351));
                                                      Utf8Strings.Add((nint)onlineStatusString);

                                                      AtkStage.Instance()->GetStringArrayData(StringArrayType.FriendList)->StringArray[3 + (5 * index)] =
                                                          onlineStatusString->StringPtr;

                                                      entryNameString = name;
                                                  });
                    InfoTokens.Add(token);
                }

                matchResult |= MatchesSearch(entryNameString);
            } 
            
            if (ModuleConfig.SearchNickname)
            {
                if (ModuleConfig.PlayerInfos.TryGetValue(data.ContentId, out configInfo))
                    matchResult |= MatchesSearch(configInfo.Nickname);
            }
            
            if (ModuleConfig.SearchRemark)
            {
                if (ModuleConfig.PlayerInfos.TryGetValue(data.ContentId, out configInfo))
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

    protected override void Uninit()
    {
        DService.ContextMenu.OnMenuOpened -= OnContextMenu;
        
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        OnAddon(AddonEvent.PreFinalize, null);
        
        RemarkEditAddon?.Dispose();
        RemarkEditAddon = null;
        
        SearchSettingAddon?.Dispose();
        SearchSettingAddon = null;
        
        if (IsAddonAndNodesReady(FriendList))
            InfoProxyFriendList.Instance()->RequestData();
    }

    private class Config : ModuleConfiguration
    {
        public Dictionary<ulong, PlayerInfo> PlayerInfos = [];
        
        public bool SearchName     = true;
        public bool SearchNickname = true;
        public bool SearchRemark   = true;

        public bool[] IgnoredGroup = new bool[8];
    }

    private class DRFriendlistRemarkEdit(DailyModuleBase instance) : NativeAddon
    {
        public ulong  ContentID { get; private set; }
        public string Name      { get; private set; } = string.Empty;
        public string WorldName { get; private set; } = string.Empty;

        private DailyModuleBase Instance { get; init; } = instance;
        
        private string NicknameInput { get; set; } = string.Empty;
        private string RemarkInput   { get; set; } = string.Empty;

        private TextNode PlayerNameNode;

        private TextNode      NicknameNode;
        private TextInputNode NicknameInputNode;
        
        private TextNode      RemarkNode;
        private TextInputNode RemarkInputNode;

        private TextButtonNode ConfirmButtonNode;
        private TextButtonNode ClearButtonNode;
        private TextButtonNode QuertUsedNameButtonNode;
        
        protected override void OnSetup(AtkUnitBase* addon)
        {
            if (ContentID == 0 || string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(WorldName))
            {
                Close();
                return;
            }

            NicknameInput = ModuleConfig.PlayerInfos.GetValueOrDefault(ContentID, new()).Nickname;
            RemarkInput   = ModuleConfig.PlayerInfos.GetValueOrDefault(ContentID, new()).Remark;
            
            PlayerNameNode = new()
            {
                IsVisible     = true,
                Position      = new(10, 36),
                Size          = new(100, 48),
                SeString      = new SeStringBuilder().Append(Name).AddIcon(BitmapFontIcon.CrossWorld).Append(WorldName).Build(),
                FontSize      = 24,
                AlignmentType = AlignmentType.Left,
            };
            AttachNode(PlayerNameNode);
            
            NicknameNode = new()
            {
                IsVisible     = true,
                Position      = new(10, 80),
                Size          = new(100, 28),
                SeString      = $"{LuminaWrapper.GetAddonText(15207)}",
                FontSize      = 14,
                AlignmentType = AlignmentType.Left,
            };
            AttachNode(NicknameNode);

            NicknameInputNode = new()
            {
                IsVisible     = true,
                Position      = new(10, 108),
                Size          = new(440, 28),
                MaxCharacters = 20,
                ShowLimitText = true,
                OnInputReceived = x =>
                {
                    NicknameInput = x.ExtractText();

                    NicknameInputNode.Tooltip = NicknameInput;
                    if (!string.IsNullOrWhiteSpace(NicknameInput))
                        NicknameInputNode.ShowTooltip();
                    else
                        NicknameInputNode.HideTooltip();
                },
                OnFocused = () =>
                {
                    if (!string.IsNullOrWhiteSpace(NicknameInput))
                        NicknameInputNode.ShowTooltip();
                    else
                        NicknameInputNode.HideTooltip();
                },
                OnUnfocused = () => NicknameInputNode.HideTooltip()
            };
            NicknameInputNode.String = NicknameInput;
            AttachNode(NicknameInputNode);
            
            RemarkNode = new()
            {
                IsVisible     = true,
                Position      = new(10, 140),
                Size          = new(100, 28),
                SeString      = $"{LuminaWrapper.GetAddonText(13294).TrimEnd(':')}",
                FontSize      = 14,
                AlignmentType = AlignmentType.Left,
            };
            AttachNode(RemarkNode);

            RemarkInputNode = new()
            {
                IsVisible     = true,
                Position      = new(10, 168),
                Size          = new(440, 28),
                MaxCharacters = 1024,
                ShowLimitText = true,
                OnInputReceived = x =>
                {
                    RemarkInput = x.ExtractText();

                    RemarkInputNode.Tooltip = RemarkInput;
                    if (!string.IsNullOrWhiteSpace(RemarkInput))
                        RemarkInputNode.ShowTooltip();
                    else
                        RemarkInputNode.HideTooltip();
                },
                OnFocused = () =>
                {
                    if (!string.IsNullOrWhiteSpace(RemarkInput))
                        RemarkInputNode.ShowTooltip();
                    else
                        RemarkInputNode.HideTooltip();
                },
                OnUnfocused = () => RemarkInputNode.HideTooltip()
            };
            RemarkInputNode.String = RemarkInput;
            AttachNode(RemarkInputNode);

            ConfirmButtonNode = new()
            {
                Position  = new(10, 208),
                Size      = new(140, 28),
                IsVisible = true,
                SeString  = GetLoc("Confirm"),
                OnClick = () =>
                {
                    ModuleConfig.PlayerInfos[ContentID] = new()
                    {
                        ContentID = ContentID,
                        Name      = Name,
                        Nickname  = NicknameInput,
                        Remark    = RemarkInput,
                    };
                    ModuleConfig.Save(Instance);
                    
                    InfoProxyFriendList.Instance()->RequestData();
                    Close();
                },
            };
            AttachNode(ConfirmButtonNode);
            
            ClearButtonNode = new()
            {
                Position  = new(160, 208),
                Size      = new(140, 28),
                IsVisible = true,
                SeString  = GetLoc("Clear"),
                OnClick = () =>
                {
                    ModuleConfig.PlayerInfos.Remove(ContentID);
                    InfoProxyFriendList.Instance()->RequestData();
                    Close();
                },
            };
            AttachNode(ClearButtonNode);
            
            QuertUsedNameButtonNode = new()
            {
                Position  = new(310, 208),
                Size      = new(140, 28),
                IsVisible = true,
                SeString  = GetLoc("OptimizedFriendList-ObtainUsedNames"),
                OnClick = () =>
                {
                    var request = OnlineDataManager.GetRequest<PlayerUsedNamesRequest>();
                    Tokens.Add(request.Subscribe(ContentID, OnlineDataManager.GetWorldRegion(GameState.HomeWorld), data =>
                    {
                        if (data.Count == 0)
                            Chat(GetLoc("OptimizedFriendList-FriendUseNamesNotFound", Name));
                        else
                        {
                            Chat($"{GetLoc("OptimizedFriendList-FriendUseNamesFound", Name)}:");
                            var counter = 1;
                            foreach (var nameChange in data)
                            {
                                Chat($"{counter}. {nameChange.ChangedTime}:");
                                Chat($"     {nameChange.BeforeName} -> {nameChange.AfterName}:");

                                counter++;
                            }
                        }
                    }));
                },
            };
            AttachNode(QuertUsedNameButtonNode);
        }

        protected override void OnUpdate(AtkUnitBase* addon)
        {
            if (!IsAddonAndNodesReady(FriendList))
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

    private class DRFriendlistSearchSetting(DailyModuleBase instance) : NativeAddon
    {
        private DailyModuleBase Instance { get; init; } = instance;
        
        protected override void OnSetup(AtkUnitBase* addon)
        {
            var searchTypeTitleNode = new TextNode
            {
                IsVisible = true,
                SeString  = GetLoc("OptimizedFriendList-SearchType"),
                FontSize  = 16,
                TextFlags = TextFlags.AutoAdjustNodeSize,
                Position  = new(10f, 42f)
            };
            AttachNode(searchTypeTitleNode);
            
            var searchTypeLayoutNode = new VerticalListNode
            {
                IsVisible = true,
                Position  = new(20f, searchTypeTitleNode.Position.Y + 28f),
                Alignment = VerticalListAnchor.Top,
            };
            
            var nameCheckboxNode = new CheckboxNode
            {
                Size      = new(80f, 20f),
                IsVisible = true,
                IsChecked = ModuleConfig.SearchName,
                IsEnabled = true,
                SeString  = GetLoc("Name"),
                OnClick = newState =>
                {
                    ModuleConfig.SearchName = newState;
                    ModuleConfig.Save(Instance);

                    ApplyFilters(SearchString);
                },
            };
            searchTypeLayoutNode.Height += searchTypeTitleNode.Height;

            var nicknameCheckboxNode = new CheckboxNode
            {
                Size      = new(80f, 20f),
                IsVisible = true,
                IsChecked = ModuleConfig.SearchNickname,
                IsEnabled = true,
                SeString  = LuminaWrapper.GetAddonText(15207),
                OnClick = newState =>
                {
                    ModuleConfig.SearchNickname = newState;
                    ModuleConfig.Save(Instance);

                    ApplyFilters(SearchString);
                },
            };
            searchTypeLayoutNode.Height += nicknameCheckboxNode.Height;

            var remarkCheckboxNode = new CheckboxNode
            {
                Size      = new(80f, 20f),
                IsVisible = true,
                IsChecked = ModuleConfig.SearchRemark,
                IsEnabled = true,
                SeString  = LuminaWrapper.GetAddonText(13294).TrimEnd(':'),
                OnClick = newState =>
                {
                    ModuleConfig.SearchRemark = newState;
                    ModuleConfig.Save(Instance);

                    ApplyFilters(SearchString);
                },
            };
            searchTypeLayoutNode.Height += remarkCheckboxNode.Height;
            
            searchTypeLayoutNode.AddNode(nameCheckboxNode, nicknameCheckboxNode, remarkCheckboxNode);
            AttachNode(searchTypeLayoutNode);
            
            var searchGroupIgnoreTitleNode = new TextNode
            {
                IsVisible = true,
                SeString  = GetLoc("OptimizedFriendList-SearchIgnoreGroup"),
                FontSize  = 16,
                TextFlags = TextFlags.AutoAdjustNodeSize,
                Position  = new(10f, searchTypeLayoutNode.Position.Y + searchTypeLayoutNode.Height + 28f)
            };
            AttachNode(searchGroupIgnoreTitleNode);

            var searchGroupIgnoreLayoutNode = new VerticalListNode
            {
                IsVisible = true,
                Position  = new(20f, searchGroupIgnoreTitleNode.Position.Y + 28f),
                Alignment = VerticalListAnchor.Top,
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
                    IsChecked = ModuleConfig.IgnoredGroup[i],
                    IsEnabled = true,
                    SeString  = groupFormatText,
                    OnClick = newState =>
                    {
                        ModuleConfig.IgnoredGroup[index] = newState;
                        ModuleConfig.Save(Instance);

                        ApplyFilters(SearchString);
                    },
                };
                
                searchGroupIgnoreLayoutNode.Height += groupCheckboxNode.Height;
                searchGroupIgnoreLayoutNode.AddNode(groupCheckboxNode);
            }
            
            AttachNode(searchGroupIgnoreLayoutNode);
        }

        protected override void OnUpdate(AtkUnitBase* addon)
        {
            if (FriendList == null)
                Close();
        }
    }
    
    private class ModifyInfoMenuItem(TaskHelper TaskHelper) : MenuItemBase
    {
        public override string Name       { get; protected set; } = GetLoc("OptimizedFriendList-ContextMenu-NicknameAndRemark");
        public override string Identifier { get; protected set; } = nameof(OptimizedFriendList);

        public override bool IsDisplay(IMenuOpenedArgs args) =>
            args is { AddonName: "FriendList", Target: MenuTargetDefault target } &&
            target.TargetContentId != 0                                           &&
            !string.IsNullOrWhiteSpace(target.TargetName);

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            if (args.Target is not MenuTargetDefault target) return;

            if (RemarkEditAddon.IsOpen)
            {
                RemarkEditAddon.Close();

                TaskHelper.DelayNext(100);
                TaskHelper.Enqueue(() => !RemarkEditAddon.IsOpen);
                TaskHelper.Enqueue(() => RemarkEditAddon.OpenWithData(target.TargetContentId, target.TargetName, target.TargetHomeWorld.Value.Name.ExtractText()));
            }
            else
                RemarkEditAddon.OpenWithData(target.TargetContentId, target.TargetName, target.TargetHomeWorld.Value.Name.ExtractText());

            ApplyFilters(SearchString);
        }
    }
    
    private class TeleportFriendZoneMenuItem : MenuItemBase
    {
        public override string Name       { get; protected set; } = GetLoc("OptimizedFriendList-ContextMenu-TeleportToFriendZone");
        public override string Identifier { get; protected set; } = nameof(OptimizedFriendList);

        
        private uint AetheryteID;

        protected override void OnClicked(IMenuItemClickedArgs args) => 
            Telepo.Instance()->Teleport(AetheryteID, 0);

        public override bool IsDisplay(IMenuOpenedArgs args) =>
            args is { AddonName : "FriendList", Target: MenuTargetDefault { TargetCharacter: not null } target } &&
            GetAetheryteID(target.TargetCharacter.Location.RowId, out AetheryteID);

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
                _ => zoneID
            };
            if (zoneID == GameState.TerritoryType) return false;
            
            aetheryteID = DService.AetheryteList
                                  .Where(aetheryte => aetheryte.TerritoryId == zoneID)
                                  .Select(aetheryte => aetheryte.AetheryteId)
                                  .FirstOrDefault();

            return aetheryteID > 0;
        }
    }

    private class TeleportFriendWorldMenuItem : MenuItemBase
    {
        public override string Name       { get; protected set; } = GetLoc("OptimizedFriendList-ContextMenu-TeleportToFriendWorld");
        public override string Identifier { get; protected set; } = nameof(OptimizedFriendList);

        
        private uint TargetWorldID;

        public override bool IsDisplay(IMenuOpenedArgs args)
        {
            if ((ModuleManager.IsModuleEnabled("WorldTravelCommand") ?? false) &&
                args is { AddonName: "FriendList", Target: MenuTargetDefault { TargetCharacter.CurrentWorld.RowId: var targetWorldID } } &&
                targetWorldID != GameState.CurrentWorld)
            {
                TargetWorldID = targetWorldID;
                return true;
            }

            return false;
        }

        protected override void OnClicked(IMenuItemClickedArgs args) => 
            ChatHelper.SendMessage($"/pdr worldtravel {LuminaWrapper.GetWorldName(TargetWorldID)}");
    }
    
    public class PlayerInfo
    {
        public ulong  ContentID { get; set; }
        public string Name      { get; set; } = string.Empty;
        public string Nickname  { get; set; } = string.Empty;
        public string Remark    { get; set; } = string.Empty;
    }
}
