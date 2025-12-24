using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using DailyRoutines.IPC;
using DailyRoutines.Managers;
using DailyRoutines.Windows;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Map = Lumina.Excel.Sheets.Map;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoMarkAetherCurrents : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoMarkAetherCurrentsTitle"),
        Description = GetLoc("AutoMarkAetherCurrentsDescription"),
        Category    = ModuleCategories.UIOptimization,
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true, AllDefaultEnabled = true };

    private static bool IsEligibleForTeleporting =>
        !(GameState.IsCN || GameState.IsTC) || AuthState.IsPremium;

    private static TaskHelper? TaskHelperMove;

    private static readonly List<AetherCurrentPoint> SelectedAetherCurrents = [];
    
    private static readonly Vector2 ChildSize    = ScaledVector2(450f, 150);
    private static          bool    IsWindowUnlock;

    private static readonly Dictionary<uint, List<ZoneAetherCurrentInfo>> VersionToZoneInfos = [];

    private static bool UseLocalMark = true;
    private static bool ManualMode;

    protected override void Init()
    {
        var acSet = LuminaGetter.Get<AetherCurrentCompFlgSet>()
                               .Where(x => x.Territory.ValueNullable != null && x.Territory.RowId != 156)
                               .ToDictionary(x => x.Territory.RowId, x => x.AetherCurrents.ToArray());

        var counter     = 0U;
        var lastVersion = 0U;
        foreach (var (zone, acArray) in acSet)
        {
            if (!LuminaGetter.TryGetRow<TerritoryType>(zone, out var zoneRow)) continue;
            
            var version = zoneRow.ExVersion.RowId;
            if (version == 0) continue;

            if (lastVersion != version)
            {
                counter = 0;
                lastVersion = version;
            }
            
            var prasedResult = ZoneAetherCurrentInfo.Parse(zone, counter, acArray);
            if (prasedResult == null) continue;

            VersionToZoneInfos.TryAdd(version - 1, []);
            VersionToZoneInfos[version - 1].Add(prasedResult);
            
            counter++;
        }

        AetherCurrentPoint.RefreshUnlockStates();
        
        TaskHelperMove ??= new() { TimeLimitMS = 30000 };

        Overlay ??= new Overlay(this);
        Overlay.Flags &= ~ImGuiWindowFlags.NoTitleBar;
        Overlay.Flags |= ImGuiWindowFlags.NoResize          | ImGuiWindowFlags.NoScrollbar |
                         ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.MenuBar;

        Overlay.SizeConstraints = new() { MinimumSize = ChildSize };
        Overlay.WindowName = $"{LuminaWrapper.GetAddonText(2448)}###AutoMarkAetherCurrents";

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "AetherCurrent", OnAddon);
        DService.ClientState.TerritoryChanged += OnZoneChanged;
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("BetterFateProgressUI-UnlockWindow"), ref IsWindowUnlock))
        {
            if (IsWindowUnlock)
            {
                Overlay.Flags &= ~ImGuiWindowFlags.AlwaysAutoResize;
                Overlay.Flags &= ~ImGuiWindowFlags.NoResize;
            }
            else
                Overlay.Flags |= ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoResize;
        }
    }

    protected override void OverlayPreDraw()
    {
        if (!Throttler.Throttle("AutoMarkAetherCurrents-Refresh", 5_000)) return;

        AetherCurrentPoint.RefreshUnlockStates();
    }

    protected override void OverlayOnOpen() => 
        AetherCurrentPoint.RefreshUnlockStates();

    protected override void OverlayUI()
    {
        using var fontPush = FontManager.UIFont120.Push();

        DrawMenuBar();
        
        DrawAetherCurrentsTabs();
    }

    private static void DrawMenuBar()
    {
        using var fontPush = FontManager.UIFont.Push();
        
        if (ImGui.BeginMenuBar())
        {
            if (ImGui.BeginMenu(GetLoc("General")))
            {
                if (ImGui.MenuItem(GetLoc("ManualMode"), string.Empty, ref ManualMode))
                    MarkAetherCurrents(DService.ClientState.TerritoryType, true, UseLocalMark);
                ImGuiOm.TooltipHover(GetLoc("AutoMarkAetherCurrents-ManualModeHelp"));
                
                if (ImGui.MenuItem(GetLoc("AutoMarkAetherCurrents-UseLocalMark"), string.Empty, ref UseLocalMark))
                    MarkAetherCurrents(DService.ClientState.TerritoryType, true, UseLocalMark);
                ImGuiOm.TooltipHover(GetLoc("AutoMarkAetherCurrents-UseLocalMarkHelp"));
                
                ImGui.EndMenu();
            }
            
            ImGui.TextDisabled("|");
            
            if (ImGui.BeginMenu(LuminaWrapper.GetAddonText(7131)))
            {
                if (ImGui.MenuItem(GetLoc("AutoMarkAetherCurrents-RefreshDisplay")))
                    MarkAetherCurrents(DService.ClientState.TerritoryType, true, UseLocalMark);
                
                if (ImGui.MenuItem(GetLoc("AutoMarkAetherCurrents-DisplayLeftCurrents")))
                    MarkAetherCurrents(DService.ClientState.TerritoryType, false, UseLocalMark);

                if (ImGui.MenuItem(GetLoc("AutoMarkAetherCurrents-RemoveAllWaymarks")))
                {
                    for (var i = 0U; i < 8; i++) 
                        FieldMarkerHelper.RemoveLocal(i);
                }
                
                ImGui.EndMenu();
            }
            
            ImGui.TextDisabled("|");
            
            if (ImGui.MenuItem(GetLoc("AutoMarkAetherCurrents-RemoveSelectedAC"), ManualMode && SelectedAetherCurrents.Count > 0))
                SelectedAetherCurrents.Clear();
            
            ImGui.TextDisabled("|");

            if (ImGui.MenuItem(GetLoc("AutoMarkAetherCurrents-DisplayNotActivated")))
            {
                SelectNotActivatedAetherCurrents();
                MarkAetherCurrents(DService.ClientState.TerritoryType, true, UseLocalMark);
            }
            
            ImGui.EndMenuBar();
        }
    }

    private static void DrawAetherCurrentsTabs()
    {
        using var group = ImRaii.Group();
        using var bar   = ImRaii.TabBar("AetherCurrentsTab");
        if (!bar) return;

        foreach (var version in VersionToZoneInfos.Keys)
            DrawAetherCurrentInfoTabItem(version);
    }

    private static void DrawAetherCurrentInfoTabItem(uint version)
    {
        if (!VersionToZoneInfos.TryGetValue(version, out var zoneInfos)) return;
        
        using var item = ImRaii.TabItem($"{version + 3}.0");
        if (!item) return;

        var counter = 0;
        foreach (var zoneInfo in zoneInfos)
        {
            zoneInfo.Draw();
            
            if (counter % 2 == 0) 
                ImGui.SameLine();
            
            counter++;
        }
    }

    private static void MarkAetherCurrents(ushort zoneID, bool isFirstPage = true, bool isLocal = true)
    {
        if (!LuminaGetter.TryGetRow<TerritoryType>(zoneID, out var zoneRow)) return;
        if (!VersionToZoneInfos.TryGetValue(zoneRow.ExVersion.RowId - 1, out var zoneInfos)) return;
        if (!zoneInfos.TryGetFirst(x => x.Zone == zoneID, out var zoneInfo)) return;
        
        Enumerable.Range(0, 8).ForEach(x => FieldMarkerHelper.PlaceLocal((uint)x, new(), false));
        
        var thisZoneSelected = SelectedAetherCurrents.Where(x => x.Zone == zoneID).ToList();
        var finalSet         = thisZoneSelected.Count != 0 || ManualMode ? thisZoneSelected : [..zoneInfo.NormalPoints];

        var result = finalSet.Skip(finalSet.Count > 8 && !isFirstPage ? 8 : 0).ToList();
        for (var i = 0U; i < result.Count; i++)
        {
            var currentMarker = result[(int)i];
            currentMarker.PlaceFieldMarker(isLocal, i);
        }
    }

    private static void SelectNotActivatedAetherCurrents()
    {
        ManualMode = true;
        SelectedAetherCurrents.Clear();

        foreach (var zoneInfos in VersionToZoneInfos.Values)
        {
            foreach (var zoneInfo in zoneInfos)
            {
                foreach (var acPoint in zoneInfo.NormalPoints.Concat(zoneInfo.QuestPoints))
                {
                    if (AetherCurrentPoint.UnlockStates.TryGetValue(acPoint.DataID, out var state) && !state)
                        SelectedAetherCurrents.Add(acPoint);
                }
            }
        }
    }

    #region 事件

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null) return;

        addon->Close(true);
        Overlay.IsOpen ^= true;
    }

    private static void OnZoneChanged(ushort zoneID) => 
        MarkAetherCurrents(zoneID, true, UseLocalMark);

    #endregion

    protected override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        
        TaskHelperMove?.Abort();
        TaskHelperMove = null;
    }

    private class ZoneAetherCurrentInfo
    {
        private const string BackgroundUldPath = "ui/uld/FlyingPermission.uld";

        public static ZoneAetherCurrentInfo? Parse(uint zoneID, uint counter, RowRef<AetherCurrent>[] acArray)
        {
            if (!LuminaGetter.TryGetRow<TerritoryType>(zoneID, out var zoneRow)) return null;

            var version = zoneRow.ExVersion.RowId;
            if (version == 0) return null;
            
            version--;

            var newInfo = new ZoneAetherCurrentInfo(version, counter, zoneID);
            foreach (var ac in acArray)
            {
                var prasedResult = AetherCurrentPoint.Parse(zoneID, ac);
                if (prasedResult == null) continue;

                switch (prasedResult.Type)
                {
                    case PointType.Normal:
                        newInfo.NormalPoints.Add(prasedResult);
                        break;
                    case PointType.Quest:
                        newInfo.QuestPoints.Add(prasedResult);
                        break;
                }
            }

            return newInfo;
        }
        
        public int  Version { get; init; }
        public int  Counter { get; init; }
        public uint Zone    { get; init; }

        public IDalamudTextureWrap? BackgroundTexture
        {
            get
            {
                if (backgroundTexture != null)
                    return backgroundTexture;
                
                // 3.0 特例
                var texturePath = $"ui/uld/FlyingPermission{(Version == 0 ? string.Empty : Version + 1)}_hr1.tex";
                backgroundTexture = DService.PI.UiBuilder.LoadUld(BackgroundUldPath).LoadTexturePart(texturePath, Counter);
                return backgroundTexture;
            }
        }

        private IDalamudTextureWrap? backgroundTexture;
        
        public List<AetherCurrentPoint> QuestPoints  { get; init; } = [];
        public List<AetherCurrentPoint> NormalPoints { get; init; } = [];

        private ZoneAetherCurrentInfo(uint version, uint counter, uint zoneID)
        {
            Version = (int)version;
            Counter = (int)counter;
            Zone    = zoneID;
        } 

        public void Draw()
        {
            using (var child = ImRaii.Child($"{ToString()}", ChildSize, true, 
                                            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                if (child)
                {
                    DrawBackgroundImage(BackgroundTexture);
                    
                    DrawZoneName(LuminaWrapper.GetZonePlaceName(Zone));
                    
                    DrawAetherCurrentProgress();
                }
            }

            HandleInteraction(Zone);
        }

        private void DrawAetherCurrentProgress()
        {
            using var fontPush = FontManager.UIFont80.Push();
            
            var height = (2 * ImGui.GetTextLineHeightWithSpacing()) + (2 * ImGui.GetStyle().FramePadding.Y);
            ImGui.SetCursorPos(new(ImGui.GetCursorPosX(), ImGui.GetContentRegionMax().Y - height));
            using (ImRaii.Group())
            {
                if (QuestPoints.Count > 0)
                {
                    using (ImRaii.Group())
                    {
                        ImGui.AlignTextToFramePadding();
                        ImGui.Text("Q  ");

                        QuestPoints.ForEach(x => x.Draw());
                    }
                }

                if (NormalPoints.Count > 0)
                {
                    using (ImRaii.Group())
                    {
                        ImGui.AlignTextToFramePadding();
                        ImGui.Text("N  ");

                        NormalPoints.ForEach(x => x.Draw());
                    }
                }
            }
        }

        private static void DrawBackgroundImage(IDalamudTextureWrap? backgroundImage)
        {
            if (backgroundImage == null) return;
            
            var originalCursorPos = ImGui.GetCursorPos();
            ImGui.SetCursorPos(originalCursorPos - ScaledVector2(10f, 4));
            
            ImGui.Image(backgroundImage.Handle, ImGui.GetWindowSize() + ScaledVector2(10f, 4f));
            
            ImGui.SetCursorPos(originalCursorPos);
        }
        
        private static void DrawZoneName(string name)
        {
            ImGui.SetWindowFontScale(1.05f);
            
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (8f * GlobalFontScale));
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (4f * GlobalFontScale));
            ImGui.Text(name);
            
            ImGui.SetWindowFontScale(1f);
        }
        
        private static void HandleInteraction(uint zone)
        {
            if (!LuminaGetter.TryGetRow<TerritoryType>(zone, out var zoneRow)) return;
            
            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                var agent = AgentMap.Instance();
                if (agent->AgentInterface.IsAgentActive() && agent->SelectedMapId == zoneRow.Map.RowId)
                    agent->AgentInterface.Hide();
                else
                    agent->OpenMap(zoneRow.Map.RowId, zoneRow.RowId, LuminaWrapper.GetAddonText(2448));
            }
        }

        public override string ToString() => $"ZoneAetherCurrentInfo_Version{Version + 3}.0_{Zone}_{Counter}";
    }
    
    private sealed class AetherCurrentPoint : IEquatable<AetherCurrentPoint>
    {
        public static Dictionary<uint, uint> EObjDataSheet { get; } =
            LuminaGetter.Get<EObj>()
                        .Where(x => x.Data.RowId != 0)
                        .DistinctBy(x => x.Data.RowId)
                        .ToDictionary(x => x.Data.RowId, x => x.RowId);

        public static Dictionary<uint, Vector3> LevelSheet { get; } =
            LuminaGetter.Get<Level>()
                        .Where(x => x.Object.Is<EObj>())
                        .DistinctBy(x => x.Object.RowId)
                        .ToDictionary(x => x.Object.RowId, x => x.ToPosition());
        
        public static Dictionary<uint, bool> UnlockStates { get; private set; } = [];
        
        public static AetherCurrentPoint? Parse(uint zone, RowRef<AetherCurrent> data)
        {
            if (data.ValueNullable == null) return null;
            // 摩杜纳
            if (zone == 156) return null;
            
            var aetherCurrent = data.Value;
            if (aetherCurrent.RowId == 0) return null;
            
            if (aetherCurrent.Quest.RowId != 0)
            {
                return new AetherCurrentPoint(PointType.Quest, zone, data.RowId, aetherCurrent.Quest.RowId,
                                              aetherCurrent.Quest.Value.IssuerLocation.Value.ToPosition());
            }

            if (!EObjDataSheet.TryGetValue(aetherCurrent.RowId, out var eobjID)) return null;
            if (!LevelSheet.TryGetValue(eobjID, out var position)) return null;

            return new AetherCurrentPoint(PointType.Normal, zone, data.RowId, eobjID, position);
        }
        
        public static void RefreshUnlockStates()
        {
            foreach (var ac in LuminaGetter.Get<AetherCurrent>().Select(x => x.RowId).ToList())
                UnlockStates[ac] = PlayerState.Instance()->IsAetherCurrentUnlocked(ac);
        }
        
        public PointType Type     { get; init; }
        public uint      Zone     { get; init; }
        public uint      DataID   { get; init; }
        public uint      ObjectID { get; init; } // EObj ID 或 Quest ID
        public Vector3   Position { get; init; }


        public TerritoryType RealTerritory { get; init; }
        public Map           RealMap       { get; init; }

        private AetherCurrentPoint(PointType type, uint zone, uint dataID, uint objectID, Vector3 position)
        {
            Type     = type;
            Zone     = zone;
            DataID   = dataID;
            ObjectID = objectID;
            Position = position;

            RealTerritory = (Type == PointType.Normal
                             ? LuminaGetter.GetRow<TerritoryType>(Zone)
                             : LuminaGetter.GetRow<Quest>(ObjectID)?.IssuerLocation.ValueNullable?.Territory.Value)
                .GetValueOrDefault();

            RealMap = (Type == PointType.Normal
                       ? RealTerritory.Map.Value
                       : LuminaGetter.GetRow<Quest>(ObjectID)?.IssuerLocation.ValueNullable?.Map.Value)
                .GetValueOrDefault();
        }

        public void Draw()
        {
            if (!UnlockStates.TryGetValue(DataID, out var state)) return;
            using var id = ImRaii.PushId($"{DataID}");

            ImGui.SameLine();
            if (!ManualMode)
                ImGui.Checkbox($"###{DataID}", ref state);
            else
            {
                state = SelectedAetherCurrents.Contains(this);
                if (ImGui.Checkbox($"###{DataID}", ref state))
                {
                    if (SelectedAetherCurrents.Contains(this))
                        SelectedAetherCurrents.Remove(this);
                    else
                        SelectedAetherCurrents.Add(this);
                }
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                DrawBasicInfo();
                ImGui.EndTooltip();
            }

            using var popup = ImRaii.ContextPopupItem($"###{DataID}");
            if (!popup) return;
            
            if (ImGui.IsWindowAppearing())
                PlaceFlag();

            DrawBasicInfo();

            ImGui.Separator();
            ImGui.Spacing();

            using (ImRaii.Disabled(!IsEligibleForTeleporting))
            {
                if (ImGui.MenuItem($"    {GetLoc("AutoMarkAetherCurrents-TeleportTo")}"))
                    TeleportTo();
            }

            ImGui.Separator();

            using (ImRaii.Disabled(!IsPluginEnabled(vnavmeshIPC.InternalName)))
            {
                if (ImGui.MenuItem($"    {GetLoc("AutoMarkAetherCurrents-MoveTo")} (vnavmesh)"))
                    MoveTo(TaskHelperMove);
            }

            ImGui.Separator();

            if (ImGui.MenuItem($"    {GetLoc("AutoMarkAetherCurrents-SendLocation")}"))
            {
                AgentMap.Instance()->SetFlagMapMarker(RealTerritory.RowId, RealTerritory.Map.RowId, Position);
                ChatManager.SendMessage("<flag>");
            }

            return;

            void DrawBasicInfo()
            {
                if (!UnlockStates.TryGetValue(DataID, out var isTouched)) return;

                ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("AutoMarkAetherCurrents-DiscoverInfo")}:");

                ImGui.SameLine();
                ImGui.TextColored(isTouched ? ImGuiColors.HealerGreen : ImGuiColors.DPSRed,
                                  isTouched ? GetLoc("AutoMarkAetherCurrents-Discovered") : GetLoc("AutoMarkAetherCurrents-NotDiscovered"));
                
                if (Type == PointType.Quest && LuminaGetter.TryGetRow<Quest>(ObjectID, out var questRow))
                {
                    var questName = questRow.Name.ExtractText();
                    var questIcon = DService.Texture.GetFromGameIcon(71141);

                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("Quest")}:");

                    ImGui.SameLine();
                    ImGuiOm.TextImage(questName, questIcon.GetWrapOrEmpty().Handle, new(ImGui.GetTextLineHeightWithSpacing()));
                }
            }
        }

        public void PlaceFieldMarker(bool isLocal, uint index)
        {
            if (isLocal) 
                FieldMarkerHelper.PlaceLocal(index, Position, true);
            else 
                FieldMarkerHelper.PlaceOnline(index, Position);
        }

        public void PlaceFlag()
        {
            var agent = AgentMap.Instance();

            agent->SelectedMapId = RealMap.RowId;
            agent->SetFlagMapMarker(RealTerritory.RowId, RealMap.RowId, Position);
            if (!agent->IsAgentActive()) 
                agent->Show();
            agent->OpenMap(RealMap.RowId, RealTerritory.RowId, LuminaGetter.GetRow<Addon>(2448)!.Value.Text.ExtractText());
        }

        public void TeleportTo()
            => MovementManager.TPSmart_BetweenZone(RealTerritory.RowId, Position);

        public void MoveTo(TaskHelper? taskHelper)
        {
            if (taskHelper == null) return;
            
            if (DService.ClientState.TerritoryType != RealTerritory.RowId)
                taskHelper.Enqueue(() => MovementManager.TeleportNearestAetheryte(Position, RealTerritory.RowId));
            taskHelper.Enqueue(() => DService.ClientState.TerritoryType == RealTerritory.RowId && IsScreenReady());
            taskHelper.Enqueue(() =>
            {
                if (!IsOnMount)
                {
                    TaskHelperMove.Enqueue(() => UseActionManager.UseAction(ActionType.GeneralAction, 9), weight: 1);
                    TaskHelperMove.Enqueue(() => IsOnMount,                                               weight: 1);
                }
            });
            taskHelper.Enqueue(() => vnavmeshIPC.PathfindAndMoveTo(Position, false));
        }

        public bool Equals(AetherCurrentPoint? other)
        {
            if(other is null) return false;
            if(ReferenceEquals(this, other)) return true;
            return DataID == other.DataID;
        }

        public override bool Equals(object? obj) => 
            ReferenceEquals(this, obj) || obj is AetherCurrentPoint other && Equals(other);

        public override int GetHashCode() => 
            (int)DataID;

        public static bool operator ==(AetherCurrentPoint? left, AetherCurrentPoint? right) => 
            Equals(left, right);

        public static bool operator !=(AetherCurrentPoint? left, AetherCurrentPoint? right) => 
            !Equals(left, right);
    };
    
    public enum PointType
    {
        Normal,
        Quest
    }
}
