using System.Numerics;
using DailyRoutines.Common.Info.Abstractions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class MultiTargetTracker : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title           = Lang.Get("MultiTargetTrackerTitle"),
        Description     = Lang.Get("MultiTargetTrackerDescription"),
        Category        = ModuleCategory.General,
        Author          = ["KirisameVanilla"],
        ModulesConflict = ["AutoHighlightFlagMarker"]
    };
    
    private static Config ModuleConfig = null!;

    private readonly TempTrackMenuItem      tempTrackItem;
    private readonly PermanentTrackMenuItem permanentTrackItem;

    private readonly HashSet<TrackPlayer> tempTrackedPlayers = [];

    public MultiTargetTracker()
    {
        tempTrackItem      = new(this);
        permanentTrackItem = new();
    }
    
    protected override void Init()
    {
        ModuleConfig = Config.Load(this) ?? new();

        PlayersManager.Instance().ReceivePlayersAround              += OnReceivePlayers;
        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        DService.Instance().ContextMenu.OnMenuOpened     += OnMenuOpen;
    }

    protected override void Uninit()
    {
        PlayersManager.Instance().ReceivePlayersAround -= OnReceivePlayers;
        FrameworkManager.Instance().Unreg(OnUpdate);

        DService.Instance().ContextMenu.OnMenuOpened     -= OnMenuOpen;
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;

        tempTrackedPlayers.Clear();
    }

    protected override void ConfigUI()
    {
        ImGui.TextUnformatted(Lang.Get("MultiTargetTracker-TempTrackHelp"));

        ImGui.Spacing();

        if (ModuleConfig.PermanentTrackedPlayers.Count == 0) return;

        using var table = ImRaii.Table("PermanentTrackedPlayers", 4);
        if (!table) return;

        ImGui.TableSetupColumn(Lang.Get("Name"),                                ImGuiTableColumnFlags.WidthStretch, 15);
        ImGui.TableSetupColumn(Lang.Get("MultiTargetTracker-LastSeenTime"),     ImGuiTableColumnFlags.WidthStretch, 10);
        ImGui.TableSetupColumn(Lang.Get("MultiTargetTracker-LastSeenLocation"), ImGuiTableColumnFlags.WidthStretch, 10);
        ImGui.TableSetupColumn(Lang.Get("Note"),                                ImGuiTableColumnFlags.WidthStretch, 15);

        ImGui.TableHeadersRow();

        for (var i = 0; i < ModuleConfig.PermanentTrackedPlayers.Count; i++)
        {
            var       player = ModuleConfig.PermanentTrackedPlayers[i];
            using var id     = ImRaii.PushId(player.ToString());

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.Selectable($"{player.Name}@{player.WorldName}");

            using (var context = ImRaii.ContextPopupItem("Context"))
            {
                if (context)
                {
                    if (ImGui.MenuItem(Lang.Get("Delete")))
                    {
                        ModuleConfig.PermanentTrackedPlayers.Remove(player);
                        ModuleConfig.Save(this);
                        continue;
                    }
                }
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(player.LastSeen.ToShortDateString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(player.LastSeenLocation);

            var note = player.Note;
            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputText("###Note", ref note, 256))
                ModuleConfig.PermanentTrackedPlayers[i].Note = note;
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);
        }
    }

    private void OnMenuOpen(IMenuOpenedArgs args)
    {
        if (!ShouldMenuOpen(args)) return;

        args.AddMenuItem(tempTrackItem.Get());
        args.AddMenuItem(permanentTrackItem.Get());
    }

    private void OnZoneChanged(ushort obj) =>
        tempTrackedPlayers.Clear();

    // 反正不会重复注册大胆造
    private void OnReceivePlayers(IReadOnlyList<IPlayerCharacter> characters)
    {
        if (characters.Count == 0)
            FrameworkManager.Instance().Unreg(OnUpdate);
        else
            FrameworkManager.Instance().Reg(OnUpdate);
    }

    private void OnUpdate(IFramework framework)
    {
        if (ModuleConfig.PermanentTrackedPlayers.Count == 0 && tempTrackedPlayers.Count == 0) return;

        if (PlayersManager.Instance().PlayersAroundCount == 0 || !GameState.IsLoggedIn)
        {
            FrameworkManager.Instance().Unreg(OnUpdate);
            return;
        }

        List<(ulong, Vector3)> validPlayers = new(8);

        foreach (var player in PlayersManager.Instance().PlayersAround)
        {
            if (validPlayers.Count >= 8) break;

            var isAdd = false;

            foreach (var trackPlayer in tempTrackedPlayers)
            {
                if (trackPlayer.ContentID != player.ContentID) continue;

                trackPlayer.LastSeen         = StandardTimeManager.Instance().Now;
                trackPlayer.LastSeenLocation = GameState.TerritoryTypeData.ExtractPlaceName();

                validPlayers.Add(new(player.ContentID, player.Position));
                isAdd = true;
            }

            if (isAdd) continue;

            foreach (var trackPlayer in ModuleConfig.PermanentTrackedPlayers)
            {
                if (trackPlayer.ContentID != player.ContentID) continue;

                trackPlayer.LastSeen         = StandardTimeManager.Instance().Now;
                trackPlayer.LastSeenLocation = GameState.TerritoryTypeData.ExtractPlaceName();

                validPlayers.Add(new(player.ContentID, player.Position));
            }
        }

        PlaceFieldMarkers(validPlayers);
    }

    private static unsafe void PlaceFieldMarkers(List<(ulong ContentID, Vector3 Position)> founds)
    {
        var counter = 0U;

        foreach (var found in founds)
        {
            if (counter > 8) break;

            MarkingController.Instance()->PlaceFieldMarkerLocal((FieldMarkerPoint)counter, found.Position);
            counter++;
        }
    }

    private static bool ShouldMenuOpen(IMenuOpenedArgs args)
    {
        if (args.Target is not MenuTargetDefault target) return false;
        return target.TargetContentId != 0 && target.TargetContentId != LocalPlayerState.ContentID;
    }

    private class Config : ModuleConfig
    {
        public List<TrackPlayer> PermanentTrackedPlayers = [];
    }

    public class TrackPlayer : IEquatable<TrackPlayer>
    {
        public unsafe TrackPlayer(IPlayerCharacter ipc)
        {
            var chara = ipc.ToStruct();
            ContentID = chara->ContentId;
            Name      = ipc.Name.TextValue;
            WorldName = ipc.HomeWorld.ValueNullable?.Name.ToString();
        }

        public TrackPlayer() { }

        public TrackPlayer(ulong contentID, string name, string world)
        {
            ContentID = contentID;
            Name      = name;
            WorldName = world;

            Added    = StandardTimeManager.Instance().Now;
            LastSeen = DateTime.MinValue;
        }

        public ulong  ContentID { get; set; }
        public string Name      { get; set; } = string.Empty;
        public string WorldName { get; set; } = string.Empty;

        public DateTime Added            { get; set; } = DateTime.MinValue;
        public DateTime LastSeen         { get; set; } = DateTime.MinValue;
        public string   LastSeenLocation { get; set; } = string.Empty;
        public string   Note             { get; set; } = string.Empty;

        public bool Equals(TrackPlayer? other) => ContentID == other.ContentID;

        public override bool Equals(object? obj)
        {
            if (obj is TrackPlayer otherPlayer) return Equals(otherPlayer);
            return false;
        }

        public override int GetHashCode() => ContentID.GetHashCode();

        public override string ToString() => $"{ContentID}";
    }

    private class TempTrackMenuItem(MultiTargetTracker module) : MenuItemBase
    {
        public override string Name       { get; protected set; } = $"{Lang.Get("MultiTargetTracker-TempTrack")}: {Lang.Get("Add")}/{Lang.Get("Delete")}";
        public override string Identifier { get; protected set; } = nameof(MultiTargetTracker);

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            if (args.Target is not MenuTargetDefault target) return;

            var data = new TrackPlayer
            (
                target.TargetContentId,
                target.TargetName,
                target.TargetHomeWorld.ValueNullable?.Name.ToString()
            );

            if (!module.tempTrackedPlayers.Add(data))
            {
                module.tempTrackedPlayers.Remove(data);
                NotifyHelper.Instance().NotificationSuccess(Lang.Get("Deleted"));
            }
            else
                NotifyHelper.Instance().NotificationSuccess(Lang.Get("Added"));
        }
    }

    private class PermanentTrackMenuItem : MenuItemBase
    {
        public override string Name       { get; protected set; } = $"{Lang.Get("MultiTargetTracker-PermanentTrack")}: {Lang.Get("Add")}/{Lang.Get("Delete")}";
        public override string Identifier { get; protected set; } = nameof(MultiTargetTracker);

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            var target = args.Target as MenuTargetDefault;
            if (IPlayerCharacter.Create(target.TargetObject.Address) is not { } player ||
                string.IsNullOrEmpty(player.Name.ToString())                           ||
                player.ClassJob.RowId == 0)
                return;

            if (ModuleConfig.PermanentTrackedPlayers.Contains(new(player)))
            {
                ModuleConfig.PermanentTrackedPlayers.Remove(new(player));
                NotifyHelper.Instance().NotificationSuccess(Lang.Get("Deleted"));
            }
            else
            {
                ModuleConfig.PermanentTrackedPlayers.Add(new(player));
                NotifyHelper.Instance().NotificationSuccess(Lang.Get("Added"));
            }

            ModuleConfig.Save(ModuleManager.Instance().GetModule<MultiTargetTracker>());
        }
    }
}
