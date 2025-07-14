using System;
using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Utility.Signatures;
using Dalamud.Utility;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Addon = Lumina.Excel.Sheets.Addon;
using Condition = Lumina.Excel.Sheets.Condition;
using Dalamud.Hooking;
using ContentsFinder = FFXIVClientStructs.FFXIV.Client.Game.UI.ContentsFinder;

namespace DailyRoutines.Modules;

public unsafe class ImprovedDutyFinderSettings : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("更好的组队查找设置"),
        Description = GetLoc("将查找器设置变为按钮。"),
        Category = ModuleCategories.UIOptimization,
        Author = ["Mizami"]
    };
    private static readonly CompSig  SetContentsFinderSettingsInitSig = new("E8 ?? ?? ?? ?? 49 8B 06 33 ED");
    private static readonly IAddonEventHandle?[] eventHandles = new IAddonEventHandle?[20]; // 增加数组大小
    private static int _eventHandleIndex = 0;                                                // 跟踪当前索引
    private static ImprovedDutyFinderSettings _tweak;
    public delegate void SetContentsFinderSettingsInitDelegate(byte* a1, nint a2);
    private static          Hook<SetContentsFinderSettingsInitDelegate>? _setContentsFinderSettingsInitHook;
    
    private static bool _languageConfigsAvailable = false;
    private static bool _languageConfigsChecked = false;

    private static bool AreLanguageConfigsAvailable()
    {
        if (_languageConfigsChecked) return _languageConfigsAvailable;
        
        try
        {
            _languageConfigsAvailable = 
                DService.GameConfig.UiConfig.TryGet("ContentsFinderUseLangTypeJA", out uint _) &&
                DService.GameConfig.UiConfig.TryGet("ContentsFinderUseLangTypeEN", out uint _) &&
                DService.GameConfig.UiConfig.TryGet("ContentsFinderUseLangTypeDE", out uint _) &&
                DService.GameConfig.UiConfig.TryGet("ContentsFinderUseLangTypeFR", out uint _);
                
            _languageConfigsChecked = true;
            
            if (!_languageConfigsAvailable)
            {
                DService.Log.Info("Language configs not available, language features will be disabled");
            }
        }
        catch (Exception ex)
        {
            DService.Log.Warning($"Failed to check language config availability: {ex.Message}");
            _languageConfigsAvailable = false;
            _languageConfigsChecked = true;
        }
        
        return _languageConfigsAvailable;
    }

    public override unsafe void Init()
    {
        _tweak = this;
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "ContentsFinder", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "ContentsFinder", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RaidFinder", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "RaidFinder", OnAddon);
        if (IsAddonAndNodesReady(InfosOm.ContentsFinder)|| IsAddonAndNodesReady(InfosOm.RaidFinder))
            OnAddon(AddonEvent.PostSetup, null);
        
        _setContentsFinderSettingsInitHook ??= DService.Hook.HookFromSignature<SetContentsFinderSettingsInitDelegate>(SetContentsFinderSettingsInitSig.Get(), SetContentsFinderSettingsInitDetour);
        _setContentsFinderSettingsInitHook.Enable();
    }

    public override void Uninit()
    {
        CleanupAllEvents();
        DService.Framework.Update -= UpdateIcons;
            
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        
        if (GetUnitBase("ContentsFinder", out var unitBase))
            ResetAddon(unitBase);
        if (GetUnitBase("RaidFinder", out var raidFinder))
            ResetAddon(raidFinder);
        
        DService.Framework.Update -= UpdateIcons;
        _setContentsFinderSettingsInitHook?.Disable();
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
            var tooltip = DService.Data.GetExcelSheet<Addon>().GetRowOrDefault(tooltipId)?.Text.ExtractText() ?? $"{Setting}";
            AtkStage.Instance()->TooltipManager.ShowTooltip(unitBase->Id, node, tooltip);
        }
    };

    private readonly List<DutyFinderSettingDisplay> dutyFinderSettingIcons = [
        new(DutyFinderSetting.JoinPartyInProgress, 60644, 2519),
        new(DutyFinderSetting.UnrestrictedParty, 60641, 10008),
        new(DutyFinderSetting.LevelSync, 60649, 12696),
        new(DutyFinderSetting.MinimumIl, 60642, 10010),
        new(DutyFinderSetting.SilenceEcho, 60647, 12691),
        new(DutyFinderSetting.ExplorerMode, 60648, 13038),
        new(DutyFinderSetting.LimitedLevelingRoulette, 60640, 13030),

        new(DutyFinderSetting.LootRule) {
            GetIcon = () => {
                return GetCurrentSettingValue(DutyFinderSetting.LootRule) switch {
                    0 => 60645,
                    1 => 60645,
                    2 => 60646,
                    _ => 0,
                };
            },
            GetTooltip = () => {
                return GetCurrentSettingValue(DutyFinderSetting.LootRule) switch {
                    0 => 10022,
                    1 => 10023,
                    2 => 10024,
                    _ => 0,
                };
            }
        }
    ];
    
    private List<DutyFinderSettingDisplay> GetLanguageButtons()
    {
        if (!AreLanguageConfigsAvailable())
            return new List<DutyFinderSettingDisplay>();
            
        return [
            new(DutyFinderSetting.Ja, 0, 10),
            new(DutyFinderSetting.En, 0, 11),
            new(DutyFinderSetting.De, 0, 12),
            new(DutyFinderSetting.Fr, 0, 13)
        ];
    }

    private static unsafe void OnAddon(AddonEvent type, AddonArgs? args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                if (args is AddonSetupArgs setupArgs)
                    _tweak?.SetupAddon((AtkUnitBase*)setupArgs.Addon);
                break;
            case AddonEvent.PreFinalize:
                if (args is AddonFinalizeArgs finalizeArgs)
                    _tweak?.ResetAddon((AtkUnitBase*)finalizeArgs.Addon);
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
        LinkNodeAfterTargetNode(container, unitBase, defaultContainer);
        
        foreach (var index in Enumerable.Range(0, eventHandles.Length))
        {
            if (eventHandles[index] is { } handle)
            {
                DService.AddonEvent.RemoveEvent(handle);
                eventHandles[index] = null;
            }
        }
        
        for (var i = 0; i < dutyFinderSettingIcons.Count; i++)
        {
            var settingDetail = dutyFinderSettingIcons[i];

            var basedOn = unitBase->GetNodeById(7 + (uint)i);
            if (basedOn == null) continue;

            var imgNode = MakeImageNode(CustomNodes.Get($"{nameof(ImprovedDutyFinderSettings)}_Icon_{settingDetail.Setting}"), new HelpersOm.PartInfo(0, 0, 24, 24));
            LinkNodeAtEnd(imgNode, container, unitBase);

            imgNode->AtkResNode.SetPositionFloat(basedOn->GetXFloat(), basedOn->GetYFloat());
            imgNode->AtkResNode.SetWidth(basedOn->GetWidth());
            imgNode->AtkResNode.SetHeight(basedOn->GetHeight());

            imgNode->AtkResNode.NodeFlags |= NodeFlags.RespondToMouse | NodeFlags.EmitsEvents | NodeFlags.HasCollision;

            var handle = DService.AddonEvent.AddEvent((nint)unitBase, (nint)imgNode,
                                                      AddonEventType.MouseClick, ToggleSetting);
            eventHandles[i] = handle;
            
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
        frameworkTicksSinceUpdate = 0;
        DService.Framework.Update -= UpdateIcons;
        DService.Framework.Update += UpdateIcons;
        UpdateIcons(unitBase);
    }

    private void ToggleSetting(AddonEventType atkEventType, nint atkUnitBase, nint atkResNode)
    {
        if (atkEventType != AddonEventType.MouseClick) return;
        
        var node = (AtkResNode*)atkResNode;
        var nodeId = node->NodeId;
        
        foreach (var settingDetail in dutyFinderSettingIcons)
        {
            if (nodeId == CustomNodes.Get($"{nameof(ImprovedDutyFinderSettings)}_Icon_{settingDetail.Setting}"))
            {
                ToggleSetting(settingDetail.Setting);
                
                if (settingDetail.Setting == DutyFinderSetting.LootRule)
                {
                    var unitBase = (AtkUnitBase*)atkUnitBase;
                    HideTooltip(unitBase);
                    ShowTooltip(unitBase, node, settingDetail);
                }
                break;
            }
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
        {
            if (nodeId == 17 + (uint)i)
            {
                ToggleSetting(languageButtons[i].Setting);
                break;
            }
        }
    }

    private void OnMouseOver(AddonEventType atkEventType, nint atkUnitBase, nint atkResNode)
    {
        if (atkEventType != AddonEventType.MouseOver) return;
        
        var unitBase = (AtkUnitBase*)atkUnitBase;
        var node = (AtkResNode*)atkResNode;
        var nodeId = node->NodeId;
        
        foreach (var settingDetail in dutyFinderSettingIcons)
        {
            if (nodeId == CustomNodes.Get($"{nameof(ImprovedDutyFinderSettings)}_Icon_{settingDetail.Setting}"))
            {
                ShowTooltip(unitBase, node, settingDetail);
                break;
            }
        }
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
        {
            if (nodeId == 17 + (uint)i)
            {
                ShowTooltip(unitBase, node, languageButtons[i]);
                break;
            }
        }
    }

    private void OnMouseOut(AddonEventType atkEventType, nint atkUnitBase, nint atkResNode)
    {
        if (atkEventType != AddonEventType.MouseOut) return;
        
        var unitBase = (AtkUnitBase*)atkUnitBase;
        HideTooltip(unitBase);
    }

    private int frameworkTicksSinceUpdate;

    private void UpdateIcons(IFramework _)
    {
        if (GetUnitBase("ContentsFinder", out var unitBase)) 
            UpdateIcons(unitBase);
        if (GetUnitBase("RaidFinder", out var raidFinder)) 
            UpdateIcons(raidFinder);
        if (frameworkTicksSinceUpdate++ > 5) 
            DService.Framework.Update -= UpdateIcons;
    }

    private void UpdateIcons(AtkUnitBase* unitBase)
    {
        if (unitBase == null) return;
        frameworkTicksSinceUpdate = 0;
        foreach (var settingDetail in dutyFinderSettingIcons)
        {
            var nodeId = CustomNodes.Get($"{nameof(ImprovedDutyFinderSettings)}_Icon_{settingDetail.Setting}");
            var imgNode = GetNodeByID<AtkImageNode>(&unitBase->UldManager, nodeId, NodeType.Image);
            if (imgNode == null) continue;

            var icon = settingDetail.GetIcon();
            // Game gets weird sometimes loading Icons using the specific icon function...
            imgNode->LoadTexture($"ui/icon/{icon / 5000 * 5000:000000}/{icon:000000}.tex");
            imgNode->AtkResNode.ToggleVisibility(true);
            var value = GetCurrentSettingValue(settingDetail.Setting);

            var isSettingDisabled = (settingDetail.Setting == DutyFinderSetting.LevelSync && GetCurrentSettingValue(DutyFinderSetting.UnrestrictedParty) == 0);

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
        DService.Framework.Update -= UpdateIcons;
        var vanillaIconContainer = unitBase->GetNodeById(6);
        if (vanillaIconContainer == null) return;
        vanillaIconContainer->ToggleVisibility(true);
        var container = GetNodeByID(&unitBase->UldManager, CustomNodes.Get($"{nameof(ImprovedDutyFinderSettings)}_Container"));

        foreach (var settingDetail in dutyFinderSettingIcons)
        {
            var imgNode = GetNodeByID<AtkImageNode>(&unitBase->UldManager, CustomNodes.Get($"{nameof(ImprovedDutyFinderSettings)}_Icon_{settingDetail.Setting}"), NodeType.Image);
            if (imgNode == null) continue;

            HelpersOm.UnlinkAndFreeImageNode(imgNode, unitBase);
        }
        
        if (AreLanguageConfigsAvailable())
        {
            var languageButtons = GetLanguageButtons();
            for (var i = 0; i < languageButtons.Count; i++)
            {
                var node = unitBase->GetNodeById(17 + (uint)i);
                if (node == null) continue;
                // 事件清理在CleanupAllEvents中统一处理
            }
        }

        if (container == null) return;
        UnlinkNode(container, unitBase);
        container->Destroy(true);

        unitBase->UldManager.UpdateDrawNodeList();
        unitBase->UpdateCollisionNodeList(false);
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
        LimitedLevelingRoulette = 11,
    }
    
    [Signature("E8 ?? ?? ?? ?? 49 8B 06 33 ED")]
    private static delegate* unmanaged<byte*, nint, void> _setContentsFinderSettings;
    
    private static void SetContentsFinderSettingsInitDetour(byte* a1, nint a2)
    {
        // 调用原始函数
        _setContentsFinderSettingsInitHook?.Original(a1, a2);
    }
    
    private static byte GetCurrentSettingValue(DutyFinderSetting dutyFinderSetting)
    {
        // 修复：每次都重新获取，确保指针有效
        var contentsFinder = ContentsFinder.Instance();
        if (contentsFinder == null)
        {
            DService.Log.Warning($"ContentsFinder instance is null for {dutyFinderSetting}");
            return 0;
        }
        
        return dutyFinderSetting switch
        {
            DutyFinderSetting.Ja => AreLanguageConfigsAvailable() ? (byte)DService.GameConfig.UiConfig.GetUInt("ContentsFinderUseLangTypeJA") : (byte)0,
            DutyFinderSetting.En => AreLanguageConfigsAvailable() ? (byte)DService.GameConfig.UiConfig.GetUInt("ContentsFinderUseLangTypeEN") : (byte)0,
            DutyFinderSetting.De => AreLanguageConfigsAvailable() ? (byte)DService.GameConfig.UiConfig.GetUInt("ContentsFinderUseLangTypeDE") : (byte)0,
            DutyFinderSetting.Fr => AreLanguageConfigsAvailable() ? (byte)DService.GameConfig.UiConfig.GetUInt("ContentsFinderUseLangTypeFR") : (byte)0,
            DutyFinderSetting.LootRule => (byte)contentsFinder->LootRules,
            DutyFinderSetting.JoinPartyInProgress => (byte)DService.GameConfig.UiConfig.GetUInt("ContentsFinderSupplyEnable"),
            DutyFinderSetting.UnrestrictedParty => *(byte*)&contentsFinder->IsUnrestrictedParty,
            DutyFinderSetting.LevelSync => *(byte*)&contentsFinder->IsLevelSync,
            DutyFinderSetting.MinimumIl => *(byte*)&contentsFinder->IsMinimalIL,
            DutyFinderSetting.SilenceEcho => *(byte*)&contentsFinder->IsSilenceEcho,
            DutyFinderSetting.ExplorerMode => *(byte*)&contentsFinder->IsExplorerMode,
            DutyFinderSetting.LimitedLevelingRoulette => *(byte*)&contentsFinder->IsLimitedLevelingRoulette,
            _ => 0,
        };
    }

    private void CleanupAllEvents()
    {
        for (var i = 0; i < eventHandles.Length; i++)
        {
            if (eventHandles[i] is { } handle)
            {
                try
                {
                    DService.AddonEvent.RemoveEvent(handle);
                }
                catch (Exception ex)
                {
                    DService.Log.Warning($"Failed to remove event handle {i}: {ex.Message}");
                }
                eventHandles[i] = null;
            }
        }
        _eventHandleIndex = 0;
    }   
    
    private void ToggleSetting(DutyFinderSetting setting)
    {
        // block setting change if queued for a duty
        if (DService.Condition[ConditionFlag.InDutyQueue])
        {
            var condition = DService.Data.GetExcelSheet<Condition>().GetRow((uint)ConditionFlag.InDutyQueue).LogMessage.Value.Text.ToDalamudString();
            DService.Toast.ShowError(condition);
            return;
        }
        
        if (AreLanguageConfigsAvailable() && setting is DutyFinderSetting.Ja or DutyFinderSetting.En or DutyFinderSetting.De or DutyFinderSetting.Fr)
        {
            var nbEnabledLanguages = GetCurrentSettingValue(DutyFinderSetting.Ja) + 
                                   GetCurrentSettingValue(DutyFinderSetting.En) + 
                                   GetCurrentSettingValue(DutyFinderSetting.De) + 
                                   GetCurrentSettingValue(DutyFinderSetting.Fr);
            if (nbEnabledLanguages == 1 && GetCurrentSettingValue(setting) == 1)
            {
                return;
            }
        }

        var array = GetCurrentSettingArray();
        if (array == null) return;

        byte newValue;
        if (setting == DutyFinderSetting.LootRule)
            newValue = (byte)((array[(int)setting] + 1) % 3);
        else
            newValue = (byte)(array[(int)setting] == 0 ? 1 : 0);

        array[(int)setting] = newValue;

        if (!IsSettingArrayValid(array))
        {
            DService.Log.Error("Tweak appears to be broken.");
            return;
        }

        fixed (byte* arrayPtr = array)
        {
            _setContentsFinderSettingsInitHook?.Original(arrayPtr, (nint)UIModule.Instance());
        }
    }

    // array used in setContentsFinderSettings
    private static byte[] GetCurrentSettingArray()
    {
        var array = new byte[27];
        var nbSettings = Enum.GetValues<DutyFinderSetting>().Length;
        for (var i = 0; i < nbSettings; i++)
        {
            array[i] = GetCurrentSettingValue((DutyFinderSetting)i);
            array[i + nbSettings] = GetCurrentSettingValue((DutyFinderSetting)i); // prev value to print in chat when changed
        }

        array[26] = 1; // has changed

        return array;
    }

    private static bool IsSettingArrayValid(IReadOnlyList<byte> array)
    {
        var isArrayValid = true;
        var nbSettings = Enum.GetValues<DutyFinderSetting>().Length; // % for previous values
        for (var index = 0; index < array.Count; index++)
        {
            if ((index % nbSettings != (int)DutyFinderSetting.LootRule && array[index] != 0 && array[index] != 1) || (array[index] != 0 && array[index] != 1 && array[index] != 2))
            {
                isArrayValid = false;
                DService.Log.Error($"Invalid setting value ({array[index]}) for: {(DutyFinderSetting)(index % nbSettings)}");
            }
        }
        
        if (AreLanguageConfigsAvailable())
        {
            if (array[(int)DutyFinderSetting.Ja] == 0 && 
                array[(int)DutyFinderSetting.En] == 0 && 
                array[(int)DutyFinderSetting.De] == 0 && 
                array[(int)DutyFinderSetting.Fr] == 0)
            {
                isArrayValid = false;
                DService.Log.Error("No language selected, this is impossible.");
            }
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

    public static bool GetUnitBase(string name, out AtkUnitBase* unitBase, int index = 1)
    {
        unitBase = GetUnitBase(name, index);
        return unitBase != null;
    }

    public static AtkUnitBase* GetUnitBase(string name, int index = 1)
    {
        return (AtkUnitBase*)DService.Gui.GetAddonByName(name, index);
    }

    public static void LinkNodeAfterTargetNode<T>(T* atkNode, AtkUnitBase* parent, AtkResNode* targetNode) where T : unmanaged
    {
        var node = (AtkResNode*)atkNode;
        var prev = targetNode->PrevSiblingNode;
        node->ParentNode = targetNode->ParentNode;

        targetNode->PrevSiblingNode = node;
        prev->NextSiblingNode = node;

        node->PrevSiblingNode = prev;
        node->NextSiblingNode = targetNode;

        parent->UldManager.UpdateDrawNodeList();
    }

    public static void LinkNodeAtEnd<T>(T* atkNode, AtkResNode* parentNode, AtkUnitBase* addon) where T : unmanaged
    {
        var node = (AtkResNode*)atkNode;
        var endNode = parentNode->ChildNode;
        if (endNode == null)
        {
            // Adding to empty res node

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

    public static void UnlinkNode<T>(T* atkNode, AtkUnitBase* unitBase) where T : unmanaged
    {
        var node = (AtkResNode*)atkNode;
        if (node == null) return;

        if (node->ParentNode->ChildNode == node)
            node->ParentNode->ChildNode = node->NextSiblingNode;

        if (node->NextSiblingNode != null && node->NextSiblingNode->PrevSiblingNode == node)
            node->NextSiblingNode->PrevSiblingNode = node->PrevSiblingNode;

        if (node->PrevSiblingNode != null && node->PrevSiblingNode->NextSiblingNode == node)
            node->PrevSiblingNode->NextSiblingNode = node->NextSiblingNode;

        unitBase->UldManager.UpdateDrawNodeList();
    }

    private static AtkResNode* GetNodeByID(AtkUldManager* uldManager, uint nodeId, NodeType? type = null) => GetNodeByID<AtkResNode>(uldManager, nodeId, type);

    private static T* GetNodeByID<T>(AtkUldManager* uldManager, uint nodeId, NodeType? type = null) where T : unmanaged
    {
        if (uldManager == null) return null;
        if (uldManager->NodeList == null) return null;
        for (var i = 0; i < uldManager->NodeListCount; i++)
        {
            var n = uldManager->NodeList[i];
            if (n == null || n->NodeId != nodeId || type != null && n->Type != type.Value) continue;
            return (T*)n;
        }

        return null;
    }
}

public static class CustomNodes
{
    private static readonly Dictionary<string, uint> NodeIds = new();
    private static uint _nextId = 0x53541000;

    public static uint Get(string name, int index = 0)
    {
        if (TryGet(name, index, out var id)) return id;
        lock (NodeIds)
        {
            id = _nextId;
            _nextId += 16;
            NodeIds.Add($"{name}#{index}", id);
            return id;
        }
    }

    public static bool TryGet(string name, int index, out uint id) => NodeIds.TryGetValue($"{name}#{index}", out id);
}
