using System;
using System.Collections.Generic;
using System.Numerics;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Utility.Numerics;
using FFXIVClientStructs.FFXIV.Client.UI;
using KamiToolKit.Classes.Timelines;
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
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

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
    
    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        OnAddon(AddonEvent.PreFinalize, null);
    }

    private static void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PreFinalize:
                foreach (var (buttonNode, imageNode) in Nodes.Values)
                {
                    buttonNode?.DetachNode();
                    imageNode?.DetachNode();
                }

                Nodes.Clear();

                LayoutNode?.DetachNode();
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
                    LayoutNode.AttachNode(attchTargetNode);

                    foreach (var settingDetail in DutyFinderSettingIcons)
                    {
                        if (Nodes.ContainsKey(settingDetail)) continue;

                        var button = new IconButtonNode
                        {
                            IsVisible = true,
                            Size      = new(32),
                            Position  = new(0, -5f),
                            Tooltip   = LuminaWrapper.GetAddonText(settingDetail.GetTooltip()),
                        };

                        button.OnClick = () =>
                        {
                            ToggleSetting(settingDetail.Setting);
                            button.Tooltip = LuminaWrapper.GetAddonText(settingDetail.GetTooltip());
                            button.HideTooltip();
                            button.ShowTooltip();
                        };

                        button.BackgroundNode.IsVisible = false;
                        button.ImageNode.IsVisible      = false;

                        var origPosition = new Vector2(4, 5);
                        var iconNode = new IconImageNode
                        {
                            IconId     = settingDetail.GetIcon(),
                            Size       = new(24),
                            IsVisible  = true,
                            Position   = origPosition,
                            FitTexture = true
                        };

                        iconNode.AddTimeline(new TimelineBuilder()
                                             .AddFrameSetWithFrame(1,  10, 1,  position: origPosition)
                                             .AddFrameSetWithFrame(11, 17, 11, position: origPosition)
                                             .AddFrameSetWithFrame(18, 26, 18, position: origPosition + new Vector2(0.0f, 1.0f))
                                             .AddFrameSetWithFrame(27, 36, 27, position: origPosition)
                                             .AddFrameSetWithFrame(37, 46, 37, position: origPosition)
                                             .AddFrameSetWithFrame(47, 53, 47, position: origPosition)
                                             .Build());

                        iconNode.AttachNode(button);

                        Nodes[settingDetail] = (button, iconNode);
                        LayoutNode.AddNode(button);
                    }

                    if (GetLanguageButtons() is { Count: > 0 } langButtons)
                    {
                        for (var i = 0; i < langButtons.Count; i++)
                        {
                            var origNode = addon->GetNodeById(17 + (uint)i);
                            if (origNode == null) continue;

                            var parentNode = origNode->ParentNode;
                            if (parentNode == null) continue;

                            var langSetting = langButtons[i];

                            var languageButton = new IconButtonNode
                            {
                                IsVisible = true,
                                Size      = new(28),
                                Position  = new(origNode->X - 7, origNode->Y - 5),
                                Tooltip   = LuminaWrapper.GetAddonText((uint)(4266 + i)),
                                OnClick = () =>
                                {
                                    if (!IsLangConfigReady()) return;
                                    ToggleSetting(langSetting.Setting);
                                }
                            };

                            languageButton.BackgroundNode.IsVisible = false;
                            languageButton.ImageNode.IsVisible      = false;

                            languageButton.AttachNode(parentNode);
                            Nodes[langSetting] = (languageButton, null!);
                        }
                    }
                }

                foreach (var (settingDetail, (buttonNode, imageNode)) in Nodes)
                {
                    var value = GetCurrentSettingValue(settingDetail.Setting);
                    
                    if (imageNode != null)
                    {
                        imageNode.IconId = settingDetail.GetIcon();

                        if (settingDetail.Setting is DutyFinderSetting.LevelSync &&
                            GetCurrentSettingValue(DutyFinderSetting.UnrestrictedParty) == 0)
                            imageNode.Color = buttonNode.Color.WithW(value != 0 ? 1 : 0.25f);
                        else
                            imageNode.Color = buttonNode.Color.WithW(value != 0 ? 1 : 0.5f);
                    }

                    buttonNode.Tooltip = LuminaWrapper.GetAddonText(settingDetail.GetTooltip());
                }

                break;
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

        public Func<uint>? GetIcon    { get; init; }
        public Func<uint>? GetTooltip { get; init; }
    }

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
}
