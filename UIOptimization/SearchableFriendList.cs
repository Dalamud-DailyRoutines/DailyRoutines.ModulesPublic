using System;
using System.Collections.Generic;
using System.Numerics;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using KamiToolKit.Nodes;

namespace DailyRoutines.ModulesPublic;

public unsafe class SearchableFriendList : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("SearchableFriendListTitle"),
        Description = GetLoc("SearchableFriendListDescription"),
        Category = ModuleCategories.UIOptimization,
        Author = ["Middo"]
    };

    private static TextInputNode? SearchNode;

    private string searchString = string.Empty;

    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreSetup, "FriendList", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, "FriendList", OnAddon);
    }

    protected override void ConfigUI()
    {

        if (ImGui.Checkbox(GetLoc("SearchableFriendList-EnableIgnoredGroup"), ref ModuleConfig.IgnoreSpecificGroup))
            SaveConfig(ModuleConfig);
        
        if (ModuleConfig.IgnoreSpecificGroup)
        {
            using (ImRaii.PushId("IgnoredGroup"))
            using (ImRaii.PushIndent())
            {
                if (ImGui.Checkbox(GetLoc("SearchableFriendList-IgnoredGroupNone"), ref ModuleConfig.IgnoredGroup[0]))
                    SaveConfig(ModuleConfig);
                ImGui.SameLine(150);
                if (ImGui.Checkbox($"{GetLoc("SearchableFriendList-IgnoredGroup")}1", ref ModuleConfig.IgnoredGroup[1]))
                    SaveConfig(ModuleConfig);
                
                if (ImGui.Checkbox($"{GetLoc("SearchableFriendList-IgnoredGroup")}2", ref ModuleConfig.IgnoredGroup[2]))
                    SaveConfig(ModuleConfig);
                ImGui.SameLine(150);
                if (ImGui.Checkbox($"{GetLoc("SearchableFriendList-IgnoredGroup")}3", ref ModuleConfig.IgnoredGroup[3]))
                    SaveConfig(ModuleConfig);

                if (ImGui.Checkbox($"{GetLoc("SearchableFriendList-IgnoredGroup")}4", ref ModuleConfig.IgnoredGroup[4]))
                    SaveConfig(ModuleConfig);
                ImGui.SameLine(150);
                if (ImGui.Checkbox($"{GetLoc("SearchableFriendList-IgnoredGroup")}5", ref ModuleConfig.IgnoredGroup[5]))
                    SaveConfig(ModuleConfig);

                if (ImGui.Checkbox($"{GetLoc("SearchableFriendList-IgnoredGroup")}6", ref ModuleConfig.IgnoredGroup[6]))
                    SaveConfig(ModuleConfig);
                ImGui.SameLine(150);
                if (ImGui.Checkbox($"{GetLoc("SearchableFriendList-IgnoredGroup")}7", ref ModuleConfig.IgnoredGroup[7]))
                    SaveConfig(ModuleConfig);
            }
        }
        

    }

    protected void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PreSetup:
                if (FriendList == null)
                    return;
                if (SearchNode == null)
                    SearchNode = CreateSearchNode();
                Service.AddonController.AttachNode(SearchNode, FriendList->GetNodeById(20));//如因切换好友列表选项卡导致的好友页面重载 只需重新挂载节点
                break;
            case AddonEvent.PreRequestedUpdate :
                ApplyFilters(searchString);
                break;
        }
    }

    private bool MatchesSearch(string filter)
    {
        if (string.IsNullOrWhiteSpace(searchString)) return true;
        if (string.IsNullOrWhiteSpace(filter)) return false;
        if (searchString.StartsWith('^')) return filter.StartsWith(searchString[1..], StringComparison.InvariantCultureIgnoreCase);
        if (searchString.EndsWith('$')) return filter.EndsWith(searchString[..^1], StringComparison.InvariantCultureIgnoreCase);
        return filter.Contains(searchString, StringComparison.InvariantCultureIgnoreCase);
    }

    private TextInputNode CreateSearchNode()
    {
        var node = new TextInputNode
        {
            IsVisible = true,
            Position = new(10f, 425f),
            Size = new(200.0f, 35f),
            MaxCharacters = 20,
            ShowLimitText = true,
            OnInputReceived = x =>
            {
                searchString = x.TextValue;
                ApplyFilters(searchString);
            },
            OnInputComplete = x =>
            {
                searchString = x.TextValue;
                ApplyFilters(searchString);
            },
        };

        node.CursorNode.ScaleY = 1.4f;
        node.CurrentTextNode.FontSize = 14;
        node.CurrentTextNode.Position += new Vector2(0f, 3f);
        return node;
    }

    protected void ApplyFilters (string filter)
    {
        var friendList = InfoProxyFriendList.Instance();
        if (string.IsNullOrWhiteSpace(filter))
        {
            friendList->ApplyFilters();
            return;
        }
        
        var resets = new Dictionary<ulong, uint>();
        var resetFilterGroup = friendList->FilterGroup;
        friendList->FilterGroup = InfoProxyCommonList.DisplayGroup.None;
        var entryCount = friendList->GetEntryCount();

        for (var i = 0U; i < entryCount; i++)
        {
            var entry = friendList->GetEntry(i);
            if (entry == null) continue;
            resets.Add(entry->ContentId, entry->ExtraFlags);

            if (ModuleConfig.IgnoreSpecificGroup && ModuleConfig.IgnoredGroup[(int)entry->Group])
            {
                entry->ExtraFlags = (entry->ExtraFlags & 0xFFFF) | ((uint)(1 & 0xFF) << 16);//添加隐藏标记
                continue;
            }

            if ((resetFilterGroup == InfoProxyCommonList.DisplayGroup.All || entry->Group == resetFilterGroup) && MatchesSearch(entry->NameString))
                entry->ExtraFlags &= 0xFFFF;//去除隐藏标记
            else
                entry->ExtraFlags = (entry->ExtraFlags & 0xFFFF) | ((uint)(1 & 0xFF) << 16);
        }
        friendList->ApplyFilters();

        friendList->FilterGroup = resetFilterGroup;
        foreach (var pair in resets)
        {
            var entry = friendList->GetEntryByContentId(pair.Key);
            entry->ExtraFlags = pair.Value;
        }
    }

    protected override void Uninit()
    {
        if (SearchNode != null)
        {
            Service.AddonController.DetachNode(SearchNode);
            SearchNode = null;
        }

        DService.AddonLifecycle.UnregisterListener(OnAddon);
    }

    public class Config : ModuleConfiguration
    {
        public bool IgnoreSpecificGroup = false;
        public bool[] IgnoredGroup = new bool[8];
    }
}
