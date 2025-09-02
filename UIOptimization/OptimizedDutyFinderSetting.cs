using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Numerics;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Nodes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedDutyFinderSetting : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("OptimizedDutyFinderSettingTitle"),
        Description = GetLoc("OptimizedDutyFinderSettingDescription"),
        Category = ModuleCategories.UIOptimization,
        Author = ["Mizami","cyf"]
    };


    private static readonly CompSig                                      SetContentsFinderSettingsInitSig = new("E8 ?? ?? ?? ?? 49 8B 06 33 ED");
    private delegate        void                                         SetContentsFinderSettingsInitDelegate(byte* a1, nint a2);
    private static          Hook<SetContentsFinderSettingsInitDelegate>? SetContentsFinderSettingsInitHook;
    

    private static readonly Dictionary<string, List<IconButtonNode>> DutyFinderButtonNodes = [];
    private static readonly Dictionary<string, List<IconImageNode>> DutyFinderImageNodes = [];
    private static readonly Dictionary<string, List<TextButtonNode>> LanguageButtonNodes = [];
    private static readonly Dictionary<string, HorizontalListNode> LayoutNodes = [];

    private static AddonController<AddonContentsFinder>? ContentsFinderController;
    private static AddonController<AddonRaidFinder>? RaidFinderController;
    private static NativeController? NativeController;

    protected override void Init()
    {
        NativeController = new NativeController(DService.PI);
        ContentsFinderController = new AddonController<AddonContentsFinder>(DService.PI);
        ContentsFinderController.OnAttach += addon => SetupAddon((AtkUnitBase*)addon);
        ContentsFinderController.OnDetach += addon => ResetAddon((AtkUnitBase*)addon);
        ContentsFinderController.OnRefresh += addon => RefreshAddon((AtkUnitBase*)addon);
        ContentsFinderController.Enable();

        RaidFinderController = new AddonController<AddonRaidFinder>(DService.PI);
        RaidFinderController.OnAttach += addon => SetupAddon((AtkUnitBase*)addon);
        RaidFinderController.OnDetach += addon => ResetAddon((AtkUnitBase*)addon);
        RaidFinderController.OnRefresh += addon => RefreshAddon((AtkUnitBase*)addon);
        RaidFinderController.Enable();

        SetContentsFinderSettingsInitHook ??= SetContentsFinderSettingsInitSig.GetHook<SetContentsFinderSettingsInitDelegate>(SetContentsFinderSettingsInitDetour);
        SetContentsFinderSettingsInitHook.Enable();

        FrameworkManager.Register(OnUpdate, throttleMS: 100);
    }

    protected override void Uninit()
    {
        FrameworkManager.Unregister(OnUpdate);

        ContentsFinderController?.Dispose();
        RaidFinderController?.Dispose();
        NativeController?.Dispose();

        SetContentsFinderSettingsInitHook?.Dispose();
        SetContentsFinderSettingsInitHook = null;
    }

    private static void SetupAddon(AtkUnitBase* addon)
    {
        if (addon == null) return;

        var addonName = addon->NameString;

        var defaultContainer = addon->GetNodeById(6);
        if (defaultContainer == null) return;
        defaultContainer->ToggleVisibility(false);

        addon->UldManager.UpdateDrawNodeList();

        var layoutNode = new HorizontalListNode
        {
            IsVisible = true,
            Size = new Vector2(defaultContainer->Width, defaultContainer->Height),
            Position = new Vector2(defaultContainer->X, defaultContainer->Y),
            ItemSpacing = 8f
        };

        LayoutNodes[addonName] = layoutNode;

        var attachNode = addon->GetNodeById(4);
        if (attachNode != null)
            NativeController!.AttachNode(layoutNode, attachNode);

        CreateDutyFinderButtons(addonName, addon, layoutNode);

        SetupLanguageButtonEvents(addonName, addon);

        UpdateIcons(addonName);
    }

    private static void ResetAddon(AtkUnitBase* addon)
    {
        if (addon == null) return;

        var addonName = addon->NameString;

        if (LanguageButtonNodes.TryGetValue(addonName, out var languageButtons))
        {
            foreach (var button in languageButtons)
                NativeController?.DetachNode(button);
            LanguageButtonNodes.Remove(addonName);
        }

        if (LayoutNodes.TryGetValue(addonName, out var layoutNode))
        {
            NativeController?.DetachNode(layoutNode);
            LayoutNodes.Remove(addonName);
        }

        DutyFinderButtonNodes.Remove(addonName);
        DutyFinderImageNodes.Remove(addonName);

        var vanillaIconContainer = addon->GetNodeById(6);
        if (vanillaIconContainer != null)
            vanillaIconContainer->ToggleVisibility(true);

        addon->UldManager.UpdateDrawNodeList();
        addon->UpdateCollisionNodeList(false);
    }

    private static void RefreshAddon(AtkUnitBase* addon)
    {
        if (addon == null) return;
        var addonName = addon->NameString;
        UpdateIcons(addonName);
    }

    private static void CreateDutyFinderButtons(string addonName, AtkUnitBase* addon, HorizontalListNode layoutNode)
    {
        var buttonNodes = new List<IconButtonNode>();
        var imageNodes = new List<IconImageNode>();

        for (var i = 0; i < DutyFinderSettingIcons.Count; i++)
        {
            var settingDetail = DutyFinderSettingIcons[i];
            var basedOn = addon->GetNodeById(7 + (uint)i);
            if (basedOn == null) continue;

            var button = new IconButtonNode
            {
                IsVisible = true,
                Size = new Vector2(basedOn->Width, basedOn->Height),
                Tooltip = LuminaWrapper.GetAddonText(settingDetail.GetTooltip()),
                OnClick = () => OnDutyFinderClick(settingDetail.Setting)
            };
            button.BackgroundNode.IsVisible = false;
            button.ImageNode.IsVisible = false;

            var image = new IconImageNode
            {
                IsVisible = true,
                Size = new Vector2(basedOn->Width, basedOn->Height),
                IconId = (uint)settingDetail.GetIcon()
            };

            NativeController!.AttachNode(image, button);
            layoutNode.AddNode(button);

            buttonNodes.Add(button);
            imageNodes.Add(image);
        }

        DutyFinderButtonNodes[addonName] = buttonNodes;
        DutyFinderImageNodes[addonName] = imageNodes;
    }

    private static void SetupLanguageButtonEvents(string addonName, AtkUnitBase* addon)
    {
        if (GetLanguageButtons() is not { Count: > 0 } langButtons)
            return;

        var languageButtonNodes = new List<TextButtonNode>();

        for (var i = 0; i < langButtons.Count; i++)
        {
            var langSetting = langButtons[i];
            var originalNode = addon->GetNodeById(17 + (uint)i);
            if (originalNode == null) continue;

            var languageButton = new TextButtonNode
            {
                IsVisible = true,
                Size = new Vector2(originalNode->Width, originalNode->Height),
                Position = new Vector2(originalNode->X, originalNode->Y),
                Label = "",
                OnClick = () => OnLanguageClick(langSetting.Setting)
            };

            languageButton.BackgroundNode.IsVisible = false;
            languageButton.LabelNode.IsVisible = false;

            var parentNode = originalNode->ParentNode;
            if (parentNode != null)
                NativeController!.AttachNode(languageButton, parentNode);

            languageButtonNodes.Add(languageButton);
        }

        LanguageButtonNodes[addonName] = languageButtonNodes;
        addon->UpdateCollisionNodeList(false);
    }

    private static void UpdateIcons(string addonName)
    {
        if (DutyFinderImageNodes.TryGetValue(addonName, out var imageNodes) &&
            DutyFinderButtonNodes.TryGetValue(addonName, out var buttonNodes))
        {
            for (var i = 0; i < Math.Min(DutyFinderSettingIcons.Count, imageNodes.Count); i++)
            {
                var settingDetail = DutyFinderSettingIcons[i];
                var value = GetCurrentSettingValue(settingDetail.Setting);

                imageNodes[i].IconId = (uint)settingDetail.GetIcon();
                imageNodes[i].IsVisible = true;

                if (settingDetail.Setting == DutyFinderSetting.LevelSync &&
                    GetCurrentSettingValue(DutyFinderSetting.UnrestrictedParty) == 0)
                    imageNodes[i].Color = imageNodes[i].Color.WithW(value != 0 ? 1 : 0.25f);
                else
                    imageNodes[i].Color = imageNodes[i].Color.WithW(value != 0 ? 1 : 0.5f);

                buttonNodes[i].Tooltip = LuminaWrapper.GetAddonText(settingDetail.GetTooltip());
            }
        }
    }

    private static void OnUpdate(IFramework _) =>
        LayoutNodes.Keys.ToList().ForEach(UpdateIcons);

    private static void OnDutyFinderClick(DutyFinderSetting setting)
    {
        ToggleSetting(setting);
        if (setting == DutyFinderSetting.LootRule)
        {
            foreach (var addonName in LayoutNodes.Keys)
                UpdateIcons(addonName);
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

    private static void ToggleSetting(DutyFinderSetting setting)
    {
        if (IsLangConfigReady() && setting is DutyFinderSetting.Ja or DutyFinderSetting.En or DutyFinderSetting.De
                or DutyFinderSetting.Fr)
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

    private static void SetContentsFinderSettingsInitDetour(byte* a1, nint a2) =>
        SetContentsFinderSettingsInitHook?.Original(a1, a2);

    private static void OnLanguageClick(DutyFinderSetting setting)
    {
        if (!IsLangConfigReady()) return;
        ToggleSetting(setting);
    }

    #endregion
    #region 自定类

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

    private record DutyFinderSettingDisplay(DutyFinderSetting Setting)
    {
        public DutyFinderSettingDisplay(DutyFinderSetting setting, int icon, uint tooltip) : this(setting)
        {
            GetIcon = () => icon;
            GetTooltip = () => tooltip;
        }

        public Func<int>? GetIcon { get; init; }
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
