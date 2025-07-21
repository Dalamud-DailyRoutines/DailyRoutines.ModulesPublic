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
using Addon = Lumina.Excel.Sheets.Addon;

namespace DailyRoutines.ModulesPublic;

public unsafe class ImprovedDutyFinderSettings : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("更好的组队查找设置"),
        Description = GetLoc("将查找器设置变为按钮。"),
        Category = ModuleCategories.UIOptimization,
        Author = ["Mizami"]
    };
    
    public delegate void SetContentsFinderSettingsInitDelegate(byte* a1, nint a2);
    private static readonly CompSig SetContentsFinderSettingsInitSig = new("E8 ?? ?? ?? ?? 49 8B 06 33 ED");
    private static Hook<SetContentsFinderSettingsInitDelegate>? setContentsFinderSettingsInitHook;
    
    private static readonly List<IAddonEventHandle> EventHandles = [];

    private static bool languageConfigsAvailable;
    private static bool languageConfigsChecked;

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
            GetIcon = () =>
            {
                return GetCurrentSettingValue(DutyFinderSetting.LootRule) switch
                {
                    0 => 60645,
                    1 => 60645,
                    2 => 60646,
                    _ => 0
                };
            },
            GetTooltip = () =>
            {
                return GetCurrentSettingValue(DutyFinderSetting.LootRule) switch
                {
                    0 => 10022,
                    1 => 10023,
                    2 => 10024,
                    _ => 0
                };
            }
        }
    ];

    
    protected override void Init()
    {
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "ContentsFinder", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "ContentsFinder", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RaidFinder", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "RaidFinder", OnAddon);

        setContentsFinderSettingsInitHook ??=
            SetContentsFinderSettingsInitSig.GetHook<SetContentsFinderSettingsInitDelegate>(
                SetContentsFinderSettingsInitDetour);
        setContentsFinderSettingsInitHook.Enable();
    }

    protected override void Uninit()
    {
        CleanupAllEvents();
        FrameworkManager.Unregister(UpdateIcons);

        DService.AddonLifecycle.UnregisterListener(OnAddon);

        var contentsFinder = ContentsFinder;
        if (contentsFinder != null)
            ResetAddon(contentsFinder);
        var raidFinder = RaidFinder;
        if (raidFinder != null)
            ResetAddon(raidFinder);

        base.Uninit();
    }


    private static bool AreLanguageConfigsAvailable()
    {
        if (languageConfigsChecked) return languageConfigsAvailable;

        try
        {
            languageConfigsAvailable =
                DService.GameConfig.UiConfig.TryGet("ContentsFinderUseLangTypeJA", out uint _) &&
                DService.GameConfig.UiConfig.TryGet("ContentsFinderUseLangTypeEN", out uint _) &&
                DService.GameConfig.UiConfig.TryGet("ContentsFinderUseLangTypeDE", out uint _) &&
                DService.GameConfig.UiConfig.TryGet("ContentsFinderUseLangTypeFR", out uint _);

            languageConfigsChecked = true;
        }
        catch
        {
            languageConfigsAvailable = false;
            languageConfigsChecked = true;
        }

        return languageConfigsAvailable;
    }

    private static List<DutyFinderSettingDisplay> GetLanguageButtons()
    {
        if (!AreLanguageConfigsAvailable())
            return [];

        return
        [
            new DutyFinderSettingDisplay(DutyFinderSetting.Ja, 0, 10),
            new DutyFinderSettingDisplay(DutyFinderSetting.En, 0, 11),
            new DutyFinderSettingDisplay(DutyFinderSetting.De, 0, 12),
            new DutyFinderSettingDisplay(DutyFinderSetting.Fr, 0, 13)
        ];
    }

    private void OnAddon(AddonEvent type, AddonArgs? args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                if (args is AddonSetupArgs setupArgs)
                    SetupAddon((AtkUnitBase*)setupArgs.Addon);
                break;
            case AddonEvent.PreFinalize:
                if (args is AddonFinalizeArgs finalizeArgs)
                    ResetAddon((AtkUnitBase*)finalizeArgs.Addon);
                break;
        }
    }

    private void SetupAddon(AtkUnitBase* unitBase)
    {
        var defaultContainer = unitBase->GetNodeById(6);
        if (defaultContainer == null) return;
        defaultContainer->ToggleVisibility(false);

        var container = IMemorySpace.GetUISpace()->Create<AtkResNode>();
        container->SetWidth(defaultContainer->GetWidth());
        container->SetHeight(defaultContainer->GetHeight());
        container->SetPositionFloat(defaultContainer->GetXFloat(), defaultContainer->GetYFloat());
        container->SetScale(1, 1);
        container->NodeId = CustomNodes.Get($"{nameof(ImprovedDutyFinderSettings)}_Container");
        container->Type = NodeType.Res;
        container->ToggleVisibility(true);
        var prev = defaultContainer->PrevSiblingNode;
        container->ParentNode = defaultContainer->ParentNode;
        defaultContainer->PrevSiblingNode = container;
        if (prev != null)
            prev->NextSiblingNode = container;
        container->PrevSiblingNode = prev;
        container->NextSiblingNode = defaultContainer;
        unitBase->UldManager.UpdateDrawNodeList();
        CleanupAllEvents();

        for (var i = 0; i < DutyFinderSettingIcons.Count; i++)
        {
            var settingDetail = DutyFinderSettingIcons[i];

            var basedOn = unitBase->GetNodeById(7 + (uint)i);
            if (basedOn == null) continue;

            var imgNode =
                MakeImageNode(CustomNodes.Get($"{nameof(ImprovedDutyFinderSettings)}_Icon_{settingDetail.Setting}"),
                              new PartInfo(0, 0, 24, 24));
            LinkNodeToContainer(imgNode, container, unitBase);
            imgNode->AtkResNode.SetPositionFloat(basedOn->GetXFloat(), basedOn->GetYFloat());
            imgNode->AtkResNode.SetWidth(basedOn->GetWidth());
            imgNode->AtkResNode.SetHeight(basedOn->GetHeight());

            imgNode->AtkResNode.NodeFlags |= NodeFlags.RespondToMouse | NodeFlags.EmitsEvents | NodeFlags.HasCollision;


            var clickHandle = DService.AddonEvent.AddEvent((nint)unitBase, (nint)imgNode,
                                                           AddonEventType.MouseClick, ToggleSetting);
            var hoverHandle = DService.AddonEvent.AddEvent((nint)unitBase, (nint)imgNode,
                                                           AddonEventType.MouseOver, OnMouseOver);
            var outHandle = DService.AddonEvent.AddEvent((nint)unitBase, (nint)imgNode,
                                                         AddonEventType.MouseOut, OnMouseOut);

            if (clickHandle != null)
                EventHandles.Add(clickHandle);

            if (hoverHandle != null)
                EventHandles.Add(hoverHandle);

            if (outHandle != null)
                EventHandles.Add(outHandle);
        }

        var languageButtons = GetLanguageButtons();
        if (languageButtons.Count > 0)
        {
            for (var i = 0; i < languageButtons.Count; i++)
            {
                var node = unitBase->GetNodeById(17 + (uint)i);
                if (node == null) continue;

                node->NodeFlags |= NodeFlags.RespondToMouse | NodeFlags.EmitsEvents | NodeFlags.HasCollision;

                var clickHandle = DService.AddonEvent.AddEvent((nint)unitBase, (nint)node,
                                                               AddonEventType.MouseClick, ToggleLanguageSetting);
                var hoverHandle = DService.AddonEvent.AddEvent((nint)unitBase, (nint)node,
                                                               AddonEventType.MouseOver, OnLanguageMouseOver);
                var outHandle = DService.AddonEvent.AddEvent((nint)unitBase, (nint)node,
                                                             AddonEventType.MouseOut, OnMouseOut);

                if (clickHandle != null)
                    EventHandles.Add(clickHandle);

                if (hoverHandle != null)
                    EventHandles.Add(hoverHandle);

                if (outHandle != null)
                    EventHandles.Add(outHandle);
            }
        }

        unitBase->UpdateCollisionNodeList(false);
        FrameworkManager.Unregister(UpdateIcons);
        FrameworkManager.Register(UpdateIcons);
        UpdateIcons(unitBase);
    }

    private void UpdateIcons(IFramework _)
    {
        if (ContentsFinder != null)
            UpdateIcons(ContentsFinder);

        if (RaidFinder != null)
            UpdateIcons(RaidFinder);
    }

    private void UpdateIcons(AtkUnitBase* unitBase)
    {
        foreach (var settingDetail in DutyFinderSettingIcons)
        {
            var nodeId = CustomNodes.Get($"{nameof(ImprovedDutyFinderSettings)}_Icon_{settingDetail.Setting}");
            var imgNode = FindImageNode(unitBase, nodeId);
            var icon = settingDetail.GetIcon();
            // Game gets weird sometimes loading Icons using the specific icon function...
            imgNode->LoadTexture($"ui/icon/{icon / 5000 * 5000:000000}/{icon:000000}.tex");
            imgNode->AtkResNode.ToggleVisibility(true);
            var value = GetCurrentSettingValue(settingDetail.Setting);

            var isSettingDisabled = settingDetail.Setting == DutyFinderSetting.LevelSync &&
                                    GetCurrentSettingValue(DutyFinderSetting.UnrestrictedParty) == 0;

            if (isSettingDisabled)
            {
                imgNode->AtkResNode.Color.A = (byte)(value != 0 ? 255 : 180);
                imgNode->AtkResNode.Alpha_2 = (byte)(value != 0 ? 255 : 180);

                imgNode->AtkResNode.MultiplyRed = 5;
                imgNode->AtkResNode.MultiplyGreen = 5;
                imgNode->AtkResNode.MultiplyBlue = 5;
                imgNode->AtkResNode.AddRed = 120;
                imgNode->AtkResNode.AddGreen = 120;
                imgNode->AtkResNode.AddBlue = 120;
            }
            else
            {
                imgNode->AtkResNode.Color.A = (byte)(value != 0 ? 255 : 127);
                imgNode->AtkResNode.Alpha_2 = (byte)(value != 0 ? 255 : 127);

                imgNode->AtkResNode.AddBlue = 0;
                imgNode->AtkResNode.AddGreen = 0;
                imgNode->AtkResNode.AddRed = 0;
                imgNode->AtkResNode.MultiplyRed = 100;
                imgNode->AtkResNode.MultiplyGreen = 100;
                imgNode->AtkResNode.MultiplyBlue = 100;
            }
        }
    }

    private void ResetAddon(AtkUnitBase* unitBase)
    {
        CleanupAllEvents();
        FrameworkManager.Unregister(UpdateIcons);

        var vanillaIconContainer = unitBase->GetNodeById(6);
        if (vanillaIconContainer == null) return;
        vanillaIconContainer->ToggleVisibility(true);

        unitBase->UldManager.UpdateDrawNodeList();
        unitBase->UpdateCollisionNodeList(false);
    }

    private static void ToggleSetting(AddonEventType atkEventType, AddonEventData data)
    {
        if (atkEventType != AddonEventType.MouseClick) return;

        var node = (AtkResNode*)data.NodeTargetPointer;

        var setting = GetSettingFromNodeId(node->NodeId);
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

    private static void ToggleSetting(DutyFinderSetting setting)
    {
        if (AreLanguageConfigsAvailable() && setting is DutyFinderSetting.Ja or DutyFinderSetting.En
                or DutyFinderSetting.De or DutyFinderSetting.Fr)
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
            setContentsFinderSettingsInitHook?.Original(arrayPtr, (nint)UIModule.Instance());
        }
    }

    private static void ToggleLanguageSetting(AddonEventType atkEventType, AddonEventData data)
    {
        if (atkEventType != AddonEventType.MouseClick) return;

        if (!AreLanguageConfigsAvailable()) return;

        var node = (AtkResNode*)data.NodeTargetPointer;
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
        var node = (AtkResNode*)data.NodeTargetPointer;

        var setting = GetSettingFromNodeId(node->NodeId);
        if (setting == null) return;

        var settingDetail = DutyFinderSettingIcons.FirstOrDefault(x => x.Setting == setting.Value);
        if (settingDetail == null) return;

        ShowTooltip(unitBase, node, settingDetail);
    }

    private static void OnLanguageMouseOver(AddonEventType atkEventType, AddonEventData data)
    {
        if (atkEventType != AddonEventType.MouseOver) return;

        if (!AreLanguageConfigsAvailable()) return;

        var unitBase = (AtkUnitBase*)data.AddonPointer;
        var node = (AtkResNode*)data.NodeTargetPointer;
        var nodeId = node->NodeId;

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

    private static DutyFinderSetting? GetSettingFromNodeId(uint nodeId)
    {
        // 遍历所有设置，检查节点ID是否匹配
        foreach (var setting in Enum.GetValues<DutyFinderSetting>())
        {
            var expectedNodeId = CustomNodes.Get($"{nameof(ImprovedDutyFinderSettings)}_Icon_{setting}");
            if (nodeId == expectedNodeId)
                return setting;
        }

        return null;
    }

    private static void CleanupAllEvents()
    {
        foreach (var handle in EventHandles)
            DService.AddonEvent.RemoveEvent(handle);
        EventHandles.Clear();
    }

    private static void SetContentsFinderSettingsInitDetour(byte* a1, nint a2) => 
        setContentsFinderSettingsInitHook?.Original(a1, a2);

    private static byte GetCurrentSettingValue(DutyFinderSetting dutyFinderSetting)
    {
        var option = ContentsFinderOption.Get();

        return dutyFinderSetting switch
        {
            DutyFinderSetting.Ja => AreLanguageConfigsAvailable()
                                        ? (byte)DService.GameConfig.UiConfig.GetUInt("ContentsFinderUseLangTypeJA")
                                        : (byte)0,
            DutyFinderSetting.En => AreLanguageConfigsAvailable()
                                        ? (byte)DService.GameConfig.UiConfig.GetUInt("ContentsFinderUseLangTypeEN")
                                        : (byte)0,
            DutyFinderSetting.De => AreLanguageConfigsAvailable()
                                        ? (byte)DService.GameConfig.UiConfig.GetUInt("ContentsFinderUseLangTypeDE")
                                        : (byte)0,
            DutyFinderSetting.Fr => AreLanguageConfigsAvailable()
                                        ? (byte)DService.GameConfig.UiConfig.GetUInt("ContentsFinderUseLangTypeFR")
                                        : (byte)0,
            DutyFinderSetting.JoinPartyInProgress => (byte)DService.GameConfig.UiConfig.GetUInt(
                "ContentsFinderSupplyEnable"),
            DutyFinderSetting.LootRule => (byte)option.LootRules,
            DutyFinderSetting.UnrestrictedParty => option.UnrestrictedParty ? (byte)1 : (byte)0,
            DutyFinderSetting.LevelSync => option.LevelSync ? (byte)1 : (byte)0,
            DutyFinderSetting.MinimumIl => option.MinimalIL ? (byte)1 : (byte)0,
            DutyFinderSetting.SilenceEcho => option.SilenceEcho ? (byte)1 : (byte)0,
            DutyFinderSetting.ExplorerMode => option.ExplorerMode ? (byte)1 : (byte)0,
            DutyFinderSetting.LimitedLevelingRoulette => option.IsLimitedLevelingRoulette ? (byte)1 : (byte)0,
            _ => 0
        };
    }


    private static byte[] GetCurrentSettingArray()
    {
        var array = new byte[27];
        var nbSettings = Enum.GetValues<DutyFinderSetting>().Length;
        for (var i = 0; i < nbSettings; i++)
        {
            array[i] = GetCurrentSettingValue((DutyFinderSetting)i);
            array[i + nbSettings] = GetCurrentSettingValue((DutyFinderSetting)i);
        }

        array[26] = 1;

        return array;
    }


    private static void HideTooltip(AtkUnitBase* unitBase) => 
        AtkStage.Instance()->TooltipManager.HideTooltip(unitBase->Id);

    private static void ShowTooltip(AtkUnitBase* unitBase, AtkResNode* node, DutyFinderSettingDisplay settingDetail) => 
        settingDetail.ShowTooltip(unitBase, node);


    public static void LinkNodeToContainer(AtkImageNode* atkNode, AtkResNode* parentNode, AtkUnitBase* addon)
    {
        var node = (AtkResNode*)atkNode;
        var endNode = parentNode->ChildNode;
        if (endNode == null)
        {
            parentNode->ChildNode = node;
            node->ParentNode = parentNode;
            node->PrevSiblingNode = null;
            node->NextSiblingNode = null;
        }
        else
        {
            while (endNode->PrevSiblingNode != null)
                endNode = endNode->PrevSiblingNode;
            node->ParentNode = parentNode;
            node->NextSiblingNode = endNode;
            node->PrevSiblingNode = null;
            endNode->PrevSiblingNode = node;
        }

        addon->UldManager.UpdateDrawNodeList();
    }

    //用自带的GetNodeById无法获取到，看了一下SimpleTweaks的代码，是自己重写了一个GetNodeByID的方法，我将其复制过来就可以正常获取
    public static AtkImageNode* FindImageNode(AtkUnitBase* unitBase, uint nodeId, NodeType? type = null)
    {
        if (unitBase == null) return null;
        var uldManager = &unitBase->UldManager;
        if (uldManager->NodeList == null) return null;

        for (var i = 0; i < uldManager->NodeListCount; i++)
        {
            var n = uldManager->NodeList[i];
            if (n != null && n->NodeId == nodeId && (type == null || n->Type == type.Value))
                return (AtkImageNode*)n;
        }

        return null;
    }


    private record DutyFinderSettingDisplay(DutyFinderSetting Setting)
    {
        public DutyFinderSettingDisplay(DutyFinderSetting setting, int icon, uint tooltip) : this(setting)
        {
            GetIcon = () => icon;
            GetTooltip = () => tooltip;
        }

        public Func<int>? GetIcon { get; init; }
        public Func<uint>? GetTooltip { get; init; }

        public void ShowTooltip(AtkUnitBase* unitBase, AtkResNode* node)
        {
            var tooltipId = GetTooltip();
            var tooltip = LuminaGetter.GetRow<Addon>(tooltipId)?.Text.ExtractText() ??
                          $"{Setting}";
            AtkStage.Instance()->TooltipManager.ShowTooltip(unitBase->Id, node, tooltip);
        }
    }

    private enum DutyFinderSetting
    {
        Ja = 0,
        En = 1,
        De = 2,
        Fr = 3,
        LootRule = 4,
        JoinPartyInProgress = 5,
        UnrestrictedParty = 6,
        LevelSync = 7,
        MinimumIl = 8,
        SilenceEcho = 9,
        ExplorerMode = 10,
        LimitedLevelingRoulette = 11
    }


    private static class CustomNodes
    {
        private static readonly ConcurrentDictionary<string, uint> NodeIds = new();
        private static uint nextId = 114514;

        public static uint Get(string name, int index = 0)
        {
            return NodeIds.GetOrAdd($"{name}#{index}", _ =>
            {
                var id = Interlocked.Add(ref nextId, 16) - 16;
                return id;
            });
        }
    }
}
