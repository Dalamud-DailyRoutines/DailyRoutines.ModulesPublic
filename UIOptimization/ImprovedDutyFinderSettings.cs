using System;
using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Addon = Lumina.Excel.Sheets.Addon;
using Condition = Lumina.Excel.Sheets.Condition;
using ContentsFinder = FFXIVClientStructs.FFXIV.Client.Game.UI.ContentsFinder;

namespace DailyRoutines.Modules;

public unsafe class ImprovedDutyFinderSettings : DailyModuleBase
{
    public delegate void SetContentsFinderSettingsInitDelegate(byte* a1, nint a2);

    private static readonly CompSig SetContentsFinderSettingsInitSig = new("E8 ?? ?? ?? ?? 49 8B 06 33 ED");
    private static readonly IAddonEventHandle?[] EventHandles = new IAddonEventHandle?[20];
    private static int eventHandleIndex;

    private static readonly List<(nint ImageNodePtr, DutyFinderSetting Setting)> CreatedImageNodes = [];
    private static AtkResNode* CreatedContainer = null;
    private static Hook<SetContentsFinderSettingsInitDelegate>? setContentsFinderSettingsInitHook;

    private static bool languageConfigsAvailable;
    private static bool languageConfigsChecked;

    private readonly List<DutyFinderSettingDisplay> dutyFinderSettingIcons =
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

    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("更好的组队查找设置"),
        Description = GetLoc("将查找器设置变为按钮。"),
        Category = ModuleCategories.UIOptimization,
        Author = ["Mizami"]
    };

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
        catch (Exception ex)
        {
            DService.Log.Debug($"Failed to check language config availability: {ex.Message}");
            languageConfigsAvailable = false;
            languageConfigsChecked = true;
        }

        return languageConfigsAvailable;
    }

    public override void Init()
    {
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "ContentsFinder", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "ContentsFinder", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RaidFinder", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "RaidFinder", OnAddon);

        if (IsAddonAndNodesReady(InfosOm.ContentsFinder) || IsAddonAndNodesReady(RaidFinder))
            OnAddon(AddonEvent.PostSetup, null);

        setContentsFinderSettingsInitHook ??=
            DService.Hook.HookFromSignature<SetContentsFinderSettingsInitDelegate>(
                SetContentsFinderSettingsInitSig.Get(), SetContentsFinderSettingsInitDetour);
        setContentsFinderSettingsInitHook.Enable();
    }

    public override void Uninit()
    {
        CleanupAllEvents();
        CleanupAllNodes();
        FrameworkManager.Unregister(UpdateIcons);

        DService.AddonLifecycle.UnregisterListener(OnAddon);

        var contentsFinder = GetAddonByName("ContentsFinder");
        if (contentsFinder != null)
            ResetAddon(contentsFinder);
        var raidFinder = GetAddonByName("RaidFinder");
        if (raidFinder != null)
            ResetAddon(raidFinder);

        setContentsFinderSettingsInitHook?.Disable();
    }

    private List<DutyFinderSettingDisplay> GetLanguageButtons()
    {
        if (!AreLanguageConfigsAvailable())
            return new List<DutyFinderSettingDisplay>();

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

        // 创建容器（可选，如果你想要的话）
        var container = IMemorySpace.GetUISpace()->Create<AtkResNode>();
        container->SetWidth(defaultContainer->GetWidth());
        container->SetHeight(defaultContainer->GetHeight());
        container->SetPositionFloat(defaultContainer->GetXFloat(), defaultContainer->GetYFloat());
        container->SetScale(1, 1);
        container->NodeId = CustomNodes.Get($"{nameof(ImprovedDutyFinderSettings)}_Container");
        container->Type = NodeType.Res;
        container->ToggleVisibility(true);
        // 直接插入container到defaultContainer之前
        var prev = defaultContainer->PrevSiblingNode;
        container->ParentNode = defaultContainer->ParentNode;
        defaultContainer->PrevSiblingNode = container;
        if (prev != null)
            prev->NextSiblingNode = container;
        container->PrevSiblingNode = prev;
        container->NextSiblingNode = defaultContainer;

        unitBase->UldManager.UpdateDrawNodeList();
        // 保存容器引用
        CreatedContainer = container;

        // 清理旧的引用
        CreatedImageNodes.Clear();

        foreach (var index in Enumerable.Range(0, EventHandles.Length))
            if (EventHandles[index] is { } handle)
            {
                DService.AddonEvent.RemoveEvent(handle);
                EventHandles[index] = null;
            }

        for (var i = 0; i < dutyFinderSettingIcons.Count; i++)
        {
            var settingDetail = dutyFinderSettingIcons[i];

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

            // 保存节点引用
            CreatedImageNodes.Add(((nint)imgNode, settingDetail.Setting));

            var handle = DService.AddonEvent.AddEvent((nint)unitBase, (nint)imgNode,
                                                      AddonEventType.MouseClick, ToggleSetting);
            EventHandles[i] = handle;

            var hoverHandle = DService.AddonEvent.AddEvent((nint)unitBase, (nint)imgNode,
                                                           AddonEventType.MouseOver, OnMouseOver);
            var outHandle = DService.AddonEvent.AddEvent((nint)unitBase, (nint)imgNode,
                                                         AddonEventType.MouseOut, OnMouseOut);
        }

        var languageButtons = GetLanguageButtons();
        if (languageButtons.Count > 0)
        {
            for (var i = 0; i < languageButtons.Count; i++)
            {
                var settingDetail = languageButtons[i];

                var node = unitBase->GetNodeById(17 + (uint)i);
                if (node == null) continue;

                node->NodeFlags |= NodeFlags.RespondToMouse | NodeFlags.EmitsEvents | NodeFlags.HasCollision;

                var clickHandle = DService.AddonEvent.AddEvent((nint)unitBase, (nint)node,
                                                               AddonEventType.MouseClick, ToggleLanguageSetting);
                var hoverHandle = DService.AddonEvent.AddEvent((nint)unitBase, (nint)node,
                                                               AddonEventType.MouseOver, OnLanguageMouseOver);
                var outHandle = DService.AddonEvent.AddEvent((nint)unitBase, (nint)node,
                                                             AddonEventType.MouseOut, OnMouseOut);
            }
        }

        unitBase->UpdateCollisionNodeList(false);
        FrameworkManager.Unregister(UpdateIcons);
        FrameworkManager.Register(UpdateIcons);
        UpdateIcons(unitBase);
    }

    private void ToggleSetting(AddonEventType atkEventType, nint atkUnitBase, nint atkResNode)
    {
        if (atkEventType != AddonEventType.MouseClick) return;

        var node = (AtkResNode*)atkResNode;

        // 通过节点指针查找对应的设置
        var foundSetting = CreatedImageNodes.FirstOrDefault(x => x.ImageNodePtr == atkResNode);
        if (foundSetting.ImageNodePtr == 0) return;

        var settingDetail = dutyFinderSettingIcons.FirstOrDefault(x => x.Setting == foundSetting.Setting);
        if (settingDetail == null) return;

        ToggleSetting(settingDetail.Setting);

        if (settingDetail.Setting == DutyFinderSetting.LootRule)
        {
            var unitBase = (AtkUnitBase*)atkUnitBase;
            HideTooltip(unitBase);
            ShowTooltip(unitBase, node, settingDetail);
        }
    }

    private void ToggleLanguageSetting(AddonEventType atkEventType, nint atkUnitBase, nint atkResNode)
    {
        if (atkEventType != AddonEventType.MouseClick) return;

        if (!AreLanguageConfigsAvailable()) return;

        var node = (AtkResNode*)atkResNode;
        var nodeId = node->NodeId;

        var languageButtons = GetLanguageButtons();
        for (var i = 0; i < languageButtons.Count; i++)
            if (nodeId == 17 + (uint)i)
            {
                ToggleSetting(languageButtons[i].Setting);
                break;
            }
    }

    private void OnMouseOver(AddonEventType atkEventType, nint atkUnitBase, nint atkResNode)
    {
        if (atkEventType != AddonEventType.MouseOver) return;

        var unitBase = (AtkUnitBase*)atkUnitBase;
        var node = (AtkResNode*)atkResNode;

        // 通过节点指针查找对应的设置
        var foundSetting = CreatedImageNodes.FirstOrDefault(x => x.ImageNodePtr == atkResNode);
        if (foundSetting.ImageNodePtr == 0) return;

        var settingDetail = dutyFinderSettingIcons.FirstOrDefault(x => x.Setting == foundSetting.Setting);
        if (settingDetail == null) return;

        ShowTooltip(unitBase, node, settingDetail);
    }

    private void OnLanguageMouseOver(AddonEventType atkEventType, nint atkUnitBase, nint atkResNode)
    {
        if (atkEventType != AddonEventType.MouseOver) return;

        if (!AreLanguageConfigsAvailable()) return;

        var unitBase = (AtkUnitBase*)atkUnitBase;
        var node = (AtkResNode*)atkResNode;
        var nodeId = node->NodeId;

        var languageButtons = GetLanguageButtons();
        for (var i = 0; i < languageButtons.Count; i++)
            if (nodeId == 17 + (uint)i)
            {
                ShowTooltip(unitBase, node, languageButtons[i]);
                break;
            }
    }

    private void OnMouseOut(AddonEventType atkEventType, nint atkUnitBase, nint atkResNode)
    {
        if (atkEventType != AddonEventType.MouseOut) return;

        var unitBase = (AtkUnitBase*)atkUnitBase;
        HideTooltip(unitBase);
    }

    private void UpdateIcons(IFramework _)
    {
        var contentsFinder = GetAddonByName("ContentsFinder");
        if (contentsFinder != null)
            UpdateIcons(contentsFinder);
        var raidFinder = GetAddonByName("RaidFinder");
        if (raidFinder != null)
            UpdateIcons(raidFinder);
        if (!Throttler.Throttle("ImprovedDutyFinderSettings-StopUpdate", 10))
            FrameworkManager.Unregister(UpdateIcons);
    }

    private void UpdateIcons(AtkUnitBase* unitBase)
    {
        if (unitBase == null || CreatedImageNodes.Count == 0) return;

        foreach (var (imageNodePtr, setting) in CreatedImageNodes)
        {
            var imgNode = (AtkImageNode*)imageNodePtr;
            if (imgNode == null) continue;

            var settingDetail = dutyFinderSettingIcons.FirstOrDefault(x => x.Setting == setting);
            if (settingDetail?.GetIcon == null) continue;

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
        CleanupAllNodes(); // 新增：清理所有节点
        FrameworkManager.Unregister(UpdateIcons);

        var vanillaIconContainer = unitBase->GetNodeById(6);
        if (vanillaIconContainer == null) return;
        vanillaIconContainer->ToggleVisibility(true);

        // 如果容器存在，销毁它
        if (CreatedContainer != null)
        {
            CreatedContainer->Destroy(true);
            CreatedContainer = null;
        }

        unitBase->UldManager.UpdateDrawNodeList();
        unitBase->UpdateCollisionNodeList(false);
    }

    private static void CleanupAllNodes()
    {
        CreatedImageNodes.Clear();
        CreatedContainer = null;
    }

    private static void SetContentsFinderSettingsInitDetour(byte* a1, nint a2)
    {
        setContentsFinderSettingsInitHook?.Original(a1, a2);
    }

    private static byte GetCurrentSettingValue(DutyFinderSetting dutyFinderSetting)
    {
        var contentsFinder = ContentsFinder.Instance();

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
            DutyFinderSetting.LootRule => (byte)contentsFinder->LootRules,
            DutyFinderSetting.JoinPartyInProgress => (byte)DService.GameConfig.UiConfig.GetUInt(
                "ContentsFinderSupplyEnable"),
            DutyFinderSetting.UnrestrictedParty => *(byte*)&contentsFinder->IsUnrestrictedParty,
            DutyFinderSetting.LevelSync => *(byte*)&contentsFinder->IsLevelSync,
            DutyFinderSetting.MinimumIl => *(byte*)&contentsFinder->IsMinimalIL,
            DutyFinderSetting.SilenceEcho => *(byte*)&contentsFinder->IsSilenceEcho,
            DutyFinderSetting.ExplorerMode => *(byte*)&contentsFinder->IsExplorerMode,
            DutyFinderSetting.LimitedLevelingRoulette => *(byte*)&contentsFinder->IsLimitedLevelingRoulette,
            _ => 0
        };
    }

    private void CleanupAllEvents()
    {
        for (var i = 0; i < EventHandles.Length; i++)
            if (EventHandles[i] is { } handle)
            {
                try
                {
                    DService.AddonEvent.RemoveEvent(handle);
                }
                catch (Exception ex)
                {
                    DService.Log.Debug($"Failed to remove event handle {i}: {ex.Message}");
                }

                EventHandles[i] = null;
            }

        eventHandleIndex = 0;
    }

    private void ToggleSetting(DutyFinderSetting setting)
    {
        if (DService.Condition[ConditionFlag.InDutyQueue])
        {
            var condition = DService.Data.GetExcelSheet<Condition>().GetRow((uint)ConditionFlag.InDutyQueue).LogMessage
                                    .Value.Text.ToDalamudString();
            DService.Toast.ShowError(condition);
            return;
        }

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

        if (!IsSettingArrayValid(array))
        {
            DService.Log.Error("It appears to be broken.");
            return;
        }

        fixed (byte* arrayPtr = array)
        {
            setContentsFinderSettingsInitHook?.Original(arrayPtr, (nint)UIModule.Instance());
        }
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

    private static bool IsSettingArrayValid(IReadOnlyList<byte> array)
    {
        var isArrayValid = true;
        var nbSettings = Enum.GetValues<DutyFinderSetting>().Length;
        for (var index = 0; index < array.Count; index++)
            if ((index % nbSettings != (int)DutyFinderSetting.LootRule && array[index] != 0 && array[index] != 1) ||
                (array[index] != 0 && array[index] != 1 && array[index] != 2))
            {
                isArrayValid = false;
                DService.Log.Error(
                    $"Invalid setting value ({array[index]}) for: {(DutyFinderSetting)(index % nbSettings)}");
            }


        return isArrayValid;
    }

    private static void HideTooltip(AtkUnitBase* unitBase)
    {
        AtkStage.Instance()->TooltipManager.HideTooltip(unitBase->Id);
    }

    private static void ShowTooltip(AtkUnitBase* unitBase, AtkResNode* node, DutyFinderSettingDisplay settingDetail)
    {
        settingDetail.ShowTooltip(unitBase, node);
    }


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
            var tooltip = DService.Data.GetExcelSheet<Addon>().GetRowOrDefault(tooltipId)?.Text.ExtractText() ??
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
}

public static class CustomNodes
{
    private static readonly Dictionary<string, uint> NodeIds = new();
    private static uint nextId = 0x53541000;

    public static uint Get(string name, int index = 0)
    {
        if (NodeIds.TryGetValue($"{name}#{index}", out var id)) return id;
        lock (NodeIds)
        {
            id = nextId;
            nextId += 16;
            NodeIds.Add($"{name}#{index}", id);
            return id;
        }
    }
}
