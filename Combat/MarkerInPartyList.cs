using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DailyRoutines.Modules;

public unsafe class MarkerInPartyList : DailyModuleBase
{
    public override ModuleInfo Info => new()
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

    private static Config? _config;
    private static readonly List<nint> _imageNodes = new(8);
    private static readonly Dictionary<int, int> _markedObject = new(8);
    private static bool _isBuilt, _needClear;
    private static readonly object _lock = new();

    public override void Init()
    {
        _isBuilt = false;
        _config = LoadConfig<Config>() ?? new();

        MarkerSheet ??= DService.Data.GetExcelSheet<Marker>();

        LocalMarkingHook = DService.Hook.HookFromSignature<LocalMarkingFunc>(LocalMarkingSig.Get(), DetourLocalMarkingFunc);
        LocalMarkingHook.Enable();

        DService.ClientState.TerritoryChanged += ResetmarkedObject;
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "_PartyList", PartyListDrawHandle);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "_PartyList", PartyListFinalizeHandle);
    }

    public override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(AddonEvent.PostDraw, PartyListDrawHandle);
        DService.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, PartyListFinalizeHandle);
        DService.ClientState.TerritoryChanged -= ResetmarkedObject;

        ResetPartyMemberList();
        ReleaseImageNodes();

        base.Uninit();
    }

    public override void ConfigUI()
    {
        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        ImGui.InputFloat2(Lang.Get("MarkerInPartyList-IconOffset"), ref _config.IconOffset, "%d");
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            SaveConfig(_config);
            RefreshPosition();
        }

        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        ImGui.InputInt(Lang.Get("MarkerInPartyList-IconScale"), ref _config.Size, 0, 0);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            SaveConfig(_config);
            RefreshPosition();
        }

        if (ImGui.Checkbox(Lang.Get("MarkerInPartyList-HidePartyListIndexNumber"), ref _config.HidePartyListIndexNumber))
        {
            SaveConfig(_config);
            ResetPartyMemberList();
        }
    }

    private static void ResetmarkedObject(ushort obj)
    {
        foreach (var i in Enumerable.Range(0, 8))
        {
            HideImageNode(i);
        }
        _markedObject.Clear();
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
        if (pPartyList is null || _config == null || (!_config.HidePartyListIndexNumber && !visible))
            return;

        var memberIdList = Enumerable.Range(10, 8).ToList();
        foreach (var id in memberIdList)
        {
            var member = pPartyList->GetNodeById((uint)id);
            if (member is null || member->GetComponent() is null)
                continue;

            if (!member->IsVisible())
                break;

            var textNode = member->GetComponent()->UldManager.SearchNodeById(15);
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
        lock (_lock)
        {
            if (_isBuilt)
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
                _imageNodes.Add((nint)imageNode);

                LinkNodeAtEnd((AtkResNode*)imageNode, partylist);
            }
            _isBuilt = true;
        }

    }

    private static void ReleaseImageNodes()
    {
        lock (_lock)
        {
            if (!_isBuilt)
                return;

            if (PartyList is null || PartyList->UldManager.LoadedState is not AtkLoadState.Loaded)
            {
                Error("Failed to get partylist");
                return;
            }

            foreach (var item in _imageNodes)
            {
                UnlinkAndFreeImageNode((AtkImageNode*)item, PartyList);
            }
            _imageNodes.Clear();
        }
    }

    private static void ShowImageNode(int i, int iconId)
    {
        if (i is < 0 or > 7 || PartyList is null || _imageNodes.Count <= i)
            return;

        var node = (AtkImageNode*)_imageNodes[i];
        if (node is null)
            return;

        var component = PartyList->GetNodeById((uint)(10 + i));
        var (x, y) = (component->X + BasePosition.X + _config.IconOffset.X, component->Y + BasePosition.Y + _config.IconOffset.Y);
        node->LoadIconTexture((uint)iconId, 0);
        node->AtkResNode.SetHeight((ushort)_config.Size);
        node->AtkResNode.SetWidth((ushort)_config.Size);
        node->AtkResNode.SetPositionFloat(x, y);
        node->AtkResNode.ToggleVisibility(true);

        ModifyPartyMemberNumber(PartyList, false);
    }

    private static void HideImageNode(int i)
    {
        if (i is < 0 or > 7 || _imageNodes.Count <= i)
            return;
        var node = (AtkImageNode*)_imageNodes[i];
        if (node is null)
            return;

        node->AtkResNode.ToggleVisibility(false);
    }

    private static void RefreshPosition()
    {
        var partylist = (AtkUnitBase*)DService.Gui.GetAddonByName("_PartyList");
        if (partylist is null || !IsAddonAndNodesReady(partylist))
            return;
        foreach (var item in _imageNodes.Zip(Enumerable.Range(10, 8)))
        {
            var node = (AtkImageNode*)item.First;
            var component = partylist->GetNodeById((uint)(10 + item.Second));
            (var x, var y) = (component->X + BasePosition.X + _config.IconOffset.X,
                                 component->Y + BasePosition.Y + _config.IconOffset.Y);
            node->AtkResNode.SetPositionFloat(x, y);
            node->AtkResNode.SetHeight((ushort)_config.Size);
            node->AtkResNode.SetWidth((ushort)_config.Size);
            node->AtkResNode.ToggleVisibility(true);
        }
    }

    #endregion

    #region Handle

    private static void PartyListDrawHandle(AddonEvent type, AddonArgs args)
    {
        lock (_lock)
        {
            if (!_isBuilt)
                InitImageNodes();
        }

        if (_needClear && _markedObject.Count is 0)
        {
            ResetPartyMemberList((AtkUnitBase*)args.Addon);
            _needClear = false;
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

        var mark = (int)(markIndex + 1);
        if (mark <= 0 || mark > MarkerSheet.Count)
        {
            FindMember(entityId, RemoveMemberMark);
            return;
        }

        var icon = MarkerSheet.ElementAt(mark);

        if (entityId is 0xE000_0000 or 0xE00_0000)
        {
            RemoveMark(icon.Icon);
            return;
        }

        if (FindMember(entityId, index =>
        {
            RemoveMemberMark(index);
            AddMemberMark(index, icon.Icon);
        }))
        {
            return;
        }

        RemoveMark(icon.Icon);
    }

    private static void AddMemberMark(int memberIndex, int markId)
    {
        _markedObject[markId] = memberIndex;
        ShowImageNode(memberIndex, markId);
        _needClear = false;
    }

    private static void RemoveMemberMark(int memberIndex)
    {
        if (_markedObject.ContainsValue(memberIndex))
        {
            _markedObject.Remove(_markedObject.First(x => x.Value == memberIndex).Key);
            HideImageNode(memberIndex);
        }

        if (_markedObject.Count == 0)
            _needClear = true;
    }

    private static void RemoveMark(int markId)
    {
        if (_markedObject.Remove(markId, out var outValue))
            HideImageNode(outValue);
        if (_markedObject.Count == 0)
            _needClear = true;
    }

    private static bool FindMember(uint entityId, Action<int> predicate)
    {
        var pAgentHUD = AgentHUD.Instance();
        for (var i = 0; i < 8; ++i)
        {
            var charData = pAgentHUD->PartyMembers[i];
            if (entityId == charData.EntityId)
            {
                predicate.Invoke(i);
                return true;
            }
        }

        if (InfoProxyCrossRealm.Instance()->IsCrossRealm > 0)
        {
            var pGroupMember = InfoProxyCrossRealm.GetMemberByEntityId(entityId);
            if (pGroupMember is not null && pGroupMember->GroupIndex == 0)
            {
                predicate.Invoke(pGroupMember->MemberIndex);
                return true;
            }

        }

        return false;
    }

    #endregion

    #region Hook

    private static void DetourLocalMarkingFunc(nint manager, uint markingType, nint objectId, nint a4)
    {
        ProcMarkIconSetted(markingType, (uint)objectId);

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

