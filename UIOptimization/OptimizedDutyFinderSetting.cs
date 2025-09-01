using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedDutyFinderSetting : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("OptimizedDutyFinderSettingTitle"),
        Description = GetLoc("OptimizedDutyFinderSettingDescription"),
        Category    = ModuleCategories.UIOptimization,
        Author      = ["Mizami"]
    };
    
    private static readonly CompSig                                      SetContentsFinderSettingsInitSig = new("E8 ?? ?? ?? ?? 49 8B 06 33 ED");
    private delegate        void                                         SetContentsFinderSettingsInitDelegate(byte* a1, nint a2);
    private static          Hook<SetContentsFinderSettingsInitDelegate>? SetContentsFinderSettingsInitHook;

    private static readonly List<IAddonEventHandle> EventHandles = [];
    
    protected override void Init()
    {
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "ContentsFinder", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "ContentsFinder", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "RaidFinder",     OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "RaidFinder",     OnAddon);

        SetContentsFinderSettingsInitHook ??= SetContentsFinderSettingsInitSig.GetHook<SetContentsFinderSettingsInitDelegate>(SetContentsFinderSettingsInitDetour);
        SetContentsFinderSettingsInitHook.Enable();
    }

    protected override void Uninit()
    {
        CleanupAllEvents();
        FrameworkManager.Unregister(OnUpdate);

        DService.AddonLifecycle.UnregisterListener(OnAddon);

        if (ContentsFinder != null)
            ResetAddon(ContentsFinder);
        if (RaidFinder != null)
            ResetAddon(RaidFinder);
    }
    
    private static void SetupAddon(AtkUnitBase* addon)
    {
        if (addon == null) return;
        
        var defaultContainer = addon->GetNodeById(6);
        if (defaultContainer == null) return;
        
        defaultContainer->ToggleVisibility(false);

        var container = IMemorySpace.GetUISpace()->Create<AtkResNode>();
        container->SetWidth(defaultContainer->GetWidth());
        container->SetHeight(defaultContainer->GetHeight());
        container->SetPositionFloat(defaultContainer->GetXFloat(), defaultContainer->GetYFloat());
        container->SetScale(1, 1);
        container->NodeId = CustomNodes.Get($"{nameof(OptimizedDutyFinderSetting)}_Container");
        container->Type   = NodeType.Res;
        container->ToggleVisibility(true);
        
        var prev = defaultContainer->PrevSiblingNode;
        container->ParentNode             = defaultContainer->ParentNode;
        defaultContainer->PrevSiblingNode = container;
        if (prev != null)
            prev->NextSiblingNode = container;
        container->PrevSiblingNode = prev;
        container->NextSiblingNode = defaultContainer;
        
        addon->UldManager.UpdateDrawNodeList();
        CleanupAllEvents();

        for (var i = 0; i < DutyFinderSettingIcons.Count; i++)
        {
            var settingDetail = DutyFinderSettingIcons[i];

            var basedOn = addon->GetNodeById(7 + (uint)i);
            if (basedOn == null) continue;

            var node = MakeImageNode(CustomNodes.Get($"{nameof(OptimizedDutyFinderSetting)}_Icon_{settingDetail.Setting}"), new(0, 0, 24, 24));
            LinkNodeToContainer(node, container, addon);

            node->AtkResNode.SetPositionFloat(basedOn->GetXFloat(), basedOn->GetYFloat());
            node->AtkResNode.SetWidth(basedOn->GetWidth());
            node->AtkResNode.SetHeight(basedOn->GetHeight());
            node->AtkResNode.NodeFlags |= NodeFlags.RespondToMouse | NodeFlags.EmitsEvents | NodeFlags.HasCollision;

            if (DService.AddonEvent.AddEvent((nint)addon, (nint)node, AddonEventType.MouseClick, OnMouseClick) is { } clickHandle)
                EventHandles.Add(clickHandle);

            if (DService.AddonEvent.AddEvent((nint)addon, (nint)node, AddonEventType.MouseOver, OnMouseOver) is { } hoverHandle)
                EventHandles.Add(hoverHandle);

            if (DService.AddonEvent.AddEvent((nint)addon, (nint)node, AddonEventType.MouseOut, OnMouseOut) is { } outHandle)
                EventHandles.Add(outHandle);
        }

        if (GetLanguageButtons() is { Count: > 0 } langButtons)
        {
            for (var i = 0; i < langButtons.Count; i++)
            {
                var node = addon->GetNodeById(17 + (uint)i);
                if (node == null) continue;

                node->NodeFlags |= NodeFlags.RespondToMouse | NodeFlags.EmitsEvents | NodeFlags.HasCollision;

                if (DService.AddonEvent.AddEvent((nint)addon, (nint)node, AddonEventType.MouseClick, OnLangMouseClick) is { } clickHandle)
                    EventHandles.Add(clickHandle);

                if (DService.AddonEvent.AddEvent((nint)addon, (nint)node, AddonEventType.MouseOver, OnLangMouseOver) is { } hoverHandle)
                    EventHandles.Add(hoverHandle);

                if (DService.AddonEvent.AddEvent((nint)addon, (nint)node, AddonEventType.MouseOut, OnMouseOut) is { } outHandle)
                    EventHandles.Add(outHandle);
            }
        }

        addon->UpdateCollisionNodeList(false);
        FrameworkManager.Unregister(OnUpdate);
        FrameworkManager.Register(OnUpdate, throttleMS: 100);
        UpdateIcons(addon);
    }
    
    private static void UpdateIcons(AtkUnitBase* addon)
    {
        if (addon == null) return;
        
        foreach (var settingDetail in DutyFinderSettingIcons)
        {
            var nodeID  = CustomNodes.Get($"{nameof(OptimizedDutyFinderSetting)}_Icon_{settingDetail.Setting}");
            
            var imgNode = FindImageNode(addon, nodeID);
            var icon    = settingDetail.GetIcon();
            
            imgNode->LoadTexture($"ui/icon/{icon / 5000 * 5000:000000}/{icon:000000}.tex");
            imgNode->AtkResNode.ToggleVisibility(true);
            
            var value = GetCurrentSettingValue(settingDetail.Setting);
            if (settingDetail.Setting == DutyFinderSetting.LevelSync && GetCurrentSettingValue(DutyFinderSetting.UnrestrictedParty) == 0)
            {
                imgNode->AtkResNode.Color.A = (byte)(value != 0 ? 255 : 180);
                imgNode->AtkResNode.Alpha_2 = (byte)(value != 0 ? 255 : 180);

                imgNode->AtkResNode.MultiplyRed   = 5;
                imgNode->AtkResNode.MultiplyGreen = 5;
                imgNode->AtkResNode.MultiplyBlue  = 5;
                imgNode->AtkResNode.AddRed        = 120;
                imgNode->AtkResNode.AddGreen      = 120;
                imgNode->AtkResNode.AddBlue       = 120;
            }
            else
            {
                imgNode->AtkResNode.Color.A = (byte)(value != 0 ? 255 : 127);
                imgNode->AtkResNode.Alpha_2 = (byte)(value != 0 ? 255 : 127);

                imgNode->AtkResNode.AddBlue       = 0;
                imgNode->AtkResNode.AddGreen      = 0;
                imgNode->AtkResNode.AddRed        = 0;
                imgNode->AtkResNode.MultiplyRed   = 100;
                imgNode->AtkResNode.MultiplyGreen = 100;
                imgNode->AtkResNode.MultiplyBlue  = 100;
            }
        }
    }

    private static void ResetAddon(AtkUnitBase* addon)
    {
        if (addon == null) return;
        
        CleanupAllEvents();
        FrameworkManager.Unregister(OnUpdate);

        var vanillaIconContainer = addon->GetNodeById(6);
        if (vanillaIconContainer == null) return;
        
        vanillaIconContainer->ToggleVisibility(true);

        addon->UldManager.UpdateDrawNodeList();
        addon->UpdateCollisionNodeList(false);
    }
    
    private static void CleanupAllEvents()
    {
        foreach (var handle in EventHandles)
            DService.AddonEvent.RemoveEvent(handle);
        
        EventHandles.Clear();
    }

    private static byte GetCurrentSettingValue(DutyFinderSetting dutyFinderSetting)
    {
        var option = ContentsFinderOption.Get();
        return dutyFinderSetting switch
        {
            DutyFinderSetting.Ja => IsLangConfigReady()
                                        ? (byte)DService.GameConfig.UiConfig.GetUInt("ContentsFinderUseLangTypeJA")
                                        : (byte)0,
            DutyFinderSetting.En => IsLangConfigReady()
                                        ? (byte)DService.GameConfig.UiConfig.GetUInt("ContentsFinderUseLangTypeEN")
                                        : (byte)0,
            DutyFinderSetting.De => IsLangConfigReady()
                                        ? (byte)DService.GameConfig.UiConfig.GetUInt("ContentsFinderUseLangTypeDE")
                                        : (byte)0,
            DutyFinderSetting.Fr => IsLangConfigReady()
                                        ? (byte)DService.GameConfig.UiConfig.GetUInt("ContentsFinderUseLangTypeFR")
                                        : (byte)0,
            DutyFinderSetting.JoinPartyInProgress     => (byte)DService.GameConfig.UiConfig.GetUInt("ContentsFinderSupplyEnable"),
            DutyFinderSetting.LootRule                => (byte)option.LootRules,
            DutyFinderSetting.UnrestrictedParty       => option.UnrestrictedParty ? (byte)1 : (byte)0,
            DutyFinderSetting.LevelSync               => option.LevelSync ? (byte)1 : (byte)0,
            DutyFinderSetting.MinimumIl               => option.MinimalIL ? (byte)1 : (byte)0,
            DutyFinderSetting.SilenceEcho             => option.SilenceEcho ? (byte)1 : (byte)0,
            DutyFinderSetting.ExplorerMode            => option.ExplorerMode ? (byte)1 : (byte)0,
            DutyFinderSetting.LimitedLevelingRoulette => option.IsLimitedLevelingRoulette ? (byte)1 : (byte)0,
            _                                         => 0
        };
    }
    
    private static void ToggleSetting(DutyFinderSetting setting)
    {
        if (IsLangConfigReady() && setting is DutyFinderSetting.Ja or DutyFinderSetting.En or DutyFinderSetting.De or DutyFinderSetting.Fr)
        {
            var nbEnabledLanguages = GetCurrentSettingValue(DutyFinderSetting.Ja) +
                                     GetCurrentSettingValue(DutyFinderSetting.En) +
                                     GetCurrentSettingValue(DutyFinderSetting.De) +
                                     GetCurrentSettingValue(DutyFinderSetting.Fr);
            if (nbEnabledLanguages == 1 && GetCurrentSettingValue(setting) == 1)
                return;
        }

        var array = GetCurrentSettingArray();

        byte newValue;
        if (setting == DutyFinderSetting.LootRule)
            newValue = (byte)((array[(int)setting] + 1) % 3);
        else
            newValue = (byte)(array[(int)setting] == 0 ? 1 : 0);

        array[(int)setting] = newValue;

        fixed (byte* arrayPtr = array)
        {
            SetContentsFinderSettingsInitHook?.Original(arrayPtr, (nint)UIModule.Instance());
        }
    }
    
    private static byte[] GetCurrentSettingArray()
    {
        var array      = new byte[27];
        var nbSettings = Enum.GetValues<DutyFinderSetting>().Length;
        for (var i = 0; i < nbSettings; i++)
        {
            array[i]              = GetCurrentSettingValue((DutyFinderSetting)i);
            array[i + nbSettings] = GetCurrentSettingValue((DutyFinderSetting)i);
        }

        array[26] = 1;

        return array;
    }
    
    private static List<DutyFinderSettingDisplay> GetLanguageButtons() =>
        !IsLangConfigReady()
            ? []
            :
            [
                new(DutyFinderSetting.Ja, 0, 10),
                new(DutyFinderSetting.En, 0, 11),
                new(DutyFinderSetting.De, 0, 12),
                new(DutyFinderSetting.Fr, 0, 13)
            ];
    
    private static DutyFinderSetting? GetSettingFromNodeID(uint nodeID)
    {
        foreach (var setting in Enum.GetValues<DutyFinderSetting>())
        {
            var expectedNodeId = CustomNodes.Get($"{nameof(OptimizedDutyFinderSetting)}_Icon_{setting}");
            if (nodeID == expectedNodeId)
                return setting;
        }

        return null;
    }
    
    #region 工具
    
    private static bool IsLangConfigReady()
    {
        try
        {
            if (DService.GameConfig.UiConfig.TryGet("ContentsFinderUseLangTypeJA", out uint _) &&
                DService.GameConfig.UiConfig.TryGet("ContentsFinderUseLangTypeEN", out uint _) &&
                DService.GameConfig.UiConfig.TryGet("ContentsFinderUseLangTypeDE", out uint _) &&
                DService.GameConfig.UiConfig.TryGet("ContentsFinderUseLangTypeFR", out uint _))
                return true;
        }
        catch
        {
            // ignored
        }

        return false;
    }

    private static void HideTooltip(AtkUnitBase* unitBase) =>
        AtkStage.Instance()->TooltipManager.HideTooltip(unitBase->Id);

    private static void ShowTooltip(AtkUnitBase* unitBase, AtkResNode* node, DutyFinderSettingDisplay settingDetail) =>
        settingDetail.ShowTooltip(unitBase, node);

    private static void LinkNodeToContainer(AtkImageNode* atkNode, AtkResNode* parentNode, AtkUnitBase* addon)
    {
        var node    = (AtkResNode*)atkNode;
        var endNode = parentNode->ChildNode;
        if (endNode == null)
        {
            parentNode->ChildNode = node;
            node->ParentNode      = parentNode;
            node->PrevSiblingNode = null;
            node->NextSiblingNode = null;
        }
        else
        {
            while (endNode->PrevSiblingNode != null)
                endNode = endNode->PrevSiblingNode;
            node->ParentNode         = parentNode;
            node->NextSiblingNode    = endNode;
            node->PrevSiblingNode    = null;
            endNode->PrevSiblingNode = node;
        }

        addon->UldManager.UpdateDrawNodeList();
    }

    private static AtkImageNode* FindImageNode(AtkUnitBase* unitBase, uint nodeId)
    {
        if (unitBase == null) return null;
        var uldManager = &unitBase->UldManager;
        if (uldManager->NodeList == null) return null;

        for (var i = 0; i < uldManager->NodeListCount; i++)
        {
            var n = uldManager->NodeList[i];
            if (n != null && n->NodeId == nodeId)
                return (AtkImageNode*)n;
        }

        return null;
    }

    #endregion

    #region 事件
    
    private static void SetContentsFinderSettingsInitDetour(byte* a1, nint a2) =>
        SetContentsFinderSettingsInitHook?.Original(a1, a2);
    
    private static void OnUpdate(IFramework _)
    {
        if (ContentsFinder == null && RaidFinder == null)
        {
            FrameworkManager.Unregister(OnUpdate);
            return;
        }
        
        if (ContentsFinder != null)
            UpdateIcons(ContentsFinder);

        if (RaidFinder != null)
            UpdateIcons(RaidFinder);
    }

    private static void OnAddon(AddonEvent type, AddonArgs? args)
    {
        if (args == null) return;

        switch (type)
        {
            case AddonEvent.PostSetup:
                SetupAddon((AtkUnitBase*)args.Addon);
                break;
            case AddonEvent.PreFinalize:
                ResetAddon((AtkUnitBase*)args.Addon);
                break;
        }
    }
    
    private static void OnMouseClick(AddonEventType atkEventType, AddonEventData data)
    {
        if (atkEventType != AddonEventType.MouseClick) return;

        var node = (AtkResNode*)data.NodeTargetPointer;

        var setting = GetSettingFromNodeID(node->NodeId);
        if (setting == null) return;

        var settingDetail = DutyFinderSettingIcons.FirstOrDefault(x => x.Setting == setting.Value);
        if (settingDetail == null) return;

        ToggleSetting(settingDetail.Setting);

        if (settingDetail.Setting == DutyFinderSetting.LootRule)
        {
            var unitBase = (AtkUnitBase*)data.AddonPointer;
            HideTooltip(unitBase);
            ShowTooltip(unitBase, node, settingDetail);
        }
    }

    private static void OnLangMouseClick(AddonEventType atkEventType, AddonEventData data)
    {
        if (atkEventType != AddonEventType.MouseClick) return;

        if (!IsLangConfigReady()) return;

        var node   = (AtkResNode*)data.NodeTargetPointer;
        var nodeId = node->NodeId;

        var languageButtons = GetLanguageButtons();
        for (var i = 0; i < languageButtons.Count; i++)
        {
            if (nodeId == 17 + (uint)i)
            {
                ToggleSetting(languageButtons[i].Setting);
                break;
            }
        }
    }

    private static void OnMouseOver(AddonEventType atkEventType, AddonEventData data)
    {
        if (atkEventType != AddonEventType.MouseOver) return;

        var unitBase = (AtkUnitBase*)data.AddonPointer;
        var node     = (AtkResNode*)data.NodeTargetPointer;

        var setting = GetSettingFromNodeID(node->NodeId);
        if (setting == null) return;

        var settingDetail = DutyFinderSettingIcons.FirstOrDefault(x => x.Setting == setting.Value);
        if (settingDetail == null) return;

        ShowTooltip(unitBase, node, settingDetail);
    }

    private static void OnLangMouseOver(AddonEventType atkEventType, AddonEventData data)
    {
        if (atkEventType != AddonEventType.MouseOver) return;

        if (!IsLangConfigReady()) return;

        var unitBase = (AtkUnitBase*)data.AddonPointer;
        var node     = (AtkResNode*)data.NodeTargetPointer;
        var nodeId   = node->NodeId;

        var languageButtons = GetLanguageButtons();
        for (var i = 0; i < languageButtons.Count; i++)
        {
            if (nodeId == 17 + (uint)i)
            {
                ShowTooltip(unitBase, node, languageButtons[i]);
                break;
            }
        }
    }

    private static void OnMouseOut(AddonEventType atkEventType, AddonEventData data)
    {
        if (atkEventType != AddonEventType.MouseOut) return;

        var unitBase = (AtkUnitBase*)data.AddonPointer;
        HideTooltip(unitBase);
    }

    #endregion

    #region 自定类
    
    private enum DutyFinderSetting
    {
        Ja                      = 0,
        En                      = 1,
        De                      = 2,
        Fr                      = 3,
        LootRule                = 4,
        JoinPartyInProgress     = 5,
        UnrestrictedParty       = 6,
        LevelSync               = 7,
        MinimumIl               = 8,
        SilenceEcho             = 9,
        ExplorerMode            = 10,
        LimitedLevelingRoulette = 11
    }

    private record DutyFinderSettingDisplay(DutyFinderSetting Setting)
    {
        public Func<int>?  GetIcon    { get; init; }
        public Func<uint>? GetTooltip { get; init; }
        
        public DutyFinderSettingDisplay(DutyFinderSetting setting, int icon, uint tooltip) : this(setting)
        {
            GetIcon    = () => icon;
            GetTooltip = () => tooltip;
        }

        public void ShowTooltip(AtkUnitBase* unitBase, AtkResNode* node) => 
            AtkStage.Instance()->TooltipManager.ShowTooltip(unitBase->Id, node, LuminaWrapper.GetAddonText(GetTooltip()));
    }
    
    private static class CustomNodes
    {
        private static readonly ConcurrentDictionary<string, uint> NodeIds = [];

        private static uint NextNodeID = 114514;

        public static uint Get(string name, int index = 0) => 
            NodeIds.GetOrAdd($"{name}#{index}", _ => Interlocked.Add(ref NextNodeID, 16) - 16);
    }

    #endregion
    
    #region 数据

    private static readonly List<DutyFinderSettingDisplay> DutyFinderSettingIcons =
    [
        new(DutyFinderSetting.JoinPartyInProgress, 60644, 2519),
        new(DutyFinderSetting.UnrestrictedParty, 60641, 10008),
        new(DutyFinderSetting.LevelSync, 60649, 12696),
        new(DutyFinderSetting.MinimumIl, 60642, 10010),
        new(DutyFinderSetting.SilenceEcho, 60647, 12691),
        new(DutyFinderSetting.ExplorerMode, 60648, 13038),
        new(DutyFinderSetting.LimitedLevelingRoulette, 60640, 13030),

        new(DutyFinderSetting.LootRule)
        {
            GetIcon = () => GetCurrentSettingValue(DutyFinderSetting.LootRule) switch
            {
                0 => 60645,
                1 => 60645,
                2 => 60646,
                _ => 0
            },
            GetTooltip = () => GetCurrentSettingValue(DutyFinderSetting.LootRule) switch
            {
                0 => 10022,
                1 => 10023,
                2 => 10024,
                _ => 0
            }
        }
    ];

    #endregion
}
