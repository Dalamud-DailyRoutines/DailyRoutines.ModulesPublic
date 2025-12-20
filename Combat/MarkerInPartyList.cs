using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace DailyRoutines.ModulesPublic;

public unsafe class MarkerInPartyList : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("MarkerInPartyListTitle"),
        Description = GetLoc("MarkerInPartyListDescription"),
        Category    = ModuleCategories.Combat,
        Author      = ["status102"]
    };

    private const int DefaultIconID = 61201;
    private static readonly (short X, short Y) BasePosition = (41, 35);
    private static ExcelSheet<Marker>? MarkerSheet;

    private static readonly CompSig LocalMarkingSig = new("E8 ?? ?? ?? ?? 4C 8B C5 8B D7 48 8B CB E8");
    public static Hook<LocalMarkingFunc>? LocalMarkingHook;
    public delegate void LocalMarkingFunc(nint manager, uint markingType, nint objectID, nint a4);

    private static          Config?              ModuleConfig;
    private static readonly Dictionary<int, int> MarkedObject = new(8); // markId, memberIndex
    private static          bool                 IsBuilt, NeedClear;
    private static readonly List<IconImageNode> NodeList = new(8);

    private static readonly Lock Lock = new();

    protected override void Init()
    {
        IsBuilt = false;
        ModuleConfig = LoadConfig<Config>() ?? new();

        TaskHelper ??= new();
        MarkerSheet ??= DService.Data.GetExcelSheet<Marker>();

        LocalMarkingHook = DService.Hook.HookFromSignature<LocalMarkingFunc>(LocalMarkingSig.Get(), DetourLocalMarkingFunc);
        LocalMarkingHook.Enable();

        DService.ClientState.TerritoryChanged += ResetMarkedObject;
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "_PartyList", PartyListDrawHandle);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "_PartyList", PartyListFinalizeHandle);
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(AddonEvent.PostDraw, PartyListDrawHandle);
        DService.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, PartyListFinalizeHandle);
        DService.ClientState.TerritoryChanged -= ResetMarkedObject;

        ResetPartyMemberList();
        ReleaseImageNodes();
    }

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        Vector2 iconOffset = ModuleConfig.IconOffset;
        ImGui.InputFloat2(Lang.Get("MarkerInPartyList-IconOffset"), ref iconOffset, format: "%.1f");
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            ModuleConfig.IconOffset = iconOffset;
            SaveConfig(ModuleConfig);
            RefreshNodeStatus();
        }

        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        ImGui.InputInt(Lang.Get("MarkerInPartyList-IconScale"), ref ModuleConfig.Size);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            SaveConfig(ModuleConfig);
            RefreshNodeStatus();
        }

        if (ImGui.Checkbox(Lang.Get("MarkerInPartyList-HidePartyListIndexNumber"), ref ModuleConfig.HidePartyListIndexNumber))
        {
            SaveConfig(ModuleConfig);

            var hide = ModuleConfig.HidePartyListIndexNumber;
            foreach (var (node, i) in NodeList.Zip(Enumerable.Range(10, 8)))
            {
                var component = PartyList->GetNodeById((uint)i);
                if (component is null || !component->IsVisible())
                    continue;
                hide = hide && node.IsVisible;
            }

            ModifyPartyMemberNumber(PartyList, !hide);
        }
    }

    private static void ResetMarkedObject(ushort obj)
    {
        foreach (var i in Enumerable.Range(0, 8))
            HideImageNode(i);
        MarkedObject.Clear();
        ResetPartyMemberList();
    }

    private static void ResetPartyMemberList(AtkUnitBase* partylist = null)
    {
        if (partylist is null)
            partylist = PartyList;
        if (partylist is not null && partylist->UldManager.LoadedState is AtkLoadState.Loaded)
            ModifyPartyMemberNumber(partylist, true);
    }

    /// <summary>
    /// 修改队伍成员编号显示状态
    /// </summary>
    /// <param name="pPartyList"></param>
    /// <param name="visible"></param>
    private static void ModifyPartyMemberNumber(AtkUnitBase* pPartyList, bool visible)
    {
        if (pPartyList is null || ModuleConfig == null || (!ModuleConfig.HidePartyListIndexNumber && !visible))
            return;

        foreach (var id in Enumerable.Range(10, 8).ToList())
        {
            var member = pPartyList->GetNodeById((uint)id);
            if (member is null || member->GetComponent() is null)
                continue;

            if (!member->IsVisible())
                continue;

            var textNode = member->GetComponent()->UldManager.SearchNodeById(16);
            if (textNode != null && textNode->IsVisible() != visible)
                textNode->ToggleVisibility(visible);
        }
    }

    #region ImageNode

    private static IconImageNode GenerateImageNode()
    {
        var imageNode = new IconImageNode()
        {
            IconId = DefaultIconID,
            NodeFlags = NodeFlags.Fill,
            DrawFlags = DrawFlags.None,
            WrapMode = WrapMode.Stretch,
        };

        imageNode.Priority = 5;
        return imageNode;
    }

    private static void InitImageNodes()
    {
        var addon = PartyList;
        if (addon == null)
            return;

        lock (Lock)
        {
            if (IsBuilt)
                return;

            foreach (var _ in Enumerable.Range(10, 8))
            {
                var imageNode = GenerateImageNode();
                if (imageNode is null)
                    continue;

                NodeList.Add(imageNode);
                imageNode.AttachNode(PartyList);
            }
            IsBuilt = true;
        }
    }

    private void InitMarkedObject()
    {
        if (MarkingController.Instance() is null)
            return;

        var markers = MarkingController.Instance()->Markers;
        for (var i = 0; i < markers.Length; i++)
        {
            var gameObjectID = markers[i].ObjectId;
            if (gameObjectID is 0 or 0xE0000000)
                continue;
            var index = (uint)i;
            TaskHelper.Insert(() => ProcMarkIconSetted(index, gameObjectID));
        }
    }

    private static void ReleaseImageNodes()
    {
        lock (Lock)
        {
            if (!IsBuilt)
                return;

            if (PartyList is null || PartyList->UldManager.LoadedState is not AtkLoadState.Loaded)
            {
                Error("Failed to get partylist");
                return;
            }

            foreach (var item in NodeList)
                item.DetachNode();

            NodeList.Clear();
            IsBuilt = false;
        }
    }

    private static void ShowImageNode(int i, int iconID)
    {
        if (i is < 0 or > 7 || PartyList is null || NodeList.Count <= i)
            return;

        var node = NodeList[i];
        if (node is null)
            return;

        node.LoadIcon((uint)iconID);
        var component = PartyList->GetNodeById((uint)(10 + i));
        node.Position = new(component->X + BasePosition.X + ModuleConfig.IconOffset.X, component->Y + BasePosition.Y + ModuleConfig.IconOffset.Y);
        node.TextureSize = node.ActualTextureSize;
        node.Size = new(ModuleConfig.Size);
        node.IsVisible = true;

        ModifyPartyMemberNumber(PartyList, false);
    }

    private static void HideImageNode(int i)
    {
        if (i is < 0 or > 7 || NodeList.Count <= i)
            return;
        var node = NodeList[i];
        if (node is null)
            return;
        node.IsVisible = false;
    }

    private static void RefreshNodeStatus()
    {
        var addon = PartyList;
        if (!IsAddonAndNodesReady(addon))
            return;

        foreach (var (node, i) in NodeList.Zip(Enumerable.Range(10, 8)))
        {
            var component = PartyList->GetNodeById((uint)i);
            if (component is null || !component->IsVisible())
                continue;
            node.Position = new(component->X + BasePosition.X + ModuleConfig.IconOffset.X, component->Y + BasePosition.Y + ModuleConfig.IconOffset.Y);
            node.TextureSize = node.ActualTextureSize;
            node.Size = new(ModuleConfig.Size);
            node.IsVisible = true;
        }
    }

    #endregion

    #region Handle

    private void PartyListDrawHandle(AddonEvent type, AddonArgs args)
    {
        lock (Lock)
        {
            if (!IsBuilt)
            {
                InitImageNodes();
                InitMarkedObject();
            }
        }

        if (NeedClear && MarkedObject.Count is 0 && IsScreenReady())
        {
            ResetPartyMemberList((AtkUnitBase*)args.Addon.Address);
            NeedClear = false;
        }
    }

    private static void PartyListFinalizeHandle(AddonEvent type, AddonArgs args) => ReleaseImageNodes();

    private static void ProcMarkIconSetted(uint markIndex, uint entityID)
    {
        if (AgentHUD.Instance() is null || InfoProxyCrossRealm.Instance() is null)
            return;

        int index;
        var mark = (int)(markIndex + 1);
        if (mark <= 0 || mark > MarkerSheet.Count)
        {
            if (FindMember(entityID, out index))
                RemoveMemberMark(index);
            return;
        }

        var icon = MarkerSheet.ElementAt(mark);
        if (entityID is 0xE000_0000 or 0xE00_0000)
        {
            RemoveMark(icon.Icon);
            return;
        }

        if (!FindMember(entityID, out index))
            RemoveMark(icon.Icon);
        else if (MarkedObject.TryGetValue(icon.Icon, out var outValue) && outValue == index)
        {
            // 对同一个成员重复标记
        }
        else
        {
            RemoveMemberMark(index);
            AddMemberMark(index, icon.Icon);
        }
    }

    private static void AddMemberMark(int memberIndex, int markID)
    {
        MarkedObject[markID] = memberIndex;
        ShowImageNode(memberIndex, markID);
        NeedClear = false;
    }

    private static void RemoveMemberMark(int memberIndex)
    {
        if (MarkedObject.ContainsValue(memberIndex))
        {
            MarkedObject.Remove(MarkedObject.First(x => x.Value == memberIndex).Key);
            HideImageNode(memberIndex);
        }

        if (MarkedObject.Count == 0)
            NeedClear = true;
    }

    private static void RemoveMark(int markID)
    {
        if (MarkedObject.Remove(markID, out var outValue))
            HideImageNode(outValue);
        if (MarkedObject.Count == 0)
            NeedClear = true;
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
            var myGroup = InfoProxyCrossRealm.GetMemberByEntityId(LocalPlayerState.EntityID);
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

    #region Hook

    private void DetourLocalMarkingFunc(nint manager, uint markingType, nint objectID, nint a4)
    {
        // 自身标记会触发两回，第一次a4: E000_0000, 第二次a4: 自身GameObjectId
        // 队友标记只会触发一回，a4: 队友GameObjectId
        // 鲶鱼精local a4: 0
        // if (a4 != (nint?)DService.ObjectTable.LocalPlayer?.GameObjectId)

        TaskHelper.Insert(() => ProcMarkIconSetted(markingType, (uint)objectID));
        LocalMarkingHook!.Original(manager, markingType, objectID, a4);
    }

    #endregion

    private class Config : ModuleConfiguration
    {
        public Vector2 IconOffset = new(0, 0);
        public int Size = 27;
        public bool HidePartyListIndexNumber = true;
    }
}
