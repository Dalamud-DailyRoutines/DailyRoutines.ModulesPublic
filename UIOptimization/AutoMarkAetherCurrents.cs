using System.Numerics;
using DailyRoutines.Common.Interface.Windows;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Manager;
using DailyRoutines.Verification;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using OmenTools.Dalamud;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;
using OmenTools.Threading.TaskHelper;
using Map = Lumina.Excel.Sheets.Map;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoMarkAetherCurrents : ModuleBase
{
    public enum PointType
    {
        Normal,
        Quest
    }

    private static TaskHelper? TaskHelperMove;

    private static readonly List<AetherCurrentPoint> SelectedAetherCurrents = [];

    private static readonly Vector2 ChildSize = ScaledVector2(450f, 150);
    private static          bool    IsWindowUnlock;

    private static readonly Dictionary<uint, List<ZoneAetherCurrentInfo>> VersionToZoneInfos = [];

    private static bool UseLocalMark = true;
    private static bool ManualMode;

    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoMarkAetherCurrentsTitle"),
        Description = Lang.Get("AutoMarkAetherCurrentsDescription"),
        Category    = ModuleCategory.UIOptimization
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true, AllDefaultEnabled = true };

    private static bool IsEligibleForTeleporting =>
        !(GameState.IsCN || GameState.IsTC) || AuthState.IsPremium;

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
                counter     = 0;
                lastVersion = version;
            }

            var prasedResult = ZoneAetherCurrentInfo.Parse(zone, counter, acArray);
            if (prasedResult == null) continue;

            VersionToZoneInfos.TryAdd(version - 1, []);
            VersionToZoneInfos[version - 1].Add(prasedResult);

            counter++;
        }

        AetherCurrentPoint.RefreshUnlockStates();

        TaskHelperMove ??= new() { TimeoutMS = 30000 };

        Overlay       ??= new Overlay(this);
        Overlay.Flags &=  ~ImGuiWindowFlags.NoTitleBar;
        Overlay.Flags |= ImGuiWindowFlags.NoResize          |
                         ImGuiWindowFlags.NoScrollbar       |
                         ImGuiWindowFlags.NoScrollWithMouse |
                         ImGuiWindowFlags.MenuBar;

        Overlay.SizeConstraints = new() { MinimumSize = ChildSize };
        Overlay.WindowName      = $"{LuminaWrapper.GetAddonText(2448)}###AutoMarkAetherCurrents";

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "AetherCurrent", OnAddon);
        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("BetterFateProgressUI-UnlockWindow"), ref IsWindowUnlock))
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
        if (!Throttler.Shared.Throttle("AutoMarkAetherCurrents-Refresh", 5_000)) return;

        AetherCurrentPoint.RefreshUnlockStates();
    }

    protected override void OverlayOnOpen() =>
        AetherCurrentPoint.RefreshUnlockStates();

    protected override void OverlayUI()
    {
        using var fontPush = FontManager.Instance().UIFont120.Push();

        DrawMenuBar();

        DrawAetherCurrentsTabs();
    }

    private static void DrawMenuBar()
    {
        using var fontPush = FontManager.Instance().UIFont.Push();

        if (ImGui.BeginMenuBar())
        {
            if (ImGui.BeginMenu(Lang.Get("General")))
            {
                if (ImGui.MenuItem(Lang.Get("ManualMode"), string.Empty, ref ManualMode))
                    MarkAetherCurrents(GameState.TerritoryType, true, UseLocalMark);
                ImGuiOm.TooltipHover(Lang.Get("AutoMarkAetherCurrents-ManualModeHelp"));

                if (ImGui.MenuItem(Lang.Get("AutoMarkAetherCurrents-UseLocalMark"), string.Empty, ref UseLocalMark))
                    MarkAetherCurrents(GameState.TerritoryType, true, UseLocalMark);
                ImGuiOm.TooltipHover(Lang.Get("AutoMarkAetherCurrents-UseLocalMarkHelp"));

                ImGui.EndMenu();
            }

            ImGui.TextDisabled("|");

            if (ImGui.BeginMenu(LuminaWrapper.GetAddonText(7131)))
            {
                if (ImGui.MenuItem(Lang.Get("AutoMarkAetherCurrents-RefreshDisplay")))
                    MarkAetherCurrents(GameState.TerritoryType, true, UseLocalMark);

                if (ImGui.MenuItem(Lang.Get("AutoMarkAetherCurrents-DisplayLeftCurrents")))
                    MarkAetherCurrents(GameState.TerritoryType, false, UseLocalMark);

                if (ImGui.MenuItem(Lang.Get("AutoMarkAetherCurrents-RemoveAllWaymarks")))
                {
                    for (var i = 0U; i < 8; i++)
                        MarkingController.Instance()->ClearFieldMarkerLocal((FieldMarkerPoint)i);
                }

                ImGui.EndMenu();
            }

            ImGui.TextDisabled("|");

            if (ImGui.MenuItem(Lang.Get("AutoMarkAetherCurrents-RemoveSelectedAC"), ManualMode && SelectedAetherCurrents.Count > 0))
                SelectedAetherCurrents.Clear();

            ImGui.TextDisabled("|");

            if (ImGui.MenuItem(Lang.Get("AutoMarkAetherCurrents-DisplayNotActivated")))
            {
                SelectNotActivatedAetherCurrents();
                MarkAetherCurrents(GameState.TerritoryType, true, UseLocalMark);
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

    private static void MarkAetherCurrents(uint zoneID, bool isFirstPage = true, bool isLocal = true)
    {
        if (!LuminaGetter.TryGetRow<TerritoryType>(zoneID, out var zoneRow)) return;
        if (!VersionToZoneInfos.TryGetValue(zoneRow.ExVersion.RowId - 1, out var zoneInfos)) return;
        if (!zoneInfos.TryGetFirst(x => x.Zone == zoneID, out var zoneInfo)) return;

        foreach (var point in Enum.GetValues<FieldMarkerPoint>())
            MarkingController.Instance()->ClearFieldMarkerLocal(point);

        var thisZoneSelected = SelectedAetherCurrents.Where(x => x.Zone == zoneID).ToList();
        var finalSet         = thisZoneSelected.Count != 0 || ManualMode ? thisZoneSelected : [..zoneInfo.NormalPoints];

        var result = finalSet.Skip(finalSet.Count > 8 && !isFirstPage ? 8 : 0).ToList();

        for (var i = 0U; i < MathF.Min(8, result.Count); i++)
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

    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);

        TaskHelperMove?.Abort();
        TaskHelperMove = null;
    }

    private class ZoneAetherCurrentInfo
    {
        private const string BACKGROUND_ULD_PATH = "ui/uld/FlyingPermission.uld";

        private IDalamudTextureWrap? backgroundTexture;

        private ZoneAetherCurrentInfo(uint version, uint counter, uint zoneID)
        {
            Version = (int)version;
            Counter = (int)counter;
            Zone    = zoneID;
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
                backgroundTexture = DService.Instance().PI.UiBuilder.LoadUld(BACKGROUND_ULD_PATH).LoadTexturePart(texturePath, Counter);
                return backgroundTexture;
            }
        }

        public List<AetherCurrentPoint> QuestPoints  { get; init; } = [];
        public List<AetherCurrentPoint> NormalPoints { get; init; } = [];

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

        public void Draw()
        {
            using (var child = ImRaii.Child
                   (
                       $"{ToString()}",
                       ChildSize,
                       true,
                       ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
                   ))
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
            using var fontPush = FontManager.Instance().UIFont80.Push();

            var height = 2 * ImGui.GetTextLineHeightWithSpacing() + 2 * ImGui.GetStyle().FramePadding.Y;
            ImGui.SetCursorPos(new(ImGui.GetCursorPosX(), ImGui.GetContentRegionMax().Y - height));

            using (ImRaii.Group())
            {
                if (QuestPoints.Count > 0)
                {
                    using (ImRaii.Group())
                    {
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextUnformatted("Q  ");

                        QuestPoints.ForEach(x => x.Draw());
                    }
                }

                if (NormalPoints.Count > 0)
                {
                    using (ImRaii.Group())
                    {
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextUnformatted("N  ");

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

            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 8f * GlobalUIScale);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 4f * GlobalUIScale);
            ImGui.TextUnformatted(name);

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

        public static Dictionary<uint, uint> EObjDataSheet { get; } =
            LuminaGetter.Get<EObj>()
                        .Where(x => x.Data.RowId != 0)
                        .DistinctBy(x => x.Data.RowId)
                        .ToDictionary(x => x.Data.RowId, x => x.RowId);

        public static Dictionary<uint, Vector3> LevelSheet { get; } =
            LuminaGetter.Get<Level>()
                        .Where(x => x.Object.Is<EObj>())
                        .DistinctBy(x => x.Object.RowId)
                        .ToDictionary(x => x.Object.RowId, x => x.GetPosition());

        public static Dictionary<uint, bool> UnlockStates { get; private set; } = [];

        public PointType Type     { get; init; }
        public uint      Zone     { get; init; }
        public uint      DataID   { get; init; }
        public uint      ObjectID { get; init; } // EObj ID 或 Quest ID
        public Vector3   Position { get; init; }


        public TerritoryType RealTerritory { get; init; }
        public Map           RealMap       { get; init; }

        public bool Equals(AetherCurrentPoint? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return DataID == other.DataID;
        }

        public static AetherCurrentPoint? Parse(uint zone, RowRef<AetherCurrent> data)
        {
            if (data.ValueNullable == null) return null;
            // 摩杜纳
            if (zone == 156) return null;

            var aetherCurrent = data.Value;
            if (aetherCurrent.RowId == 0) return null;

            if (aetherCurrent.Quest.RowId != 0)
            {
                return new AetherCurrentPoint
                (
                    PointType.Quest,
                    zone,
                    data.RowId,
                    aetherCurrent.Quest.RowId,
                    aetherCurrent.Quest.Value.IssuerLocation.Value.GetPosition()
                );
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
                if (ImGui.MenuItem($"    {Lang.Get("AutoMarkAetherCurrents-TeleportTo")}"))
                    TeleportTo();
            }

            ImGui.Separator();

            using (ImRaii.Disabled(!DService.Instance().PI.IsPluginEnabled(vnavmeshIPC.INTERNAL_NAME)))
            {
                if (ImGui.MenuItem($"    {Lang.Get("AutoMarkAetherCurrents-MoveTo")} (vnavmesh)"))
                    MoveTo(TaskHelperMove);
            }

            ImGui.Separator();

            if (ImGui.MenuItem($"    {Lang.Get("AutoMarkAetherCurrents-SendLocation")}"))
            {
                AgentMap.Instance()->SetFlagMapMarker(RealTerritory.RowId, RealTerritory.Map.RowId, Position);
                ChatManager.Instance().SendMessage("<flag>");
            }

            return;

            void DrawBasicInfo()
            {
                if (!UnlockStates.TryGetValue(DataID, out var isTouched)) return;

                ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoMarkAetherCurrents-DiscoverInfo")}:");

                ImGui.SameLine();
                ImGui.TextColored
                (
                    isTouched ? ImGuiColors.HealerGreen : ImGuiColors.DPSRed,
                    isTouched ? Lang.Get("AutoMarkAetherCurrents-Discovered") : Lang.Get("AutoMarkAetherCurrents-NotDiscovered")
                );

                if (Type == PointType.Quest && LuminaGetter.TryGetRow<Quest>(ObjectID, out var questRow))
                {
                    var questName = questRow.Name.ToString();
                    var questIcon = DService.Instance().Texture.GetFromGameIcon(71141);

                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Quest")}:");

                    ImGui.SameLine();
                    ImGuiOm.TextImage(questName, questIcon.GetWrapOrEmpty().Handle, new(ImGui.GetTextLineHeightWithSpacing()));
                }
            }
        }

        public void PlaceFieldMarker(bool isLocal, uint index)
        {
            var marker = (FieldMarkerPoint)index;
            if (isLocal)
                MarkingController.Instance()->PlaceFieldMarkerLocal(marker, Position);
            else
                MarkingController.Instance()->PlaceFieldMarkerOnline(marker, Position);
        }

        public void PlaceFlag()
        {
            var agent = AgentMap.Instance();

            agent->SelectedMapId = RealMap.RowId;
            agent->SetFlagMapMarker(RealTerritory.RowId, RealMap.RowId, Position);
            if (!agent->IsAgentActive())
                agent->Show();
            agent->OpenMap(RealMap.RowId, RealTerritory.RowId, LuminaWrapper.GetAddonText(2448));
        }

        public void TeleportTo() =>
            MovementManager.TPSmart_BetweenZone(RealTerritory.RowId, Position);

        public void MoveTo(TaskHelper? taskHelper)
        {
            if (taskHelper == null) return;

            if (GameState.TerritoryType != RealTerritory.RowId)
                taskHelper.Enqueue(() => MovementManager.TeleportNearestAetheryte(Position, RealTerritory.RowId));
            taskHelper.Enqueue(() => GameState.TerritoryType == RealTerritory.RowId && UIModule.IsScreenReady());
            taskHelper.Enqueue
            (() =>
                {
                    if (!DService.Instance().Condition.IsOnMount)
                    {
                        TaskHelperMove.Enqueue(() => UseActionManager.Instance().UseAction(ActionType.GeneralAction, 9), weight: 1);
                        TaskHelperMove.Enqueue(() => DService.Instance().Condition.IsOnMount,                            weight: 1);
                    }
                }
            );
            taskHelper.Enqueue(() => vnavmeshIPC.PathfindAndMoveTo(Position, false));
        }

        public override bool Equals(object? obj) =>
            ReferenceEquals(this, obj) || obj is AetherCurrentPoint other && Equals(other);

        public override int GetHashCode() =>
            (int)DataID;

        public static bool operator ==(AetherCurrentPoint? left, AetherCurrentPoint? right) =>
            Equals(left, right);

        public static bool operator !=(AetherCurrentPoint? left, AetherCurrentPoint? right) =>
            !Equals(left, right);
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
}
