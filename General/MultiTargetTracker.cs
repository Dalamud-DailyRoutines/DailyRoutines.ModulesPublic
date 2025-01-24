using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.GeneratedSheets;

namespace DailyRoutines.Modules;

public class MultiTargetTracker : DailyModuleBase
{
    private static Config ModuleConfig = null!;

    private static readonly TempTrackMenuItem  TempTrackItem  = new();
    private static readonly PermanentTrackMenuItem PermanentTrackItem = new();

    public static HashSet<TrackPlayer> TempTrackedPlayers = [];

    #region ModuleBase

    public override ModuleInfo Info => new()
    {
        Title           = GetLoc("MultiTargetTrackerTitle"),
        Description     = GetLoc("MultiTargetTrackerDescription"),
        Category        = ModuleCategories.General,
        Author          = ["KirisameVanilla"],
        ModulesConflict = ["AutoHighlightFlagMarker"],
    };

    private class Config : ModuleConfiguration
    {
        public List<TrackPlayer> PermanentTrackedPlayers = [];
    }

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        FrameworkManager.Register(true, OnUpdate);
        DService.ClientState.TerritoryChanged += OnZoneChanged;
        DService.ContextMenu.OnMenuOpened     += OnMenuOpen;
    }

    public override void Uninit()
    {
        DService.ContextMenu.OnMenuOpened -= OnMenuOpen;
        FrameworkManager.Unregister(OnUpdate);
        DService.ClientState.TerritoryChanged -= OnZoneChanged;

        TempTrackedPlayers.Clear();
    }

    #endregion

    #region UI

    public override void ConfigUI()
    {
        ImGui.Text(GetLoc("MultiTargetTracker-TempTrackHelp"));
        
        ImGui.Spacing();
        
        if (ModuleConfig.PermanentTrackedPlayers.Count == 0) return;
        
        using var table = ImRaii.Table("PermanentTrackedPlayers", 4);
        if (!table) return;

        ImGui.TableSetupColumn(GetLoc("Name"),                                ImGuiTableColumnFlags.WidthStretch, 15);
        ImGui.TableSetupColumn(GetLoc("MultiTargetTracker-LastSeenTime"),     ImGuiTableColumnFlags.WidthStretch, 10);
        ImGui.TableSetupColumn(GetLoc("MultiTargetTracker-LastSeenLocation"), ImGuiTableColumnFlags.WidthStretch, 10);
        ImGui.TableSetupColumn(GetLoc("Note"),                                ImGuiTableColumnFlags.WidthStretch, 15);

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
                    if (ImGui.MenuItem(GetLoc("Delete")))
                    {
                        ModuleConfig.PermanentTrackedPlayers.Remove(player);
                        ModuleConfig.Save(this);
                        continue;
                    }
                }
            }

            ImGui.TableNextColumn();
            ImGui.Text(player.LastSeen.ToShortDateString());

            ImGui.TableNextColumn();
            ImGui.Text(player.LastSeenLocation);

            var note = player.Note;
            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputText("###Note", ref note, 256))
                ModuleConfig.PermanentTrackedPlayers[i].Note = note;
            if (ImGui.IsItemDeactivatedAfterEdit())
                SaveConfig(ModuleConfig);
        }
    }

    #endregion

    private static void OnMenuOpen(IMenuOpenedArgs args)
    {
        if (!ShouldMenuOpen(args)) return;

        args.AddMenuItem(TempTrackItem.Get());
        args.AddMenuItem(PermanentTrackItem.Get());
    }

    private void OnZoneChanged(ushort obj)
    {
        TempTrackedPlayers.Clear();
    }

    private unsafe void OnUpdate(IFramework framework)
    {
        if (!Throttler.Throttle("MultiTargetTracker_Update", 1_000)) return;
        if (BetweenAreas || !IsScreenReady() || DService.ClientState.TerritoryType == 0) return;
        if (ModuleConfig.PermanentTrackedPlayers.Count == 0 && TempTrackedPlayers.Count == 0) return;

        var currentZoneData = LuminaCache.GetRow<TerritoryType>(DService.ClientState.TerritoryType);

        Dictionary<ulong, Vector3> validPlayers = [];

        foreach (var player in DService.ObjectTable)
        {
            if (validPlayers.Count >= 8) break;

            if (player.ObjectKind != ObjectKind.Player || player is not IPlayerCharacter) continue;

            var playerStruct = (Character*)player.Address;
            if (playerStruct == null || playerStruct->ContentId == 0) continue;

            var isAdd = false;
            foreach (var trackPlayer in TempTrackedPlayers)
            {
                if (trackPlayer.ContentID != playerStruct->ContentId) continue;
                if (validPlayers.ContainsKey(trackPlayer.ContentID)) continue;

                trackPlayer.LastSeen         = DateTime.Now;
                trackPlayer.LastSeenLocation = currentZoneData.ExtractPlaceName();

                validPlayers.Add(playerStruct->ContentId, player.Position);
                isAdd = true;
            }

            if (isAdd) continue;

            foreach (var trackPlayer in ModuleConfig.PermanentTrackedPlayers)
            {
                if (trackPlayer.ContentID != playerStruct->ContentId) continue;
                if (validPlayers.ContainsKey(trackPlayer.ContentID)) continue;

                trackPlayer.LastSeen         = DateTime.Now;
                trackPlayer.LastSeenLocation = currentZoneData.ExtractPlaceName();

                validPlayers.Add(playerStruct->ContentId, player.Position);
            }
        }

        // 防止溢出
        validPlayers = validPlayers.Take(8).ToDictionary(x => x.Key, x => x.Value);
        PlaceFieldMarkers(validPlayers);
    }

    private static void PlaceFieldMarkers(IReadOnlyDictionary<ulong, Vector3> founds)
    {
        var counter = 0U;
        foreach (var found in founds)
        {
            if (counter > 8) break;

            FieldMarkerHelper.PlaceLocal(counter, found.Value, true);
            counter++;
        }
    }

    private static bool ShouldMenuOpen(IMenuOpenedArgs args)
    {
        if (args.Target is not MenuTargetDefault target) return false;
        return target.TargetContentId != 0;
    }

    #region CustomClass

    public class TrackPlayer : IEquatable<TrackPlayer>
    {
        public ulong  ContentID { get; set; }
        public string Name      { get; set; } = string.Empty;
        public string WorldName { get; set; } = string.Empty;

        public DateTime Added            { get; set; } = DateTime.MinValue;
        public DateTime LastSeen         { get; set; } = DateTime.MinValue;
        public string   LastSeenLocation { get; set; } = string.Empty;
        public string   Note             { get; set; } = string.Empty;

        public unsafe TrackPlayer(IPlayerCharacter ipc)
        {
            var chara = ipc.ToStruct();
            ContentID = chara->ContentId;
            Name      = ipc.Name.TextValue;
            WorldName = ipc.HomeWorld.GameData.Name.ExtractText();
        }

        public TrackPlayer() { }

        public TrackPlayer(ulong contentId, string name, string world)
        {
            ContentID = contentId;
            Name      = name;
            WorldName = world;

            Added    = DateTime.Now;
            LastSeen = DateTime.MinValue;
        }

        public bool Equals(TrackPlayer? other) => ContentID == other.ContentID;

        public override bool Equals(object? obj)
        {
            if (obj is TrackPlayer otherPlayer) return Equals(otherPlayer);
            return false;
        }

        public override int GetHashCode() => ContentID.GetHashCode();

        public override string ToString() => $"{ContentID}";
    }

    private class TempTrackMenuItem : MenuItemBase
    {
        public override string Name { get; protected set; } =
            $"{GetLoc("MultiTargetTracker-TempTrack")}: {GetLoc("Add")}/{GetLoc("Delete")}";

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            if (args.Target is not MenuTargetDefault target) return;

            var data = new TrackPlayer(target.TargetContentId,
                                       target.TargetName, target.TargetHomeWorld.GameData.Name.ExtractText());
            if (!TempTrackedPlayers.Add(data))
            {
                TempTrackedPlayers.Remove(data);
                NotificationSuccess(GetLoc("Deleted"));
            }
            else
                NotificationSuccess(GetLoc("Added"));
        }
    }

    private class PermanentTrackMenuItem : MenuItemBase
    {
        public override string Name { get; protected set; } =
            $"{GetLoc("MultiTargetTracker-PermanentTrack")}: {GetLoc("Add")}/{GetLoc("Delete")}";

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            var target = args.Target as MenuTargetDefault;
            if (target.TargetObject is not IPlayerCharacter ipc) return;

            if (ModuleConfig.PermanentTrackedPlayers.Contains(new(ipc)))
            {
                ModuleConfig.PermanentTrackedPlayers.Remove(new(ipc));
                NotificationSuccess(GetLoc("Deleted"));
            }
            else
            {
                ModuleConfig.PermanentTrackedPlayers.Add(new(ipc));
                NotificationSuccess(GetLoc("Added"));
            }

            ModuleConfig.Save(ModuleManager.GetModule<MultiTargetTracker>());
        }
    }

    #endregion
}
