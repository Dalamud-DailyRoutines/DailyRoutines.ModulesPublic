using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class BetterMountRoulette : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("BetterMountRouletteTitle"),
        Description = Lang.Get("BetterMountRouletteDescription"),
        Category    = ModuleCategory.System,
        Author      = ["XSZYYS"]
    };
    
    private Config config = null!;

    private readonly ZoneSelectCombo zoneSelector = new("##BetterMountRouletteZoneSelector");
    
    private LuminaSearcher<Mount>?             masterMountsSearcher;
    private MountListHandler?                  pvpMounts;
    private MountListHandler?                  normalMounts;
    private Dictionary<uint, MountListHandler> zoneMountListHandlers = [];
    
    private HashSet<uint>? mountsListToUse;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();
        
        UseActionManager.Instance().RegPreUseAction(OnPreUseAction);

        DService.Instance().ClientState.Login += OnLogin;
        if (DService.Instance().ClientState.IsLoggedIn)
            OnLogin();

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
    }

    protected override void Uninit()
    {
        UseActionManager.Instance().Unreg(OnPreUseAction);
        DService.Instance().ClientState.Login            -= OnLogin;
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;

        masterMountsSearcher = null;
        normalMounts         = null;
        pvpMounts            = null;
        zoneMountListHandlers.Clear();

        mountsListToUse = null;
    }

    protected override void ConfigUI()
    {
        if (normalMounts == null || pvpMounts == null)
            return;

        using var tabBar = ImRaii.TabBar("##MountTabs", ImGuiTabBarFlags.Reorderable);
        if (!tabBar) return;

        DrawTab(Lang.Get("General"), normalMounts);

        DrawTab("PVP", pvpMounts);

        DrawZoneTabs();

        HandleNewZoneTabAddition();
    }

    private void DrawTab(string tabLabel, MountListHandler handler)
    {
        using var tab = ImRaii.TabItem(tabLabel);
        if (!tab) return;

        DrawSearchAndMountsGrid(tabLabel, handler);
    }

    private void DrawZoneTabs()
    {
        var zonesToRemove = new List<uint>();

        foreach (var (zoneID, handler) in zoneMountListHandlers)
        {
            if (!LuminaGetter.TryGetRow<TerritoryType>(zoneID, out var territory)) continue;

            var zoneName = territory.ExtractPlaceName();
            if (string.IsNullOrEmpty(zoneName)) continue;

            var pOpen = true;

            using var id  = ImRaii.PushId((int)zoneID);
            using var tab = ImRaii.TabItem(zoneName, ref pOpen);
            if (tab)
                DrawSearchAndMountsGrid(zoneName, handler);

            if (!pOpen)
                zonesToRemove.Add(zoneID);
        }

        foreach (var zoneID in zonesToRemove)
        {
            zoneMountListHandlers.Remove(zoneID);
            config.ZoneRouletteMounts.Remove(zoneID);
            config.Save(this);
        }
    }

    private void HandleNewZoneTabAddition()
    {
        if (zoneSelector == null) return;

        if (ImGui.TabItemButton("+", ImGuiTabItemFlags.Trailing | ImGuiTabItemFlags.NoTooltip))
            ImGui.OpenPopup("AddNewZonePopup");

        using (var popup = ImRaii.Popup("AddNewZonePopup"))
        {
            if (popup)
            {
                if (zoneSelector.DrawRadio())
                {
                    var newMountSet = new HashSet<uint>();

                    if (config.ZoneRouletteMounts.TryAdd(zoneSelector.SelectedID, newMountSet))
                    {
                        zoneMountListHandlers[zoneSelector.SelectedID] = new MountListHandler(masterMountsSearcher, newMountSet);
                        config.Save(this);
                    }
                }
            }
        }
    }

    private void DrawSearchAndMountsGrid(string tabLabel, MountListHandler handler)
    {
        var searchText = handler.SearchText;
        ImGui.SetNextItemWidth(-1f);

        if (ImGui.InputTextWithHint($"##Search{tabLabel}", Lang.Get("Search"), ref searchText, 128))
        {
            handler.SearchText = searchText;
            handler.Searcher.Search(searchText);
        }

        var       childSize = new Vector2(ImGui.GetContentRegionAvail().X, 400 * GlobalUIScale);
        using var child     = ImRaii.Child($"##MountsGrid{tabLabel}", childSize, true);
        if (!child) return;

        DrawMountsGrid(handler.Searcher.SearchResult, handler);
    }

    private void DrawMountsGrid(List<Mount> mountsToDraw, MountListHandler handler)
    {
        if (mountsToDraw.Count == 0) return;

        var itemWidthEstimate = 150f * GlobalUIScale;
        var contentWidth      = ImGui.GetContentRegionAvail().X;
        var columnCount       = Math.Max(1, (int)Math.Floor(contentWidth / itemWidthEstimate));
        var iconSize          = 3 * ImGui.GetTextLineHeightWithSpacing();

        using var table = ImRaii.Table("##MountsGridTable", columnCount, ImGuiTableFlags.SizingStretchSame);
        if (!table) return;

        foreach (var mount in mountsToDraw)
        {
            if (!ImageHelper.TryGetGameIcon(mount.Icon, out var texture)) continue;

            ImGui.TableNextColumn();

            var cursorPos   = ImGui.GetCursorPos();
            var contentSize = new Vector2(ImGui.GetContentRegionAvail().X, 4 * ImGui.GetTextLineHeightWithSpacing());

            using (ImRaii.Group())
            {
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (contentSize.X - iconSize) / 2);
                ImGui.Image(texture.Handle, new(iconSize));

                var mountName = mount.Singular.ToString();
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (contentSize.X - ImGui.CalcTextSize(mountName).X) / 2);
                ImGui.TextUnformatted(mountName);
            }

            ImGui.SetCursorPos(cursorPos);

            using (ImRaii.PushColor(ImGuiCol.Button, ButtonNormalColor))
            using (ImRaii.PushColor(ImGuiCol.ButtonActive, ButtonActiveColor))
            using (ImRaii.PushColor(ImGuiCol.ButtonHovered, ButtonHoveredColor))
            using (ImRaii.PushColor(ImGuiCol.Button, ButtonSelectedColor, handler.SelectedIDs.Contains(mount.RowId)))
            {
                if (ImGui.Button($"##{mount.RowId}_{cursorPos}", contentSize))
                {
                    if (!handler.SelectedIDs.Add(mount.RowId))
                        handler.SelectedIDs.Remove(mount.RowId);
                    config.Save(this);
                }

                if (ImGui.IsItemHovered())
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }
        }
    }

    private void OnZoneChanged(ushort obj) =>
        OnLogin();

    private unsafe void OnLogin()
    {
        var unlockedMounts = LuminaGetter.Get<Mount>()
                                         .Where
                                         (mount => PlayerState.Instance()->IsMountUnlocked(mount.RowId) &&
                                                   mount.Icon != 0                                      &&
                                                   !string.IsNullOrEmpty(mount.Singular.ToString())
                                         )
                                         .ToList();

        masterMountsSearcher = new LuminaSearcher<Mount>
        (
            unlockedMounts,
            [
                x => x.Singular.ToString()
            ]
        );

        normalMounts = new(masterMountsSearcher, config.NormalRouletteMounts);
        pvpMounts    = new(masterMountsSearcher, config.PVPRouletteMounts);

        zoneMountListHandlers.Clear();
        foreach (var (zoneID, mountIDs) in config.ZoneRouletteMounts)
            zoneMountListHandlers[zoneID] = new MountListHandler(masterMountsSearcher, mountIDs);
    }

    private void OnPreUseAction
    (
        ref bool                        isPrevented,
        ref ActionType                  actionType,
        ref uint                        actionID,
        ref ulong                       targetID,
        ref uint                        extraParam,
        ref ActionManager.UseActionMode queueState,
        ref uint                        comboRouteID
    )
    {
        if (!DService.Instance().Condition[ConditionFlag.Mounted] && actionType == ActionType.GeneralAction && MountRouletteActionIDs.Contains(actionID))
        {
            mountsListToUse = null;
            var currentZone = GameState.TerritoryType;

            if (config.ZoneRouletteMounts.TryGetValue(currentZone, out var zoneMounts) && zoneMounts.Count > 0)
                mountsListToUse = zoneMounts;
            else
            {
                mountsListToUse = GameState.IsInPVPArea
                                      ? config.PVPRouletteMounts
                                      : config.NormalRouletteMounts;
            }
        }

        if (mountsListToUse != null && actionType == ActionType.Mount)
        {
            try
            {
                if (mountsListToUse.Count > 0)
                {
                    var mountListAsList = mountsListToUse.ToList();
                    var randomMountID   = mountListAsList[Random.Shared.Next(mountListAsList.Count)];
                    actionID = randomMountID;
                }
            }
            finally
            {
                mountsListToUse = null;
            }
        }
    }

    private class Config : ModuleConfig
    {
        public HashSet<uint>                   NormalRouletteMounts = [];
        public HashSet<uint>                   PVPRouletteMounts    = [];
        public Dictionary<uint, HashSet<uint>> ZoneRouletteMounts   = [];
    }

    private class MountListHandler
    (
        LuminaSearcher<Mount> searcher,
        HashSet<uint>         selectedIDs
    )
    {
        public LuminaSearcher<Mount> Searcher     { get; }       = searcher;
        public HashSet<uint>         SelectedIDs  { get; }       = selectedIDs;
        public string                SearchText   { get; set; }  = string.Empty;
        public int                   DisplayCount { get; init; } = searcher.Data.Count;
    }

    #region 数据
    
    private static readonly HashSet<uint> MountRouletteActionIDs = [9, 24];

    private static readonly Vector4 ButtonNormalColor   = ImGuiCol.Button.ToVector4().WithAlpha(0f);
    private static readonly Vector4 ButtonActiveColor   = ImGuiCol.ButtonActive.ToVector4().WithAlpha(0.8f);
    private static readonly Vector4 ButtonHoveredColor  = ImGuiCol.ButtonHovered.ToVector4().WithAlpha(0.4f);
    private static readonly Vector4 ButtonSelectedColor = ImGuiCol.Button.ToVector4().WithAlpha(0.6f);

    #endregion
}
