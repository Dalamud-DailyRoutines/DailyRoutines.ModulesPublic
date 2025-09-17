using System;
using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Numerics;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Nodes;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedDutyFinderSetting : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("OptimizedDutyFinderSettingTitle"),
        Description = GetLoc("OptimizedDutyFinderSettingDescription"),
        Category    = ModuleCategories.UIOptimization,
        Author      = ["Mizami", "Cyf5119"]
    };

    private delegate void SetContentsFinderSettingsInitDelegate(byte* data, UIModule* module);
    private static readonly SetContentsFinderSettingsInitDelegate SetContentsFinderSettingsInit =
        new CompSig("E8 ?? ?? ?? ?? 49 8B 06 45 33 FF 49 8B CE 45 89 7E 20 FF 50 28 B0 01").GetDelegate<SetContentsFinderSettingsInitDelegate>();

    private static readonly Dictionary<DutyFinderSettingDisplay, (IconButtonNode ButtonNode, IconImageNode ImageNode)> Nodes = [];

    private static HorizontalListNode? LayoutNode;

    protected override void Init()
    {
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    ["ContentsFinder", "RaidFinder"], OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, ["ContentsFinder", "RaidFinder"], OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, ["ContentsFinder", "RaidFinder"], OnAddon);
    }

    private static void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PreFinalize:
                foreach (var (buttonNode, imageNode) in Nodes.Values)
                {
                    Service.AddonController.DetachNode(buttonNode);
                    Service.AddonController.DetachNode(imageNode);
                }
                Nodes.Clear();
                
                Service.AddonController.DetachNode(LayoutNode);
                LayoutNode = null;
                break;
            case AddonEvent.PostRefresh:
            case AddonEvent.PostDraw:
                var addon = args.Addon.ToAtkUnitBase();
                if (addon == null) return;
                
                if (LayoutNode == null)
                {
                    var defaultContainer = addon->GetNodeById(6);
                    if (defaultContainer == null) return;
                    
                    defaultContainer->ToggleVisibility(false);
                    
                    var attchTargetNode = addon->GetNodeById(4);
                    if (attchTargetNode == null) return;
                    
                    LayoutNode = new HorizontalListNode
                    {
                        IsVisible   = true,
                        Size        = new(defaultContainer->Width, defaultContainer->Height),
                        Position    = new(defaultContainer->X - 5, defaultContainer->Y),
                        ItemSpacing = 0
                    };
                    Service.AddonController.AttachNode(LayoutNode, attchTargetNode);
                    
                    foreach (var settingDetail in DutyFinderSettingIcons)
                    {
                        if (Nodes.ContainsKey(settingDetail)) continue;
                    
                        var button = new IconButtonNode
                        {
                            IsVisible = true,
                            Size      = new(36),
                            Position  = new(0, -5),
                            Tooltip   = LuminaWrapper.GetAddonText(settingDetail.GetTooltip()),
                            IconId    = settingDetail.GetIcon()
                        };
                        
                        button.OnClick = () =>
                        {
                            ToggleSetting(settingDetail.Setting);
                            OnAddon(AddonEvent.PreFinalize, null);
                        };
                        
                        button.BackgroundNode.IsVisible = false;
                        
                        Nodes[settingDetail] = (button, null!);
                        LayoutNode.AddNode(button);
                    }
                }

                foreach (var (settingDetail, (buttonNode, _)) in Nodes)
                {
                    var value = GetCurrentSettingValue(settingDetail.Setting);
                    
                    buttonNode.IconId = settingDetail.GetIcon();
                    
                    if (settingDetail.Setting                                       is DutyFinderSetting.LevelSync &&
                        GetCurrentSettingValue(DutyFinderSetting.UnrestrictedParty) == 0)
                        buttonNode.Color = buttonNode.Color.WithW(value != 0 ? 1 : 0.25f);
                    else
                        buttonNode.Color = buttonNode.Color.WithW(value != 0 ? 1 : 0.5f);

                    buttonNode.Tooltip = LuminaWrapper.GetAddonText(settingDetail.GetTooltip());
                }
                
                break;
        }
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        OnAddon(AddonEvent.PreFinalize, null);
    }
    
    private static void SetupLanguageButtonEvents(AtkUnitBase* addon)
    {
        if (GetLanguageButtons() is not { Count: > 0 } langButtons)
            return;

        var languageButtonNodes = new List<TextButtonNode>();

        for (var i = 0; i < langButtons.Count; i++)
        {
            var langSetting  = langButtons[i];
            var originalNode = addon->GetNodeById(17 + (uint)i);
            if (originalNode == null) continue;

            var languageButton = new TextButtonNode
            {
                IsVisible = true,
                Size      = new(originalNode->Width, originalNode->Height),
                Position  = new(originalNode->X, originalNode->Y),
                SeString  = string.Empty,
                OnClick   = () => OnLanguageClick(langSetting.Setting)
            };

            languageButton.BackgroundNode.IsVisible = false;
            languageButton.LabelNode.IsVisible      = false;

            var parentNode = originalNode->ParentNode;
            if (parentNode != null)
                Service.AddonController.AttachNode(languageButton, parentNode);

            languageButtonNodes.Add(languageButton);
        }

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
            SetContentsFinderSettingsInit(arrayPtr, UIModule.Instance());
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
                new DutyFinderSettingDisplay(DutyFinderSetting.Ja, 0, 10),
                new DutyFinderSettingDisplay(DutyFinderSetting.En, 0, 11),
                new DutyFinderSettingDisplay(DutyFinderSetting.De, 0, 12),
                new DutyFinderSettingDisplay(DutyFinderSetting.Fr, 0, 13)
            ];

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

    #endregion

    #region 事件

    private static void OnLanguageClick(DutyFinderSetting setting)
    {
        if (!IsLangConfigReady()) return;
        ToggleSetting(setting);
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
        public DutyFinderSettingDisplay(DutyFinderSetting setting, uint icon, uint tooltip) : this(setting)
        {
            GetIcon    = () => icon;
            GetTooltip = () => tooltip;
        }

        public Func<uint>?  GetIcon    { get; init; }
        public Func<uint>? GetTooltip { get; init; }

        public void ShowTooltip(AtkUnitBase* unitBase, AtkResNode* node) =>
            AtkStage.Instance()->TooltipManager.ShowTooltip(unitBase->Id, node,
                                                            LuminaWrapper.GetAddonText(GetTooltip()));
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
