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

public unsafe class AutoDisplayMarkerInPartyList : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoDisplayMarkerInPartyListTitle"),
        Description = Lang.Get("AutoDisplayMarkerInPartyListDescription"),
        Category    = ModuleCategory.Combat,
        Author      = ["status102"]
    };

    private static readonly CompSig UpdateMarkerLocalSig = new("E8 ?? ?? ?? ?? 4C 8B C5 8B D7 48 8B CB E8");
    private delegate void UpdateMarkerLocalDelegate
    (
        MarkingController* controller,
        uint               marker,
        GameObjectId       objectID,
        uint               entityID
    );
    private Hook<UpdateMarkerLocalDelegate>? UpdateMarkerLocalHook;

    private Config config = null!;

    private readonly (short X, short Y)  basePosition  = (41, 35);
    private readonly Dictionary<int, int> markedObjects = new(8); // markID → memberIndex
    private readonly List<IconImageNode>  nodes         = new(8);

    protected override void Init()
    {
        config     = Config.Load(this) ?? new();
        TaskHelper = new();

        UpdateMarkerLocalHook = UpdateMarkerLocalSig.GetHook<UpdateMarkerLocalDelegate>(UpdateMarkerLocalDetour);
        UpdateMarkerLocalHook.Enable();

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "_PartyList", OnPartyList);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "_PartyList", OnPartyList);
    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnPartyList);
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;

        ModifyPartyMemberNumber(true);

        foreach (var node in nodes)
            node.Dispose();
        nodes.Clear();
    }

    protected override void ConfigUI()
    {
        var iconOffset = config.IconOffset;
        ImGui.SetNextItemWidth(200f * GlobalUIScale);
        ImGui.InputFloat2(Lang.Get("IconOffset"), ref iconOffset, format: "%.1f");
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.IconOffset = iconOffset;
            config.Save(this);
            foreach (var (markID, memberIndex) in markedObjects)
                SetNode(memberIndex, markID);
        }

        ImGui.SetNextItemWidth(200f * GlobalUIScale);
        ImGui.InputInt(Lang.Get("IconScale"), ref config.Size);

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.Save(this);
            foreach (var (markID, memberIndex) in markedObjects)
                SetNode(memberIndex, markID);
        }

        if (ImGui.Checkbox(Lang.Get("AutoDisplayMarkerInPartyList-HidePartyListIndexNumber"), ref config.HidePartyListIndexNumber))
        {
            config.Save(this);

            var hide = config.HidePartyListIndexNumber;

            for (var i = 0; i < 8; i++)
            {
                var component = PartyList->GetNodeById((uint)(10 + i));
                if (component is null || !component->IsVisible())
                    continue;
                hide = hide && nodes[i].IsVisible;
            }

            ModifyPartyMemberNumber(!hide);
        }
    }

    private void OnZoneChanged(uint zone)
    {
        for (var i = 0; i < 8; i++)
            SetNode(i, null);
        markedObjects.Clear();
        ModifyPartyMemberNumber(true);
    }
    
    private void UpdateMarkerLocalDetour
    (
        MarkingController* controller,
        uint               marker,
        GameObjectId       objectID,
        uint               entityID
    )
    {
        TaskHelper.Enqueue(() => ProcessMarkIconSetted(marker, (uint)objectID));
        UpdateMarkerLocalHook.Original(controller, marker, objectID, entityID);
    }

    private void OnPartyList(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PreFinalize:
                nodes.Clear();
                break;

            case AddonEvent.PostDraw:
                if (!PartyList->IsAddonAndNodesReady()) return;

                if (nodes.Count == 0)
                {
                    for (var i = 0; i < 8; i++)
                    {
                        var imageNode = new IconImageNode
                        {
                            IconId    = 61201,
                            NodeFlags = NodeFlags.Fill,
                            DrawFlags = DrawFlags.None,
                            WrapMode  = WrapMode.Stretch
                        };
                        imageNode.Priority = 5;

                        nodes.Add(imageNode);
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

    private void ModifyPartyMemberNumber(bool visible)
    {
        if (!PartyList->IsAddonAndNodesReady() || (!config.HidePartyListIndexNumber && !visible))
            return;

        for (var i = 10; i <= 17; i++)
        {
            var member = PartyList->GetNodeById((uint)i);
            if (member is null || member->GetComponent() is null || !member->IsVisible())
                continue;

            var textNode = member->GetComponent()->UldManager.SearchNodeById(16);
            if (textNode != null && textNode->IsVisible() != visible)
                textNode->ToggleVisibility(visible);
        }
    }

    private void ProcessMarkIconSetted(uint markIndex, uint entityID)
    {
        if (AgentHUD.Instance() == null || InfoProxyCrossRealm.Instance() == null || !PartyList->IsAddonAndNodesReady())
            return;

        var mark = (int)(markIndex + 1);
        if (mark <= 0 || mark > LuminaGetter.Get<Marker>().Count || !LuminaGetter.TryGetRow((uint)mark, out Marker markerRow))
        {
            if (FindMember(entityID, out var memberIndex))
                ClearMemberMark(memberIndex);
            return;
        }

        if (entityID is 0xE000_0000 or 0xE00_0000)
        {
            ClearMark(markerRow.Icon);
            return;
        }

        if (!FindMember(entityID, out var index))
        {
            ClearMark(markerRow.Icon);
            return;
        }

        // 同成员重复标记: 忽略
        if (markedObjects.TryGetValue(markerRow.Icon, out var existingIndex) && existingIndex == index)
            return;

        ClearMemberMark(index);
        markedObjects[markerRow.Icon] = index;
        SetNode(index, markerRow.Icon);
        ModifyPartyMemberNumber(false);
    }

    private void ClearMemberMark(int memberIndex)
    {
        foreach (var kv in markedObjects)
        {
            if (kv.Value != memberIndex) continue;
            markedObjects.Remove(kv.Key);
            SetNode(memberIndex, null);
            break;
        }
        if (markedObjects.Count == 0)
            ModifyPartyMemberNumber(true);
    }

    private void ClearMark(int markID)
    {
        if (markedObjects.Remove(markID, out var memberIndex))
            SetNode(memberIndex, null);
        if (markedObjects.Count == 0)
            ModifyPartyMemberNumber(true);
    }

    private void SetNode(int i, int? iconID)
    {
        if (i < 0 || i >= nodes.Count || !PartyList->IsAddonAndNodesReady())
            return;

        var node = nodes[i];
        node.IsVisible = iconID.HasValue;
        if (iconID is not { } id)
            return;

        node.LoadIcon((uint)id);
        var component = PartyList->GetNodeById((uint)(10 + i));
        node.Position    = new(component->X + basePosition.X + config.IconOffset.X, component->Y + basePosition.Y + config.IconOffset.Y);
        node.TextureSize = node.ActualTextureSize;
        node.Size        = new(config.Size);
    }

    private static bool FindMember(uint entityID, out int index)
    {
        var pAgentHUD = AgentHUD.Instance();

        for (var i = 0; i < pAgentHUD->PartyMemberCount; ++i)
        {
            if (entityID == pAgentHUD->PartyMembers[i].EntityId)
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

    private class Config : ModuleConfig
    {
        public bool    HidePartyListIndexNumber = true;
        public Vector2 IconOffset               = new(0, 0);
        public int     Size                     = 27;
    }
}
