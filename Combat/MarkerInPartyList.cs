using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class MarkerInPartyList : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("MarkerInPartyListTitle"),
        Description = GetLoc("MarkerInPartyListDescription"),
        Category = ModuleCategories.Combat,
        Author = ["status102"]
    };

    private const int DefaultIconId = 61201;
    private static readonly (short X, short Y) BasePosition = (41, 35);
    private static ExcelSheet<Marker>? MarkerSheet;

    private static readonly CompSig LocalMarkingSig = new("E8 ?? ?? ?? ?? 4C 8B C5 8B D7 48 8B CB E8");
    public static Hook<LocalMarkingFunc>? LocalMarkingHook;
    public delegate void LocalMarkingFunc(nint manager, uint markingType, nint objectId, nint a4);

    private static          Config?              ModuleConfig;
    private static readonly List<nint>           ImageNodes   = new(8);
    private static readonly Dictionary<int, int> MarkedObject = new(8); // markId, memberIndex
    private static          bool                 IsBuilt, NeedClear;
    
    private static readonly object Lock = new();

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

        base.Uninit();
    }

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        ImGui.InputFloat2(Lang.Get("MarkerInPartyList-IconOffset"), ref ModuleConfig.IconOffset, "%d");
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            SaveConfig(ModuleConfig);
            RefreshPosition();
        }

        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        ImGui.InputInt(Lang.Get("MarkerInPartyList-IconScale"), ref ModuleConfig.Size, 0, 0);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            SaveConfig(ModuleConfig);
            RefreshPosition();
        }

        if (ImGui.Checkbox(Lang.Get("MarkerInPartyList-HidePartyListIndexNumber"), ref ModuleConfig.HidePartyListIndexNumber))
        {
            SaveConfig(ModuleConfig);
            ResetPartyMemberList();
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

    private static void ModifyPartyMemberNumber(AtkUnitBase* pPartyList, bool visible)
    {
        if (pPartyList is null || ModuleConfig == null || (!ModuleConfig.HidePartyListIndexNumber && !visible))
            return;

        var memberIdList = Enumerable.Range(10, 8).ToList();
        foreach (var id in memberIdList)
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

    private static AtkImageNode* GenerateImageNode()
    {
        var node = (AtkImageNode*)IMemorySpace.GetUISpace()->Malloc((ulong)sizeof(AtkImageNode), 8);
        if (node == null)
        {
            DService.Log.Error("Failed to allocate memory for image parentNode");
            return null;
        }
        IMemorySpace.Memset(node, 0, (ulong)sizeof(AtkImageNode));
        node->Ctor();

        node->AtkResNode.Type = NodeType.Image;
        node->AtkResNode.NodeFlags = NodeFlags.AnchorLeft | NodeFlags.AnchorTop;
        node->AtkResNode.DrawFlags = 0;

        node->WrapMode = 1;
        node->Flags |= (byte)ImageNodeFlags.AutoFit;

        var partsList = (AtkUldPartsList*)IMemorySpace.GetUISpace()->Malloc((ulong)sizeof(AtkUldPartsList), 8);
        if (partsList == null)
        {
            DService.Log.Error("Failed to allocate memory for parts list");
            node->AtkResNode.Destroy(true);
            return null;
        }

        partsList->Id = 0;
        partsList->PartCount = 1;

        var part = (AtkUldPart*)IMemorySpace.GetUISpace()->Malloc((ulong)sizeof(AtkUldPart), 8);
        if (part == null)
        {
            DService.Log.Error("Failed to allocate memory for part");
            IMemorySpace.Free(partsList, (ulong)sizeof(AtkUldPartsList));
            node->AtkResNode.Destroy(true);
            return null;
        }

        part->U = 0;
        part->V = 0;
        part->Width = 80;
        part->Height = 80;

        partsList->Parts = part;

        var asset = (AtkUldAsset*)IMemorySpace.GetUISpace()->Malloc((ulong)sizeof(AtkUldAsset), 8);
        if (asset == null)
        {
            DService.Log.Error("Failed to allocate memory for asset");
            IMemorySpace.Free(part, (ulong)sizeof(AtkUldPart));
            IMemorySpace.Free(partsList, (ulong)sizeof(AtkUldPartsList));
            node->AtkResNode.Destroy(true);
            return null;
        }

        asset->Id = 0;
        asset->AtkTexture.Ctor();

        part->UldAsset = asset;

        node->PartsList = partsList;

        node->LoadIconTexture(DefaultIconId, 0);
        node->AtkResNode.SetPriority(5);
        return node;
    }

    private static void InitImageNodes()
    {
        var partylist = (AtkUnitBase*)DService.Gui.GetAddonByName("_PartyList");
        if (partylist is null)
        {
            DService.Log.Error("Failed to get partylist");
            return;
        }
        lock (Lock)
        {
            if (IsBuilt)
                return;

            foreach (var i in Enumerable.Range(10, 8))
            {
                var imageNode = GenerateImageNode();
                if (imageNode is null)
                {
                    DService.Log.Error($"Failed to create image parentNode-{i}");
                    continue;
                }
                imageNode->AtkResNode.NodeId = 114514;
                ImageNodes.Add((nint)imageNode);

                LinkNodeAtEnd((AtkResNode*)imageNode, partylist);
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
            if (gameObjectID == 0 || gameObjectID == 0xE0000000)
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

            foreach (var item in ImageNodes)
                UnlinkAndFreeImageNode((AtkImageNode*)item, PartyList);
            ImageNodes.Clear();
        }
    }

    private static void ShowImageNode(int i, int iconId)
    {
        if (i is < 0 or > 7 || PartyList is null || ImageNodes.Count <= i)
            return;

        var node = (AtkImageNode*)ImageNodes[i];
        if (node is null)
            return;

        var component = PartyList->GetNodeById((uint)(10 + i));
        var (x, y) = (component->X + BasePosition.X + ModuleConfig.IconOffset.X, component->Y + BasePosition.Y + ModuleConfig.IconOffset.Y);
        node->LoadIconTexture((uint)iconId, 0);
        node->AtkResNode.SetHeight((ushort)ModuleConfig.Size);
        node->AtkResNode.SetWidth((ushort)ModuleConfig.Size);
        node->AtkResNode.SetPositionFloat(x, y);
        node->AtkResNode.ToggleVisibility(true);

        ModifyPartyMemberNumber(PartyList, false);
    }

    private static void HideImageNode(int i)
    {
        if (i is < 0 or > 7 || ImageNodes.Count <= i)
            return;
        var node = (AtkImageNode*)ImageNodes[i];
        if (node is null)
            return;

        node->AtkResNode.ToggleVisibility(false);
    }

    private static void RefreshPosition()
    {
        var partylist = (AtkUnitBase*)DService.Gui.GetAddonByName("_PartyList");
        if (partylist is null || !IsAddonAndNodesReady(partylist))
            return;
        foreach (var item in ImageNodes.Zip(Enumerable.Range(10, 8)))
        {
            var node = (AtkImageNode*)item.First;
            var component = partylist->GetNodeById((uint)(10 + item.Second));
            (var x, var y) = (component->X + BasePosition.X + ModuleConfig.IconOffset.X,
                                 component->Y + BasePosition.Y + ModuleConfig.IconOffset.Y);
            node->AtkResNode.SetPositionFloat(x, y);
            node->AtkResNode.SetHeight((ushort)ModuleConfig.Size);
            node->AtkResNode.SetWidth((ushort)ModuleConfig.Size);
            node->AtkResNode.ToggleVisibility(true);
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
            ResetPartyMemberList((AtkUnitBase*)args.Addon);
            NeedClear = false;
        }
    }

    private static void PartyListFinalizeHandle(AddonEvent type, AddonArgs args)
    {
        ReleaseImageNodes();
    }

    private static void ProcMarkIconSetted(uint markIndex, uint entityId)
    {
        if (AgentHUD.Instance() is null || InfoProxyCrossRealm.Instance() is null)
            return;

        int index;
        var mark = (int)(markIndex + 1);
        if (mark <= 0 || mark > MarkerSheet.Count)
        {
            if (FindMember(entityId, out index))
                RemoveMemberMark(index);
            return;
        }

        var icon = MarkerSheet.ElementAt(mark);
        if (entityId is 0xE000_0000 or 0xE00_0000)
        {
            RemoveMark(icon.Icon);
            return;
        }

        if (!FindMember(entityId, out index))
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

    private static void AddMemberMark(int memberIndex, int markId)
    {
        MarkedObject[markId] = memberIndex;
        ShowImageNode(memberIndex, markId);
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

    private static void RemoveMark(int markId)
    {
        if (MarkedObject.Remove(markId, out var outValue))
            HideImageNode(outValue);
        if (MarkedObject.Count == 0)
            NeedClear = true;
    }

    private static bool FindMember(uint entityId, out int index)
    {
        var pAgentHUD = AgentHUD.Instance();
        for (var i = 0; i < pAgentHUD->PartyMemberCount; ++i)
        {
            var charData = pAgentHUD->PartyMembers[i];
            if (entityId == charData.EntityId)
            {
                index = i;
                return true;
            }
        }

        if (InfoProxyCrossRealm.Instance()->IsCrossRealm > 0)
        {
            var myGroup      = InfoProxyCrossRealm.GetMemberByEntityId((uint)DService.ObjectTable.LocalPlayer!.GameObjectId);
            var pGroupMember = InfoProxyCrossRealm.GetMemberByEntityId(entityId);
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

    private void DetourLocalMarkingFunc(nint manager, uint markingType, nint objectId, nint a4)
    {
        // 自身标记会触发两回，第一次a4: E000_0000, 第二次a4: 自身GameObjectId
        // 队友标记只会触发一回，a4: 队友GameObjectId
        // 鲶鱼精local a4: 0
        // if (a4 != (nint?)DService.ObjectTable.LocalPlayer?.GameObjectId)

        TaskHelper.Insert(() => ProcMarkIconSetted(markingType, (uint)objectId));
        LocalMarkingHook!.Original(manager, markingType, objectId, a4);
    }

    #endregion

    private class Config : ModuleConfiguration
    {
        public Vector2 IconOffset = new(0, 0);
        public int Size = 27;
        public bool HidePartyListIndexNumber = true;
    }
}

