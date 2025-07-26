using System;
using System.Collections.Generic;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedFriendList : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("OptimizedFriendListTitle"),
        Description = GetLoc("OptimizedFriendListDescription"),
        Category    = ModuleCategories.UIOptimization
    };

    private static readonly ModifyInfoMenuItem   ModifyInfoItem = new();
    
    private static Config ModuleConfig = null!;
    
    private static readonly List<nint> Utf8Strings = [];
    
    private static readonly List<PlayerUsedNamesSubscriptionToken> Tokens = [];

    private static readonly List<PlayerInfoSubscriptionToken> InfoTokens = [];

    private static bool   IsNeedToOpen;
    private static ulong  ContentIDToModify;
    private static string NameToModify;

    private static string NicknameInput = string.Empty;
    private static string RemarkInput   = string.Empty;

    protected override void Init()
    {
        TaskHelper ??= new();
        
        Overlay        ??= new(this);
        Overlay.IsOpen =   true;
        Overlay.Flags |= ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDecoration |
                         ImGuiWindowFlags.NoDocking    | ImGuiWindowFlags.NoFocusOnAppearing    | ImGuiWindowFlags.NoNav      | ImGuiWindowFlags.NoResize     |
                         ImGuiWindowFlags.NoInputs;
        
        ModuleConfig =   LoadConfig<Config>() ?? new();
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,           "FriendList", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "FriendList", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize,         "FriendList", OnAddon);
        if (IsAddonAndNodesReady(FriendList)) 
            OnAddon(AddonEvent.PostSetup, null);

        DService.ContextMenu.OnMenuOpened += OnContextMenu;
    }

    protected override void OverlayUI()
    {
        if (IsNeedToOpen)
        {
            IsNeedToOpen = false;

            var isExisted = ModuleConfig.PlayerInfos.TryGetValue(ContentIDToModify, out var info);
            
            NicknameInput = isExisted ? info.Nickname : string.Empty;
            RemarkInput   = isExisted ? info.Remark : string.Empty;
            
            ImGui.OpenPopup("ModifyPopup");
        }

        using var popup = ImRaii.Popup("ModifyPopup");
        if (!popup) return;
        
        ImGui.AlignTextToFramePadding();
        ImGui.Text($"{LuminaWrapper.GetAddonText(9818)}: {NameToModify}");
        
        if (ImGui.IsItemHovered())
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (ImGui.IsItemClicked())
        {
            ImGui.SetClipboardText($"{NameToModify}");
            NotificationSuccess($"{GetLoc("CopiedToClipboard")}: {NameToModify}");
        }
        
        ImGuiOm.TooltipHover($"Content ID: {ContentIDToModify}");
        
        ImGui.SameLine();
        if (ImGui.SmallButton(GetLoc("OptimizedFriendList-ObtainUsedNames")))
        {
            var request = OnlineDataManager.GetRequest<PlayerUsedNamesRequest>();
            Tokens.Add(request.Subscribe(ContentIDToModify, OnlineDataManager.GetWorldRegion(GameState.HomeWorld), data =>
            {
                if (data.Count == 0)
                    Chat(GetLoc("OptimizedFriendList-FriendUseNamesNotFound", NameToModify));
                else
                {
                    Chat($"{GetLoc("OptimizedFriendList-FriendUseNamesFound", NameToModify)}:");
                    var counter = 1;
                    foreach (var nameChange in data)
                    {
                        Chat($"{counter}. {nameChange.ChangedTime}:");
                        Chat($"     {nameChange.BeforeName} -> {nameChange.AfterName}:");

                        counter++;
                    }
                }
            }));
        }

        ImGui.Text($"{LuminaWrapper.GetAddonText(15207)}");
        ImGui.InputText("###NicknameInput", ref NicknameInput, 128);
        
        ImGui.Text($"{LuminaWrapper.GetAddonText(13294).TrimEnd(':')}");
        ImGui.InputText("###RemarkInput", ref RemarkInput, 512);
        ImGui.TextWrapped(RemarkInput);
        
        if (ImGui.Button($"{GetLoc("Confirm")}"))
        {
            ModuleConfig.PlayerInfos[ContentIDToModify] = new()
            {
                ContentID = ContentIDToModify,
                Name      = NameToModify,
                Nickname  = NicknameInput,
                Remark    = RemarkInput,
            };
            ModuleConfig.Save(this);
            
            ImGui.CloseCurrentPopup();
            Modify(TaskHelper);
        }

        using (ImRaii.Disabled(!ModuleConfig.PlayerInfos.ContainsKey(ContentIDToModify)))
        {
            ImGui.SameLine();
            if (ImGui.Button($"{GetLoc("Delete")}"))
            {
                ModuleConfig.PlayerInfos.Remove(ContentIDToModify);
                ModuleConfig.Save(this);
                
                ImGui.CloseCurrentPopup();
                InfoProxyFriendList.Instance()->RequestData();
            }
        }
    }

    private static void OnContextMenu(IMenuOpenedArgs args)
    {
        if (ModifyInfoItem.IsDisplay(args))
            args.AddMenuItem(ModifyInfoItem.Get());
    }

    private void OnAddon(AddonEvent type, AddonArgs? args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
            case AddonEvent.PostRequestedUpdate:
                Modify(TaskHelper);
                break;
            case AddonEvent.PreFinalize:
                Tokens.ForEach(x => OnlineDataManager.GetRequest<PlayerUsedNamesRequest>().Unsubscribe(x));
                Tokens.Clear();
                
                InfoTokens.ForEach(x => OnlineDataManager.GetRequest<PlayerInfoRequest>().Unsubscribe(x));
                InfoTokens.Clear();
                
                Utf8Strings.ForEach(x => ((Utf8String*)x)->Dtor(true));
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

            var existedName = SeString.Parse(AtkStage.Instance()->GetStringArrayData(StringArrayType.FriendList)->StringArray[0 + (5 * i)]).TextValue;
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

                    taskHelper.Abort();
                    taskHelper.Enqueue(() =>
                    {
                        if (FriendList == null) return;
                        FriendList->OnRequestedUpdate(AtkStage.Instance()->GetNumberArrayData(), AtkStage.Instance()->GetStringArrayData());
                    });
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

            var existedRemark = SeString.Parse(AtkStage.Instance()->GetStringArrayData(StringArrayType.FriendList)->StringArray[3 + (5 * i)]).TextValue;
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

        taskHelper.Abort();
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

    protected override void Uninit()
    {
        DService.ContextMenu.OnMenuOpened -= OnContextMenu;
        DService.AddonLifecycle.UnregisterListener(OnAddon);

        OnAddon(AddonEvent.PreFinalize, null);
        base.Uninit();

        if (IsAddonAndNodesReady(FriendList))
            InfoProxyFriendList.Instance()->RequestData();

        IsNeedToOpen = false;
    }

    private class Config : ModuleConfiguration
    {
        public Dictionary<ulong, PlayerInfo> PlayerInfos = [];
    }
    
    private class ModifyInfoMenuItem : MenuItemBase
    {
        public override string Name { get; protected set; } = GetLoc("OptimizedFriendList-ContextMenuItemName");

        public override bool IsDisplay(IMenuOpenedArgs args) =>
            args is { AddonName: "FriendList", Target: MenuTargetDefault target } &&
            target.TargetContentId != 0                                           &&
            !string.IsNullOrWhiteSpace(target.TargetName);

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            if (args.Target is not MenuTargetDefault target) return;
            
            ContentIDToModify = target.TargetContentId;
            NameToModify      = target.TargetName;
            IsNeedToOpen      = true;
        }
    }
    
    public class PlayerInfo
    {
        public ulong  ContentID { get; set; }
        public string Name      { get; set; } = string.Empty;
        public string Nickname  { get; set; } = string.Empty;
        public string Remark    { get; set; } = string.Empty;
    }
}
