using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class MarkerInPartyList : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("MarkerInPartyListTitle"),
        Description = Lang.Get("MarkerInPartyListDescription"),
        Category    = ModuleCategory.Combat,
        Author      = ["status102"]
    };

    private static readonly CompSig                     LocalMarkingSig = new("E8 ?? ?? ?? ?? 4C 8B C5 8B D7 48 8B CB E8");
    private delegate        void                        LocalMarkingDelegate(void* manager, uint markingType, GameObjectId objectID, uint entityID);
    private                 Hook<LocalMarkingDelegate>? LocalMarkingHook;

    private Config? config;

    private readonly (short X, short Y)   basePosition = (41, 35);
    private readonly Dictionary<int, int> markedObject = new(8); // markID, memberIndex
    private readonly List<IconImageNode>  nodeList     = new(8);

    private bool isNeedClear;

    protected override void Init()
    {
        config =   Config.Load(this) ?? new();
        TaskHelper   ??= new();

        LocalMarkingHook = LocalMarkingSig.GetHook<LocalMarkingDelegate>(LocalMarkingDetour);
        LocalMarkingHook.Enable();

        DService.Instance().ClientState.TerritoryChanged += ResetMarkedObject;

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "_PartyList", OnAddonPartyList);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "_PartyList", OnAddonPartyList);
    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonPartyList);

        DService.Instance().ClientState.TerritoryChanged -= ResetMarkedObject;

        ResetPartyMemberList();
        ReleaseImageNodes();
    }

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(200f * GlobalUIScale);
        var iconOffset = config.IconOffset;
        ImGui.InputFloat2(Lang.Get("MarkerInPartyList-IconOffset"), ref iconOffset, format: "%.1f");

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.IconOffset = iconOffset;
            config.Save(this);
            RefreshNodeStatus();
        }

        ImGui.SetNextItemWidth(200f * GlobalUIScale);
        ImGui.InputInt(Lang.Get("MarkerInPartyList-IconScale"), ref config.Size);

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.Save(this);
            RefreshNodeStatus();
        }

        if (ImGui.Checkbox(Lang.Get("MarkerInPartyList-HidePartyListIndexNumber"), ref config.HidePartyListIndexNumber))
        {
            config.Save(this);

            var hide = config.HidePartyListIndexNumber;

            foreach (var (node, i) in nodeList.Zip(Enumerable.Range(10, 8)))
            {
                var component = PartyList->GetNodeById((uint)i);
                if (component is null || !component->IsVisible())
                    continue;
                hide = hide && node.IsVisible;
            }

            ModifyPartyMemberNumber(!hide);
        }
    }

    private void ResetMarkedObject(uint u)
    {
        foreach (var i in Enumerable.Range(0, 8))
            HideImageNode(i);
        markedObject.Clear();
        ResetPartyMemberList();
    }

    private void ResetPartyMemberList()
    {
        if (!PartyList->IsAddonAndNodesReady()) return;
        ModifyPartyMemberNumber(true);
    }

    private void ModifyPartyMemberNumber(bool visible)
    {
        if (!PartyList->IsAddonAndNodesReady() || !config.HidePartyListIndexNumber && !visible)
            return;

        foreach (var id in Enumerable.Range(10, 8).ToList())
        {
            var member = PartyList->GetNodeById((uint)id);
            if (member is null || member->GetComponent() is null)
                continue;

            if (!member->IsVisible())
                continue;

            var textNode = member->GetComponent()->UldManager.SearchNodeById(16);
            if (textNode != null && textNode->IsVisible() != visible)
                textNode->ToggleVisibility(visible);
        }
    }

    private void ProcessMarkIconSetted(uint markIndex, uint entityID)
    {
        if (AgentHUD.Instance() is null || InfoProxyCrossRealm.Instance() is null)
            return;

        int index;
        var mark = (int)(markIndex + 1);

        if (mark <= 0 || mark > LuminaGetter.Get<Marker>().Count || !LuminaGetter.TryGetRow((uint)mark, out Marker markerRow))
        {
            if (FindMember(entityID, out index))
                RemoveMemberMark(index);

            return;
        }

        if (entityID is 0xE000_0000 or 0xE00_0000)
        {
            RemoveMark(markerRow.Icon);
            return;
        }

        if (!FindMember(entityID, out index))
            RemoveMark(markerRow.Icon);
        else if (markedObject.TryGetValue(markerRow.Icon, out var outValue) && outValue == index)
        {
            // 对同一个成员重复标记
        }
        else
        {
            RemoveMemberMark(index);
            AddMemberMark(index, markerRow.Icon);
        }
    }
    
    #region ImageNode

    private void ReleaseImageNodes()
    {
        if (!PartyList->IsAddonAndNodesReady()) return;

        foreach (var item in nodeList)
            item?.Dispose();

        nodeList.Clear();
    }

    private void ShowImageNode(int i, int iconID)
    {
        if (i is < 0 or > 7 || PartyList is null || nodeList.Count <= i)
            return;

        var node = nodeList[i];
        if (node is null) return;

        node.LoadIcon((uint)iconID);
        var component = PartyList->GetNodeById((uint)(10 + i));
        node.Position    = new(component->X + basePosition.X + config.IconOffset.X, component->Y + basePosition.Y + config.IconOffset.Y);
        node.TextureSize = node.ActualTextureSize;
        node.Size        = new(config.Size);
        node.IsVisible   = true;

        ModifyPartyMemberNumber(false);
    }

    private void HideImageNode(int i)
    {
        if (i is < 0 or > 7 || nodeList.Count <= i) return;

        var node = nodeList[i];
        if (node == null) return;

        node.IsVisible = false;
    }

    private void RefreshNodeStatus()
    {
        var addon = PartyList;
        if (!addon->IsAddonAndNodesReady())
            return;

        foreach (var (node, i) in nodeList.Zip(Enumerable.Range(10, 8)))
        {
            var component = PartyList->GetNodeById((uint)i);
            if (component is null || !component->IsVisible())
                continue;

            node.Position    = new(component->X + basePosition.X + config.IconOffset.X, component->Y + basePosition.Y + config.IconOffset.Y);
            node.TextureSize = node.ActualTextureSize;
            node.Size        = new(config.Size);
            node.IsVisible   = true;
        }
    }

    #endregion

    #region 工具

    private void AddMemberMark(int memberIndex, int markID)
    {
        markedObject[markID] = memberIndex;
        ShowImageNode(memberIndex, markID);
        isNeedClear = false;
    }

    private void RemoveMemberMark(int memberIndex)
    {
        if (markedObject.ContainsValue(memberIndex))
        {
            markedObject.Remove(markedObject.First(x => x.Value == memberIndex).Key);
            HideImageNode(memberIndex);
        }

        if (markedObject.Count == 0)
            isNeedClear = true;
    }

    private void RemoveMark(int markID)
    {
        if (markedObject.Remove(markID, out var outValue))
            HideImageNode(outValue);
        if (markedObject.Count == 0)
            isNeedClear = true;
    }

    private static bool FindMember(uint entityID, out int index)
    {
        var pAgentHUD = AgentHUD.Instance();

        for (var i = 0; i < pAgentHUD->PartyMemberCount; ++i)
        {
            var charData = pAgentHUD->PartyMembers[i];

            if (entityID == charData.EntityId)
            {
                index = i;
                return true;
            }
        }

        if (InfoProxyCrossRealm.Instance()->IsCrossRealm)
        {
            var myGroup      = InfoProxyCrossRealm.GetMemberByEntityId(LocalPlayerState.EntityID);
            var pGroupMember = InfoProxyCrossRealm.GetMemberByEntityId(entityID);

            if (myGroup is not null && pGroupMember is not null && pGroupMember->GroupIndex == myGroup->GroupIndex)
            {
                index = pGroupMember->MemberIndex;
                return true;
            }

        }

        index = -1;
        return false;
    }

    #endregion

    #region 事件

    private void LocalMarkingDetour(void* manager, uint markingType, GameObjectId objectID, uint entityID)
    {
        // 自身标记会触发两回，第一次a4: E000_0000, 第二次a4: 自身GameObjectId
        // 队友标记只会触发一回，a4: 队友GameObjectId
        // 鲶鱼精local a4: 0
        // if (a4 != (nint?)DService.Instance().ObjectTable.LocalPlayer?.GameObjectId)

        TaskHelper.Enqueue(() => ProcessMarkIconSetted(markingType, (uint)objectID));
        LocalMarkingHook!.Original(manager, markingType, objectID, entityID);
    }

    private void OnAddonPartyList(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PreFinalize:
                ReleaseImageNodes();
                break;

            case AddonEvent.PostDraw:
                if (!PartyList->IsAddonAndNodesReady()) return;

                if (isNeedClear && markedObject.Count is 0 && UIModule.IsScreenReady())
                {
                    ResetPartyMemberList();
                    isNeedClear = false;

                    return;
                }

                // 加入
                if (nodeList.Count == 0)
                {
                    foreach (var _ in Enumerable.Range(10, 8))
                    {
                        var imageNode = new IconImageNode
                        {
                            IconId    = DEFAULT_ICON_ID,
                            NodeFlags = NodeFlags.Fill,
                            DrawFlags = DrawFlags.None,
                            WrapMode  = WrapMode.Stretch
                        };
                        imageNode.Priority = 5;

                        nodeList.Add(imageNode);
                        imageNode.AttachNode(PartyList);
                    }

                    if (MarkingController.Instance() is null)
                        return;

                    var markers = MarkingController.Instance()->Markers;

                    for (var i = 0; i < markers.Length; i++)
                    {
                        var gameObjectID = markers[i].ObjectId;
                        if (gameObjectID is 0 or 0xE0000000)
                            continue;

                        var index = (uint)i;
                        TaskHelper.Enqueue(() => ProcessMarkIconSetted(index, gameObjectID));
                    }
                }

                break;
        }
    }

    #endregion

    private class Config : ModuleConfig
    {
        public bool    HidePartyListIndexNumber = true;
        public Vector2 IconOffset               = new(0, 0);
        public int     Size                     = 27;
    }

    #region 常量

    private const int DEFAULT_ICON_ID = 61201;

    #endregion
}
