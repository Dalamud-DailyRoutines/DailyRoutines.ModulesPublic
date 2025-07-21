using System;
using System.Collections.Generic;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using DailyRoutines.Modules;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public unsafe class FriendListRemarks : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("FriendListRemarksTitle"),
        Description = GetLoc("FriendListRemarksDescription"),
        Category    = ModuleCategories.UIOptimization
    };

    private static readonly ModifyInfoMenuItem   ModifyInfoItem = new();
    
    private static Config ModuleConfig = null!;
    
    private static readonly List<nint> Utf8Strings = [];
    
    private static readonly List<PlayerUsedNamesSubscriptionToken> Tokens = [];

    private static bool   IsNeedToOpen;
    private static ulong  ContentIDToModify;
    private static string NameToModify;

    private static string NicknameInput = string.Empty;
    private static string RemarkInput   = string.Empty;

    protected override void Init()
    {
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
        if (ImGui.SmallButton(GetLoc("FriendListRemarks-ObtainUsedNames")))
        {
            var request = OnlineDataManager.GetRequest<PlayerUsedNamesRequest>();
            Tokens.Add(request.Subscribe(ContentIDToModify, OnlineDataManager.GetWorldRegion(GameState.HomeWorld), data =>
            {
                if (data.Count == 0)
                    Chat(GetLoc("FriendListRemarks-FriendUseNamesNotFound", NameToModify));
                else
                {
                    Chat($"{GetLoc("FriendListRemarks-FriendUseNamesFound", NameToModify)}:");
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
            Modify();
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

    private static void OnAddon(AddonEvent type, AddonArgs? args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
            case AddonEvent.PostRequestedUpdate:
                Modify();
                break;
            case AddonEvent.PreFinalize:
                Utf8Strings.ForEach(x => ((Utf8String*)x)->Dtor(true));
                Utf8Strings.Clear();
                
                var request = OnlineDataManager.GetRequest<PlayerUsedNamesRequest>();
                Tokens.ForEach(x => request.Unsubscribe(x));
                Tokens.Clear();
                break;
        }
    }

    private static void Modify()
    {
        var addon = FriendList;
        if (!IsAddonAndNodesReady(addon)) return;

        var info = InfoProxyFriendList.Instance();

        var isAnyUpdate = false;
        for (var i = 0; i < info->EntryCount; i++)
        {
            var data = info->CharDataSpan[i];
            if (!ModuleConfig.PlayerInfos.TryGetValue(data.ContentId, out var configInfo)) continue;

            var existedName = SeString.Parse(AtkStage.Instance()->GetStringArrayData(StringArrayType.FriendList)->StringArray[0 + (5 * i)]).TextValue;
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
        
        FriendList->OnRequestedUpdate(AtkStage.Instance()->GetNumberArrayData(), AtkStage.Instance()->GetStringArrayData());
        DService.Framework.RunOnTick(
            () =>
            {
                if (!IsAddonAndNodesReady(FriendList)) return;
                FriendList->OnRequestedUpdate(AtkStage.Instance()->GetNumberArrayData(), AtkStage.Instance()->GetStringArrayData());
            }, TimeSpan.FromMilliseconds(100));
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
        public override string Name { get; protected set; } = GetLoc("FriendListRemarks-ContextMenuItemName");

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
