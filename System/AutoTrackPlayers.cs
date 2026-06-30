using System.Numerics;
using DailyRoutines.Common.Info.Abstractions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Manager;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoTrackPlayers : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title           = Lang.Get("AutoTrackPlayersTitle"),
        Description     = Lang.Get("AutoTrackPlayersDescription"),
        Category        = ModuleCategory.System,
        Author          = ["KirisameVanilla"],
        ModulesConflict = ["AutoHighlightFlagMarker"]
    };
    
    private readonly Dictionary<ulong, (FieldMarkerPoint? Marker, string Name, uint World)> trackedPlayers = [];
    
    private readonly ContextMenuItem contextMenuItem;

    private AddonDRAutoTrackPlayers? trackAddon;

    public AutoTrackPlayers() =>
        contextMenuItem = new(this);

    protected override void Init()
    {
        PlayersManager.Instance().ReceivePlayersAround   += OnReceivePlayers;
        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        DService.Instance().ContextMenu.OnMenuOpened     += OnMenuOpen;

        trackAddon = new(this)
        {
            InternalName = "DRAutoTrackPlayers",
            Title        = Lang.Get("AutoTrackPlayersTitle"),
            Size         = new(400f, 300f)
        };

        CommandManager.Instance().AddCommand
        (
            COMMAND,
            new(OnCommand) { HelpMessage = Lang.Get("AutoTrackPlayers-CommandHelp") }
        );
    }

    protected override void Uninit()
    {
        PlayersManager.Instance().ReceivePlayersAround -= OnReceivePlayers;
        FrameworkManager.Instance().Unreg(OnUpdate);

        DService.Instance().ContextMenu.OnMenuOpened     -= OnMenuOpen;
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;

        CommandManager.Instance().RemoveCommand(COMMAND);

        trackedPlayers.Clear();

        trackAddon?.Dispose();
        trackAddon = null;
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Command")}");

        using (ImRaii.PushIndent())
            ImGui.TextUnformatted($"{COMMAND} → {Lang.Get("AutoTrackPlayers-CommandHelp")}");
    }

    private void OnCommand(string command, string arguments) =>
        trackAddon.Toggle();

    private void OnMenuOpen(IMenuOpenedArgs args)
    {
        if (!contextMenuItem.IsDisplay(args)) return;
        args.AddMenuItem(contextMenuItem.Get());
    }

    private void OnZoneChanged(uint territoryType) =>
        trackedPlayers.Clear();

    private void OnReceivePlayers(IReadOnlyList<IPlayerCharacter> characters)
    {
        if (characters.Count == 0)
            FrameworkManager.Instance().Unreg(OnUpdate);
        else
            FrameworkManager.Instance().Reg(OnUpdate);
    }

    private unsafe void OnUpdate(IFramework framework)
    {
        if (trackedPlayers.Count == 0 || !GameState.IsLoggedIn)
        {
            FrameworkManager.Instance().Unreg(OnUpdate);
            return;
        }

        if (PlayersManager.Instance().PlayersAroundCount == 0)
        {
            FrameworkManager.Instance().Unreg(OnUpdate);
            return;
        }

        List<(ulong ContentID, FieldMarkerPoint Marker, Vector3 Position)> validPlayers = [];

        foreach (var kvp in trackedPlayers)
        {
            if (kvp.Value.Marker == null) continue;

            var player = PlayersManager.Instance().PlayersAround
                                       .FirstOrDefault(p => p.ContentID == kvp.Key);

            if (player != null)
                validPlayers.Add((kvp.Key, kvp.Value.Marker.GetValueOrDefault(), player.Position));
        }

        foreach (var found in validPlayers)
            MarkingController.Instance()->PlaceFieldMarkerLocal(found.Marker, found.Position);
    }

    private class ContextMenuItem
    (
        AutoTrackPlayers module
    ) : MenuItemBase
    {
        public override string Name { get; protected set; } = Lang.Get("AutoTrackPlayers-Track");

        public override string Identifier { get; protected set; } = nameof(AutoTrackPlayers);

        public override bool IsDisplay(IMenuOpenedArgs args)
        {
            if (args.Target is not MenuTargetDefault target) return false;
            
            var isTracked = module.trackedPlayers.ContainsKey(target.TargetContentId);
            Name = isTracked
                       ? $"{Lang.Get("AutoTrackPlayers-Track")}: {Lang.Get("Delete")}"
                       : $"{Lang.Get("AutoTrackPlayers-Track")}: {Lang.Get("Add")}";
            
            return target.TargetContentId != 0 && 
                   target.TargetContentId != LocalPlayerState.ContentID;
        }

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            if (args.Target is not MenuTargetDefault target) return;

            if (!module.trackedPlayers.Remove(target.TargetContentId))
                module.trackedPlayers.Add(target.TargetContentId, (null, target.TargetName, target.TargetHomeWorld.RowId));

            if (!module.trackAddon.IsOpen)
                module.trackAddon.Open();
            else
                module.trackAddon.RebuildList();
        }
    }

    private class AddonDRAutoTrackPlayers(AutoTrackPlayers module) : NativeAddon
    {
        private ScrollingNode<VerticalListNode> scrollArea    = null!;
        private TextNode                        emptyHintText = null!;
        private List<PlayerEntryNode>           playerEntries = [];

        private bool isRebuildingList;

        protected override unsafe void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
        {
            scrollArea = new()
            {
                Position          = ContentStartPosition,
                Size              = ContentSize,
                AutoHideScrollBar = true,
            };
            scrollArea.ContentNode.FitContents = true;
            scrollArea.AttachNode(this);

            emptyHintText = new()
            {
                String        = Lang.Get("AutoTrackPlayers-EmptyHint"),
                Size          = ContentSize,
                Position      = new(0, 100),
                AlignmentType = AlignmentType.Center,
                FontSize      = 12,
                IsVisible     = true
            };
            scrollArea.ContentNode.AddNode(emptyHintText);

            RebuildList();
        }

        public void RebuildList()
        {
            if (this is not { IsOpen: true }) return;
            if (isRebuildingList) return;
            
            isRebuildingList = true;
            
            try
            {
                emptyHintText.IsVisible = module.trackedPlayers.Count == 0;

                foreach (var entry in playerEntries)
                {
                    entry.DetachNode();
                    entry.Dispose();
                }
                playerEntries.Clear();

                foreach (var (contentID, (marker, name, world)) in module.trackedPlayers)
                {
                    var entry = new PlayerEntryNode
                    (
                        scrollArea,
                        marker,
                        contentID,
                        name,
                        LuminaWrapper.GetWorldName(world),
                        x => module.trackedPlayers[contentID] = new(x, name, world),
                        () =>
                        {
                            module.trackedPlayers.Remove(contentID);
                            RebuildList();
                        },
                        GetAvailableMarkers(contentID)
                    );
                    playerEntries.Add(entry);
                }

                scrollArea.ContentNode.RecalculateLayout();
            }
            finally
            {
                isRebuildingList = false;
            }
        }

        private List<string> GetAvailableMarkers(ulong currentContentID)
        {
            var usedMarkers = module.trackedPlayers
                                    .Where(kvp => kvp.Key != currentContentID && kvp.Value.Marker.HasValue)
                                    .Select(kvp => kvp.Value.Marker.GetValueOrDefault())
                                    .ToHashSet();

            var options = new List<string> { Lang.Get("AutoTrackPlayers-NoMarker") };
            for (var i = 0; i < 8; i++)
            {
                var marker = (FieldMarkerPoint)i;
                if (!usedMarkers.Contains(marker))
                    options.Add(marker.ToString());
            }

            return options;
        }

        private class PlayerEntryNode : HorizontalListNode
        {
            public PlayerEntryNode
            (
                ScrollingNode<VerticalListNode> parent,
                FieldMarkerPoint?               currentMarker,
                ulong                           contentID,
                string                          name,
                string                          world,
                Action<FieldMarkerPoint?>       onMarkerChange,
                Action                          onDelete,
                List<string>                    availableOptions
            )
            {
                ItemSpacing = 5;
                Size        = new(parent.Width, 30);

                var markerDropdown = new StringDropDownNode
                {
                    Size           = new(120, 30),
                    Options        = availableOptions,
                    SelectedOption = currentMarker?.ToString() ?? Lang.Get("AutoTrackPlayers-NoMarker"),
                    OnOptionSelected = selected =>
                    {
                        if (selected == Lang.Get("AutoTrackPlayers-NoMarker"))
                            onMarkerChange(null);
                        else if (Enum.TryParse<FieldMarkerPoint>(selected, out var marker))
                            onMarkerChange(marker);
                    }
                };

                using var rented = new RentedSeStringBuilder();
                var displayName = string.IsNullOrEmpty(world)
                                      ? contentID.ToString()
                                      : rented.Builder
                                              .Append(name)
                                              .AppendIcon((uint)BitmapFontIcon.CrossWorld)
                                              .Append(world)
                                              .ToReadOnlySeString();
                
                var nameText = new TextNode
                {
                    String        = displayName,
                    Size          = new(200, 30),
                    AlignmentType = AlignmentType.Left,
                    FontSize      = 14
                };

                var nameButton = new TextButtonNode
                {
                    String = string.Empty,
                    Size   = new(200, 30),
                    OnClick = () =>
                    {
                        var player = PlayersManager.Instance().PlayersAround.FirstOrDefault(x => x.ContentID == contentID);
                        if (player == null) return;

                        TargetManager.Target = player;
                    }
                };
                nameButton.BackgroundNode.IsVisible = false;

                var deleteButton = new IconButtonNode
                {
                    Size      = new(24),
                    IconId    = 61502,
                    IsVisible = true,
                    OnClick   = onDelete
                };

                AddNode(markerDropdown);
                
                AddNode(nameText);
                nameButton.AttachNode(nameText);
                
                AddNode(deleteButton);
                
                parent.ContentNode.AddNode(this);
            }
        }
    }

    private const string COMMAND = "/pdrtrack";
}
