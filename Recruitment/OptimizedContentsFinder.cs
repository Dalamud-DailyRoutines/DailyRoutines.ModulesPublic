using System.Numerics;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Controllers;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;
using KamiToolKit.Timelines;
using OmenTools.Interop.Game;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using AgentContentsFinder = FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent.AgentContentsFinder;
using AddonContentsFinder = FFXIVClientStructs.Expansion.FFXIV.Client.UI.Addon.AddonContentsFinder;
using UIContentsFinder = FFXIVClientStructs.FFXIV.Client.Game.UI.ContentsFinder;
using UIInstanceContent = FFXIVClientStructs.FFXIV.Client.Game.UI.InstanceContent;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedContentsFinder : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("OptimizedContentsFinderTitle"),
        Description = Lang.Get("OptimizedContentsFinderDescription"),
        Category    = ModuleCategory.Recruitment,
        PreviewImageURL =
        [
            "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/OptimizedContentsFinder/preview-1.webp"
        ]
    };

    public override ModuleBehaviour Behaviour { get; } = new()
    {
        AllDefaultEnabled = true
    };

    public override string ModuleName => nameof(OptimizedContentsFinder);
    
    private static readonly CompSig ApplySettingsSig =
        new("4C 8B DC 55 57 41 56 49 8D 6B ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 45 ?? 80 79");
    private delegate void                  ApplySettingsDelegate(byte* data, UIModule* module);
    private          ApplySettingsDelegate ApplySettings = null!;
    
    private MemoryPatch PrerequisiteNamesPatch = null!;
    
    private NativeListController<AddonContentsFinder> listController = null!;

    private readonly Dictionary<string, FinderNodes> finderNodes = [];

    private readonly Dictionary<uint, EntryNodes> entryNodes = [];
    
    private uint registrationIcon;

    private string registrationTooltip = string.Empty;

    private byte enteredRoulette;

    private uint enteredContent;

    protected override void Init()
    {
        registrationIcon    = 60081;

        ApplySettings = ApplySettingsSig.GetDelegate<ApplySettingsDelegate>();

        PrerequisiteNamesPatch = new("74 ?? 48 8B 13 48 8B CB FF 52 ?? BA ?? ?? ?? ?? 49 8B CF", [0x90, 0x90]);
        PrerequisiteNamesPatch.Enable();

        listController = new()
        {
            AddonName        = "ContentsFinder",
            GetPopulatorNode = addon => addon->DutyList->GetComponentItemRendererById(6),
            ShouldModifyElement = (addon, item) =>
            {
                var entry = addon->DutyList->GetItem(item.ItemIndex);
                return entry                       != null &&
                       entry->UIntValues.LongCount > 1     &&
                       !entry->Type.IsSetAny(TreeListItemType.Group, TreeListItemType.SectionHeader);
            },
            UpdateElement = UpdateEntry,
            ResetElement  = (_, item) => ResetEntry(item)
        };
        listController.Enable();

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup,   ["ContentsFinder", "RaidFinder"], OnAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostRefresh, ["ContentsFinder", "RaidFinder"], OnAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostShow,    ["ContentsFinder", "RaidFinder"], OnAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize, ["ContentsFinder", "RaidFinder"], OnAddon);

        IDutyState.Instance().DutyCompleted      += OnDutyCompleted;
        IClientState.Instance().TerritoryChanged += OnTerritoryChanged;

        if (ContentsFinder != null)
        {
            SetupFinder(ContentsFinder);
            
            var agent = AgentContentsFinder.Instance();
            agent->ListChanged = true;
            agent->UpdateAddon();
        }

        if (RaidFinder != null)
            SetupFinder(RaidFinder);
    }

    protected override void Uninit()
    {
        IAddonLifecycle.Instance().UnregisterListener(OnAddon);

        IDutyState.Instance().DutyCompleted      -= OnDutyCompleted;
        IClientState.Instance().TerritoryChanged -= OnTerritoryChanged;

        if (ContentsFinder != null)
        {
            foreach (var entry in ((AddonContentsFinder*)ContentsFinder)->DutyList->Items)
            {
                var renderer = entry.Value->Renderer;
                if (renderer != null && entryNodes.TryGetValue(renderer->OwnerNode->NodeId, out var nodes))
                    renderer->GetTextNodeById(6)->SetWidth(nodes.NameWidth);
            }
        }
        
        listController.Dispose();

        foreach (var nodes in entryNodes.Values)
            nodes.Layout.Dispose();
        entryNodes.Clear();

        foreach (var nodes in finderNodes.Values)
            nodes.Dispose();
        finderNodes.Clear();

        if (ContentsFinder != null)
            ContentsFinder->GetNodeById(6)->ToggleVisibility(true);

        if (RaidFinder != null)
            RaidFinder->GetNodeById(6)->ToggleVisibility(true);
    }

    #region 事件

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PreFinalize:
                if (finderNodes.Remove(args.AddonName, out var nodes))
                    nodes.Dispose();

                if (args.AddonName == "ContentsFinder")
                {
                    foreach (var entry in entryNodes.Values)
                        entry.Layout.Dispose();

                    entryNodes.Clear();
                }
                
                break;
            
            case AddonEvent.PostSetup:
                SetupFinder(args.Addon.ToStruct());
                break;
            
            case AddonEvent.PostShow:
            case AddonEvent.PostRefresh:
                RefreshSettings(args.Addon.ToStruct());
                break;
        }
        
    }

    private void OnTerritoryChanged(uint territory)
    {
        if (GameState.ContentFinderCondition == 0)
        {
            enteredContent  = 0;
            enteredRoulette = 0;
        }
        else
        {
            enteredRoulette = UIContentsFinder.Instance()->GetQueueInfo()->QueuedContentRouletteId;
            enteredContent  = GameState.ContentFinderCondition;
        }
    }

    private void OnDutyCompleted(IDutyStateEventArgs args)
    {
        var content = GameState.ContentFinderCondition;
        if (content == 0 || content != enteredContent)
            return;

        var agent = AgentContentsFinder.Instance();
        for (var index = (int)agent->SelectedContent.LongCount - 1; index >= 0; index--)
        {
            var selected = agent->SelectedContent[index];

            if (selected.ContentType == ContentsType.Roulette)
            {
                if (enteredRoulette == 0 || selected.ID != enteredRoulette)
                    continue;
            }
            else if (selected.ID != content || agent->SelectedContent.LongCount < 2)
                continue;

            agent->SelectedContent.RemoveAt(index);
        }

        agent->HasRouletteSelected = false;

        foreach (var selected in agent->SelectedContent)
        {
            if (selected.ContentType == ContentsType.Roulette)
                agent->HasRouletteSelected = true;
        }

        agent->ListChanged    = true;
        agent->DetailsChanged = true;
        agent->UpdateAddon();
        enteredRoulette = 0;
    }

    #endregion

    private void SetupFinder(AtkUnitBase* addon)
    {
        if (finderNodes.ContainsKey(addon->NameString))
            return;

        var container         = addon->GetNodeById(30);
        var settingsContainer = addon->GetNodeById(6);
        var settingsParent    = addon->GetNodeById(4);
        if (container == null || settingsContainer == null || settingsParent == null)
            return;

        var buttons = new HorizontalListNode
        {
            Size        = new(container->Width, container->Height),
            IsVisible   = true,
            ItemSpacing = 0
        };

        for (var index = 0; index < 2; index++)
        {
            var button = new TextButtonNode
            {
                Size      = new(container->Width / 2f, container->Height),
                IsVisible = true,
                String    = string.Empty,
                OnClick   = OpenClassSelection
            };
            button.BackgroundNode.IsVisible = false;
            buttons.AddNode(button);
        }

        buttons.AttachNode(container);
        var nodes = new FinderNodes
        {
            Jobs = buttons,
            Settings = new()
            {
                Size        = new(settingsContainer->Width, settingsContainer->Height),
                Position    = new(settingsContainer->X - 5, settingsContainer->Y - 5),
                IsVisible   = true,
                ItemSpacing = 0
            }
        };

        foreach (var setting in SETTINGS)
        {
            var button = new IconButtonNode
            {
                Size         = new(32),
                InnerPadding = new(8),
                IsVisible    = true
            };
            button.OnClick = () =>
            {
                ToggleSetting(setting);

                if (setting == 4)
                {
                    button.HideTooltip();
                    button.ShowTooltip();
                }
            };
            var icon = CreateButtonIcon(button, 24);
            nodes.Settings.AddNode(button);
            nodes.SettingButtons.Add(button);
            nodes.SettingIcons.Add(icon);
        }

        nodes.Settings.AttachNode(settingsContainer, NodePosition.AfterTarget);
        settingsContainer->ToggleVisibility(false);

        if (GameState.IsGL)
        {
            for (var index = 0; index < LANGUAGE_CONFIGS.Length; index++)
            {
                var language = index;
                var label    = addon->GetNodeById(17 + (uint)index);
                var button = new TextButtonNode
                {
                    Size        = new(28),
                    Position    = new(label->X - 7, label->Y - 5),
                    IsVisible   = true,
                    String      = string.Empty,
                    TextTooltip = LuminaWrapper.GetAddonText((uint)(4266 + index)),
                    OnClick     = () => ToggleSetting(language)
                };
                button.BackgroundNode.IsVisible = false;
                button.AttachNode(label->ParentNode);
                nodes.Languages.Add(button);
            }
        }

        finderNodes.Add(addon->NameString, nodes);
        RefreshSettings(addon);
        
        return;
        
        static void OpenClassSelection()
        {
            AgentStatus.Instance()->TabIndex = 2;
            UIModule.Instance()->ExecuteMainCommand(2);
        }
    }
    
    private void RefreshSettings(AtkUnitBase* addon)
    {
        if (!finderNodes.TryGetValue(addon->NameString, out var nodes))
            return;

        addon->GetNodeById(6)->ToggleVisibility(false);
        Span<byte> settings = stackalloc byte[12];
        settings.Clear();
        LoadSettingsToContentsFinder(settings);

        for (var index = 0; index < SETTINGS.Length; index++)
        {
            var setting = SETTINGS[index];
            var button  = nodes.SettingButtons[index];
            var icon    = nodes.SettingIcons[index];
            icon.IconId = SETTING_ICONS[index];
            var tooltip = SETTING_TOOLTIPS[index];

            if (setting == 4)
            {
                icon.IconId = settings[4] == 2 ?
                                  60646u :
                                  60645u;
                tooltip += settings[4];
            }

            button.TextTooltip = LuminaWrapper.GetAddonText(tooltip);
            icon.Alpha = settings[setting] == 0 ?
                             0.5f :
                             1;
        }

        registrationIcon = 60081;
        if (settings[10] != 0)
            registrationIcon = 60648;
        else if (settings[7] != 0)
            registrationIcon = 60649;
        else if (settings[6] != 0)
            registrationIcon = 60641;

        var conditions = new List<string>();

        for (var index = 0; index < SETTINGS.Length; index++)
        {
            var setting = SETTINGS[index];
            if (settings[setting] == 0)
                continue;

            var tooltip = SETTING_TOOLTIPS[index];
            if (setting == 4)
                tooltip += settings[4];

            conditions.Add($"  \ue06f {LuminaWrapper.GetAddonText(tooltip)}");
        }

        registrationTooltip = conditions.Count == 0 ?
                                  Lang.Get("OptimizedContentsFinder-Register") :
                                  Lang.Get
                                  (
                                      "OptimizedContentsFinder-RegisterWithConditions",
                                      new Dictionary<string, object>
                                      {
                                          ["conditions"] = string.Join('\n', conditions)
                                      }
                                  );

        var queued = ICondition.Instance()[ConditionFlag.InDutyQueue];

        foreach (var entry in entryNodes.Values)
        {
            entry.Icon.IconId = queued ?
                                    61502u :
                                    registrationIcon;
            entry.Button.TextTooltip = queued ?
                                           LuminaWrapper.GetAddonText(2506) :
                                           registrationTooltip;
        }
    }
    
    private void UpdateEntry(AddonContentsFinder* addon, ListItemData item)
    {
        var agent        = AgentContentsFinder.Instance();
        var entry        = addon->DutyList->GetItem(item.ItemIndex);
        var contentIndex = entry->UIntValues[1];

        if (contentIndex == 0 || contentIndex > agent->ContentList.LongCount)
        {
            ResetEntry(item);
            return;
        }

        var content = agent->ContentList[(int)contentIndex - 1].Value;
        var rewardReceived = content->ID.ContentType == ContentsType.Roulette
                             && UIInstanceContent.Instance()->IsRouletteComplete((byte)content->ID.ID);

        if (content->UnavailableReasons != 0
            && !rewardReceived)
        {
            ResetEntry(item);
            return;
        }

        var renderer = item.TryGetListItemRenderer();

        if (!entryNodes.TryGetValue(item.NodeId, out var nodes))
        {
            nodes = new()
            {
                NameWidth = renderer->GetTextNodeById(6)->Width,
                Layout = new()
                {
                    Size        = new(48, 24),
                    IsVisible   = true,
                    ItemSpacing = 0
                },
                Button = new()
                {
                    Size         = new(24),
                    InnerPadding = new(8),
                    IsVisible    = true
                },
                Reward = new()
                {
                    Size        = new(24),
                    Position    = new(24, 0),
                    TextTooltip = LuminaWrapper.GetAddonText(102588),
                    WrapMode    = WrapMode.Stretch
                }
            };

            nodes.Reward.AddPart
            (
                new Part
                {
                    Size               = new(48),
                    TexturePath        = "ui/uld/Journal_Detail_hr1.tex",
                    TextureCoordinates = new(472, 0),
                    Id                 = 1
                }
            );
            nodes.Reward.PartId = 1;

            nodes.Icon           = CreateButtonIcon(nodes.Button, 20);
            nodes.Button.OnClick = () => RegisterContent(nodes);
            nodes.Layout.AddNode(nodes.Button);
            nodes.Reward.AttachNode(nodes.Layout);
            nodes.Layout.AttachNode(renderer->OwnerNode, NodePosition.AsLastChild);
            listController.AddNode(item, nodes.Button.CollisionNode);
            listController.AddNode(item, nodes.Reward);
            entryNodes.Add(item.NodeId, nodes);
        }

        var name = renderer->GetTextNodeById(6);
        var sync = renderer->GetNodeById(14);
        nodes.Layout.X = sync->X - nodes.Layout.Width;
        name->SetWidth((ushort)(nodes.Layout.X - name->X));
        nodes.Content          = content->ID;
        nodes.Layout.IsVisible = true;
        nodes.Button.IsVisible = content->UnavailableReasons == 0;
        nodes.Reward.IsVisible = rewardReceived;

        var queued = ICondition.Instance()[ConditionFlag.InDutyQueue];
        nodes.Icon.IconId = queued ?
                                61502u :
                                registrationIcon;
        nodes.Button.TextTooltip = queued ?
                                       LuminaWrapper.GetAddonText(2506) :
                                       registrationTooltip;
        
        return;
        
        static void RegisterContent(EntryNodes nodes)
        {
            if (ICondition.Instance()[ConditionFlag.InDutyQueue])
            {
                ContentsFinderHelper.CancelDutyApply();
                return;
            }

            var options = ContentsFinderOption.Get();

            if (nodes.Content.ContentType == ContentsType.Roulette)
                ContentsFinderHelper.RequestDutyRoulette((ushort)nodes.Content.ID, options);
            else
                ContentsFinderHelper.RequestDutyNormal(nodes.Content.ID, options);
        }
    }
    
    private void ResetEntry(ListItemData item)
    {
        if (!entryNodes.TryGetValue(item.NodeId, out var nodes))
            return;

        nodes.Layout.IsVisible = false;
        var name = item.TryGetListItemRenderer()->GetTextNodeById(6);
        if (name != null)
            name->SetWidth(nodes.NameWidth);
    }
    
    private static IconImageNode CreateButtonIcon(IconButtonNode parent, float size)
    {
        parent.BackgroundNode.IsVisible = false;
        parent.ImageNode.IsVisible      = false;
        
        var position = (parent.Size - new Vector2(size)) / 2;
        
        var icon = new IconImageNode
        {
            Size       = new(size),
            Position   = position,
            FitTexture = true,
            IsVisible  = true
        };
        
        icon.AddTimeline
        (
            new TimelineBuilder()
                .AddFrameSetWithFrame(1, 10, 1, position, addColor: Vector3.Zero)
                .BeginFrameSet(11, 17)
                .AddFrame(11, position, addColor: Vector3.Zero)
                .AddFrame(13, position, addColor: new Vector3(16))
                .EndFrameSet()
                .AddFrameSetWithFrame(18, 26, 18, position + new Vector2(0, 1), addColor: new Vector3(16))
                .AddFrameSetWithFrame(27, 36, 27, position,                     addColor: Vector3.Zero)
                .AddFrameSetWithFrame(37, 46, 37, position,                     addColor: new Vector3(16))
                .BeginFrameSet(47, 53)
                .AddFrame(47, position, addColor: new Vector3(16))
                .AddFrame(53, position, addColor: Vector3.Zero)
                .EndFrameSet()
                .Build()
        );
        
        icon.AttachNode(parent);
        return icon;
    }

    private static void LoadSettingsToContentsFinder(Span<byte> settings)
    {
        if (GameState.IsGL)
        {
            for (var index = 0; index < LANGUAGE_CONFIGS.Length; index++)
                settings[index] = (byte)IGameConfig.Instance().UiConfig.GetUInt(LANGUAGE_CONFIGS[index]);
        }

        var state = UIContentsFinder.Instance();
        settings[4]  = (byte)state->LootRules;
        settings[5]  = (byte)IGameConfig.Instance().UiConfig.GetUInt("ContentsFinderSupplyEnable");
        settings[6]  = Convert.ToByte(state->IsUnrestrictedParty);
        settings[7]  = Convert.ToByte(state->IsLevelSync);
        settings[8]  = Convert.ToByte(state->IsMinimalIL);
        settings[9]  = Convert.ToByte(state->IsSilenceEcho);
        settings[10] = Convert.ToByte(state->IsExplorerMode);
        settings[11] = Convert.ToByte(state->IsLimitedLevelingRoulette);
    }

    private void ToggleSetting(int setting)
    {
        Span<byte> settings = stackalloc byte[27];
        settings.Clear();
        LoadSettingsToContentsFinder(settings);
        settings[..12].CopyTo(settings[12..]);

        if (setting < 4 && settings[setting] != 0 && settings[0] + settings[1] + settings[2] + settings[3] == 1)
            return;

        settings[setting] = setting == 4 ?
                                (byte)((settings[setting] + 1) % 3) :
                                (byte)(1 - settings[setting]);
        settings[26] = 1;

        fixed (byte* data = settings)
            ApplySettings(data, UIModule.Instance());

        if (ContentsFinder != null)
            RefreshSettings(ContentsFinder);

        if (RaidFinder != null)
            RefreshSettings(RaidFinder);
    }
    
    private sealed class FinderNodes : IDisposable
    {
        public HorizontalListNode Jobs { get; init; } = null!;

        public HorizontalListNode Settings { get; init; } = null!;

        public List<IconButtonNode> SettingButtons { get; init; } = [];

        public List<IconImageNode> SettingIcons { get; init; } = [];

        public List<TextButtonNode> Languages { get; init; } = [];

        public void Dispose()
        {
            Jobs.Dispose();
            Settings.Dispose();

            foreach (var language in Languages)
                language.Dispose();
        }
    }

    private sealed class EntryNodes
    {
        public HorizontalListNode Layout { get; init; } = null!;

        public IconButtonNode Button { get; init; } = null!;
        
        public SimpleImageNode Reward { get; init; } = null!;

        public IconImageNode Icon { get; set; } = null!;
        
        public ushort NameWidth { get; init; }

        public AgentContentsFinder.ContentsID Content { get; set; }
    }

    #region 常量

    private static readonly int[] SETTINGS = [5, 6, 7, 8, 9, 10, 11, 4];

    private static readonly uint[] SETTING_ICONS = [60644, 60641, 60649, 60642, 60647, 60648, 60640, 60645];

    private static readonly uint[] SETTING_TOOLTIPS = [2519, 10008, 12696, 10010, 12691, 13038, 13030, 10022];

    private static readonly string[] LANGUAGE_CONFIGS =
    [
        "ContentsFinderUseLangTypeJA",
        "ContentsFinderUseLangTypeEN",
        "ContentsFinderUseLangTypeDE",
        "ContentsFinderUseLangTypeFR"
    ];

    #endregion
}
