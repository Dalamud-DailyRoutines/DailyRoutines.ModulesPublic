using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Internal;
using DailyRoutines.Manager;
using DailyRoutines.Verification;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models.Packets.Upstream;
using OmenTools.OmenService;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;
using Map = Lumina.Excel.Sheets.Map;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;

namespace DailyRoutines.ModulesPublic;

public unsafe class BetterTeleport : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = Lang.Get("BetterTeleportTitle"),
        Description         = Lang.Get("BetterTeleportDescription"),
        Category            = ModuleCategory.UIOptimization,
        ModulesPrerequisite = ["SameAethernetTeleport"]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };
    
    private static uint TicketUsageType
    {
        get => DService.Instance().GameConfig.UiConfig.GetUInt("TelepoTicketUseType");
        set => DService.Instance().GameConfig.UiConfig.Set("TelepoTicketUseType", value);
    }

    private static uint TicketUsageGilSetting
    {
        get => DService.Instance().GameConfig.UiConfig.GetUInt("TelepoTicketGilSetting");
        set => DService.Instance().GameConfig.UiConfig.Set("TelepoTicketGilSetting", value);
    }

    private IEnumerable<AetheryteRecord> AllRecords =>
        records.Values.SelectMany(x => x).Concat(houseRecords);
    
    private Config config = null!;
    
    // Icon ID - Record
    private readonly Dictionary<string, List<AetheryteRecord>> records      = [];
    private readonly List<AetheryteRecord>                     houseRecords = [];
    
    private bool isRefreshing;

    private string                searchWord   = string.Empty;
    private List<AetheryteRecord> searchResult = [];
    private List<AetheryteRecord> favorites    = [];
    private bool                  isNeedToLoseFocusSearchBar;

    private readonly Dictionary<uint, float> hoverProgress = [];
    private          float                   hoverStartTime;

    private AetheryteRecord? hoveredAetheryte;
    private AetheryteRecord? lastHoveredAetheryte;
    private AetheryteRecord? pinnedAetheryte;

    private Vector3 contextMenuTargetPos;
    private uint    contextMenuTargetZone;

    protected override void Init()
    {
        Overlay            ??= new(this);
        Overlay.Flags      &=  ~ImGuiWindowFlags.AlwaysAutoResize;
        Overlay.Flags      &=  ~ImGuiWindowFlags.NoTitleBar;
        Overlay.Flags      |=  ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        Overlay.WindowName =   $"{LuminaWrapper.GetAddonText(8513)}###BetterTeleportOverlay";

        TaskHelper ??= new() { TimeoutMS = 60_000 };

        config = Config.Load(this) ?? new();

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);

        CommandManager.Instance().AddCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("BetterTeleport-CommandHelp") });

        UseActionManager.Instance().RegPreUseAction(OnPostUseAction);
    }
    
    protected override void Uninit()
    {
        UseActionManager.Instance().Unreg(OnPostUseAction);
        CommandManager.Instance().RemoveCommand(COMMAND);

        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Command")}:");

        ImGui.SameLine();
        ImGui.TextWrapped($"{COMMAND} {Lang.Get("BetterTeleport-CommandHelp")}");
    }

    protected override void OverlayUI()
    {
        hoveredAetheryte = null;

        switch (isRefreshing)
        {
            case false when !TaskHelper.IsBusy && records.Count == 0:
                OnZoneChanged(0);
                return;
            case true:
                return;
        }

        if (DService.Instance().KeyState[VirtualKey.ESCAPE])
        {
            Overlay.IsOpen = false;
            if (SystemMenu != null)
                SystemMenu->Close(true);
        }

        var isSearchEmpty = string.IsNullOrWhiteSpace(searchWord);


        var searchBarID = "###Search";

        if (isNeedToLoseFocusSearchBar)
        {
            searchBarID                = "###Search_LoseFocus";
            isNeedToLoseFocusSearchBar = false;
        }

        ImGui.SetNextItemWidth(isSearchEmpty ? -1f : -ImGui.GetFrameHeight() - ImGui.GetStyle().ItemSpacing.X);

        if (ImGui.InputTextWithHint(searchBarID, Lang.Get("PleaseSearch"), ref searchWord, 128))
        {
            searchResult = !string.IsNullOrWhiteSpace(searchWord)
                               ? records.Values
                                        .SelectMany(x => x)
                                        .Where
                                        (x => x.ToString()
                                               .Contains(searchWord, StringComparison.OrdinalIgnoreCase) ||
                                              config.Remarks.TryGetValue(x.RowID, out var remark) &&
                                              remark.Contains(searchWord, StringComparison.OrdinalIgnoreCase)
                                        )
                                        .ToList()
                               : [];
        }

        if (!isSearchEmpty)
        {
            ImGui.SameLine();

            if (ImGuiOm.ButtonIcon("Clear", FontAwesomeIcon.Times))
            {
                searchWord   = string.Empty;
                searchResult = [];
            }
        }

        ImGui.Spacing();

        if (searchResult.Count > 0 || !isSearchEmpty)
        {
            using var child = ImRaii.Child("###SearchResultChild", new Vector2(0, -ImGui.GetFrameHeightWithSpacing()), false, ImGuiWindowFlags.NoBackground);

            if (child)
            {
                if (searchResult.Count != 0)
                {
                    foreach (var aetheryte in searchResult.ToList())
                        DrawAetheryte(aetheryte);
                }
            }
        }
        else
        {
            using var tabBar = ImRaii.TabBar("###AetherytesTabBar", ImGuiTabBarFlags.Reorderable | ImGuiTabBarFlags.NoTooltip);

            if (tabBar)
            {
                var isSettingOn = false;

                if (favorites.Count > 0)
                {
                    using var tabItem = ImRaii.TabItem($"{Lang.Get("Favorite")}##TabItem");

                    if (tabItem)
                    {
                        var       childSize = new Vector2(0, -ImGui.GetFrameHeightWithSpacing());
                        using var child     = ImRaii.Child("###FavoriteChild", childSize, false, ImGuiWindowFlags.NoBackground);

                        if (child)
                        {
                            foreach (var aetheryte in favorites.ToList())
                                DrawAetheryte(aetheryte);
                        }
                    }
                }

                var agentLobby = AgentLobby.Instance();

                if (agentLobby != null)
                {
                    foreach (var (name, aetherytes) in records.ToList())
                    {
                        using var tabItem = ImRaii.TabItem($"{name}##TabItem");
                        if (!tabItem) continue;

                        var       childSize = new Vector2(0, -ImGui.GetFrameHeightWithSpacing());
                        using var child     = ImRaii.Child($"###{name}Child", childSize, false, ImGuiWindowFlags.NoBackground);
                        if (!child) continue;

                        var source      = name == LuminaWrapper.GetAddonText(832) ? houseRecords.Concat(aetherytes) : aetherytes;
                        var lastName    = string.Empty;
                        var lastGroupID = -1;

                        foreach (var aetheryte in source.ToList())
                        {
                            if (!aetheryte.IsUnlocked() && aetheryte.Group != 255) continue;
                            if (aetheryte.Group                   == 254 &&
                                agentLobby->LobbyData.HomeWorldId != agentLobby->LobbyData.CurrentWorldId)
                                continue;

                            var isNewGroup = false;

                            if (aetheryte.Group == 0)
                            {
                                if (lastName != aetheryte.RegionName)
                                {
                                    isNewGroup = true;
                                    lastName   = aetheryte.RegionName;
                                }
                            }
                            else
                            {
                                if (lastGroupID != aetheryte.Group)
                                {
                                    isNewGroup  = true;
                                    lastGroupID = aetheryte.Group;
                                }
                            }

                            if (isNewGroup)
                            {
                                ImGui.Spacing();

                                var headerName    = aetheryte.RegionName;
                                var headerBgColor = ImGui.GetColorU32(ImGuiCol.Header);
                                var cursor        = ImGui.GetCursorScreenPos();
                                var width         = ImGui.GetContentRegionAvail().X;
                                var height        = ImGui.GetTextLineHeightWithSpacing();

                                ImGui.GetWindowDrawList().AddRectFilled(cursor, cursor + new Vector2(width, height), headerBgColor, 4f);

                                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetStyle().ItemSpacing.X * 2);
                                ImGui.AlignTextToFramePadding();
                                ImGui.TextColored(ImGui.GetColorU32(ImGuiCol.Text).ToVector4(), headerName);

                                ImGui.Spacing();
                            }

                            DrawAetheryte(aetheryte);
                        }
                    }
                }

                using (var settingTab = ImRaii.TabItem(FontAwesomeIcon.Cog.ToIconString()))
                {
                    if (settingTab)
                    {
                        isSettingOn = true;

                        ImGui.TextUnformatted($"{LuminaWrapper.GetAddonText(8522)}");

                        using (var combo = ImRaii.Combo("###TeleportUsageTypeCombo", TicketUsageTypes[TicketUsageType]))
                        {
                            if (combo)
                            {
                                foreach (var kvp in TicketUsageTypes)
                                {
                                    if (ImGui.Selectable($"{kvp.Value}", kvp.Key == TicketUsageType))
                                        TicketUsageType = kvp.Key;
                                }
                            }
                        }

                        ImGui.TextUnformatted($"{LuminaWrapper.GetAddonText(8528)}");

                        var gilSetting = TicketUsageGilSetting;
                        if (ImGui.InputUInt("###GilInput", ref gilSetting))
                            TicketUsageGilSetting = gilSetting;

                        if (ImGui.Checkbox(Lang.Get("BetterTeleport-HideAethernetInParty"), ref config.HideAethernetInParty))
                            config.Save(this);
                    }
                    else
                        ImGuiOm.TooltipHover(LuminaWrapper.GetAddonText(8516));
                }

                if (!isSettingOn)
                    DrawBottomToolbar();
            }
        }

        DrawHoveredTooltip();
    }

    protected override void OverlayOnClose() =>
        config.Save(this);

    private void DrawAetheryte(AetheryteRecord aetheryte)
    {
        if (config.HideAethernetInParty && !aetheryte.IsAetheryte && DService.Instance().PartyList.Length > 1)
            return;

        var hasRemark   = config.Remarks.TryGetValue(aetheryte.RowID, out var remark);
        var displayName = hasRemark ? remark : aetheryte.Name;
        var cost        = aetheryte.Cost;

        using var id = ImRaii.PushId($"{aetheryte}");

        var startPos   = ImGui.GetCursorScreenPos();
        var width      = ImGui.GetContentRegionAvail().X;
        var lineHeight = ImGui.GetTextLineHeight();
        var padding    = ImGui.GetStyle().ItemSpacing.X;
        var itemHeight = lineHeight * 2.2f + padding;

        if (ImGui.InvisibleButton("##ItemBtn", new Vector2(width, itemHeight)))
            HandleTeleport(aetheryte);
        var isHovered = ImGui.IsItemHovered();
        var isActive  = ImGui.IsItemActive();

        using (var context = ImRaii.ContextPopupItem("AetheryteContextPopup"))
        {
            if (context)
            {
                ImGui.TextUnformatted($"{aetheryte.Name}");

                ImGui.Separator();
                ImGui.Spacing();

                if (ImGui.MenuItem(Lang.Get("Favorite"), string.Empty, config.Favorites.Contains(aetheryte.RowID)))
                {
                    if (!config.Favorites.Add(aetheryte.RowID))
                        config.Favorites.Remove(aetheryte.RowID);

                    RefreshFavoritesInfo();
                    config.Save(this);
                }

                ImGui.Separator();
                ImGui.Spacing();

                ImGui.TextUnformatted(Lang.Get("Note"));

                var input = hasRemark ? remark : string.Empty;
                ImGui.SetNextItemWidth(Math.Max(150f * GlobalUIScale, ImGui.CalcTextSize(aetheryte.Name).X));

                if (ImGui.InputText("###Note", ref input, 128))
                {
                    if (string.IsNullOrWhiteSpace(input))
                        config.Remarks.Remove(aetheryte.RowID);
                    else
                        config.Remarks[aetheryte.RowID] = input;
                }

                if (ImGui.IsItemDeactivatedAfterEdit())
                    config.Save(this);

                ImGui.Separator();
                ImGui.Spacing();

                ImGui.TextUnformatted(Lang.Get("Position"));

                var hasPosition = config.Positions.TryGetValue(aetheryte.RowID, out var position);
                using (FontManager.Instance().UIFont60.Push())
                    ImGui.TextUnformatted($"{(hasPosition ? position : aetheryte.Position):F1}");

                if (ImGui.MenuItem(Lang.Get("BetterTeleport-RedirectedToCurrentPos")))
                {
                    config.Positions[aetheryte.RowID] = Control.GetLocalPlayer()->Position;
                    config.Save(this);
                }

                using (ImRaii.Disabled(!config.Positions.ContainsKey(aetheryte.RowID)))
                {
                    if (ImGui.MenuItem($"{Lang.Get("Clear")}###DeleteRedirected"))
                    {
                        config.Positions.Remove(aetheryte.RowID);
                        config.Save(this);
                    }
                }
            }
        }

        hoverProgress.TryAdd(aetheryte.RowID, 0f);

        var targetProgress  = isHovered ? 1f : 0f;
        var speed           = ImGui.GetIO().DeltaTime * 12f;
        var currentProgress = hoverProgress[aetheryte.RowID];

        if (Math.Abs(currentProgress - targetProgress) > 0.001f)
        {
            currentProgress                += (targetProgress - currentProgress) * Math.Min(speed, 1.0f);
            currentProgress                =  Math.Clamp(currentProgress, 0f, 1f);
            hoverProgress[aetheryte.RowID] =  currentProgress;
        }

        var animOffset      = currentProgress * 8.0f;
        var indicatorHeight = itemHeight      * 0.7f * currentProgress;

        var drawList    = ImGui.GetWindowDrawList();
        var baseColor   = ImGui.GetColorU32(ImGuiCol.FrameBgHovered);
        var activeColor = ImGui.GetColorU32(ImGuiCol.FrameBgActive);

        uint bgCol = 0;

        if (isActive)
            bgCol = activeColor;
        else if (currentProgress > 0.01f)
        {
            var alpha = (uint)(currentProgress * (baseColor >> 24 & 0xFF));
            bgCol = baseColor & 0x00FFFFFF | alpha << 24;
        }

        if (bgCol != 0)
        {
            drawList.AddRectFilledMultiColor
            (
                startPos,
                startPos + new Vector2(width, itemHeight),
                bgCol,
                bgCol & 0x00FFFFFF | ((uint)((bgCol >> 24) * 0.5f) & 0xFF) << 24,
                bgCol & 0x00FFFFFF | ((uint)((bgCol >> 24) * 0.5f) & 0xFF) << 24,
                bgCol
            );
        }

        if (currentProgress > 0.01f)
        {
            var indicatorColor = ImGui.GetColorU32(ImGuiCol.CheckMark);

            switch (aetheryte.State)
            {
                case AetheryteRecordState.Home:
                    indicatorColor = 0xFF00A5FF;
                    break;
                case AetheryteRecordState.Favorite:
                    indicatorColor = 0xFF00D7FF;
                    break;
            }

            var centerY = startPos.Y + itemHeight / 2;
            drawList.AddRectFilled
            (
                startPos with { Y = centerY - indicatorHeight / 2 },
                new Vector2(startPos.X + 3f, centerY + indicatorHeight / 2),
                indicatorColor,
                1.5f
            );
        }

        SeString iconStr = null;

        switch (aetheryte.State)
        {
            case AetheryteRecordState.Home: iconStr = HomeChar; break;
            case AetheryteRecordState.Free:
            case AetheryteRecordState.FreePS: iconStr = FreeChar; break;
            case AetheryteRecordState.Favorite: iconStr = FavoriteChar; break;
        }

        var contentStartX = startPos.X + padding + animOffset;
        var iconWidth     = lineHeight * 1.2f;

        if (iconStr != null)
        {
            var iconY = startPos.Y + (itemHeight - lineHeight) / 2;
            ImGui.SetCursorScreenPos(new Vector2(contentStartX, iconY));
            ImGuiHelpers.SeStringWrapped(iconStr.Encode());
        }

        contentStartX += iconWidth + padding;

        var titleY = startPos.Y + padding;
        drawList.AddText(new Vector2(contentStartX, titleY), ImGui.GetColorU32(ImGuiCol.Text), displayName);

        using (FontManager.Instance().UIFont80.Push())
        {
            var subY = titleY + lineHeight + 2f * GlobalUIScale;
            drawList.AddText(new Vector2(contentStartX, subY), ImGui.GetColorU32(ImGuiCol.TextDisabled), aetheryte.GetZone().ExtractPlaceName());
        }

        var costText    = $"{cost}";
        var costStrFull = $"{costText}\uE049";
        var costSize    = ImGui.CalcTextSize(costStrFull);
        var costPos     = new Vector2(startPos.X + width - costSize.X - padding * 2 - animOffset, startPos.Y + (itemHeight - costSize.Y) / 2);

        drawList.AddText(costPos, ImGui.GetColorU32(ImGuiCol.Text), costStrFull);

#if DEBUG
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            var localPos = Control.GetLocalPlayer()->Position;
            ImGui.SetClipboardText
            (
                $"// {aetheryte.Name}\n" +
                $"[{aetheryte.RowID}] = new({localPos.X:F2}f, {localPos.Y + 0.1f:F2}f, {localPos.Z:F2}f),"
            );
        }
#endif

        if (!ImGui.IsPopupOpen("AetheryteContextPopup") && isHovered)
        {
            hoveredAetheryte           = aetheryte;
            isNeedToLoseFocusSearchBar = true;
        }

        ImGui.SetCursorScreenPos(startPos + new Vector2(0, itemHeight + 3));
    }

    private void DrawHoveredTooltip()
    {
        if (hoveredAetheryte != null && PluginConfig.Instance().ConflictKeyBinding.IsPressed())
            pinnedAetheryte = hoveredAetheryte;

        if (pinnedAetheryte != null)
        {
            ImGui.SetNextWindowBgAlpha(0.8f);

            if (ImGui.Begin
                (
                    "###PinnedAetheryteMap",
                    ImGuiWindowFlags.NoDecoration     |
                    ImGuiWindowFlags.AlwaysAutoResize |
                    ImGuiWindowFlags.NoSavedSettings
                ))
            {
                DrawAetheryteMap(DService.Instance().Texture.GetFromGame(pinnedAetheryte.GetMap().GetTexturePath()), pinnedAetheryte, true);

                if (!ImGui.IsWindowFocused() && !ImGui.IsPopupOpen("BetterTeleport_Map_ContextMenu"))
                {
                    pinnedAetheryte = null;
                    config.Save(this);
                }

                ImGui.End();
            }
        }

        if (hoveredAetheryte == null)
        {
            lastHoveredAetheryte = null;
            return;
        }

        if (pinnedAetheryte != null && hoveredAetheryte.RowID == pinnedAetheryte.RowID)
            return;

        if (lastHoveredAetheryte != hoveredAetheryte)
        {
            lastHoveredAetheryte = hoveredAetheryte;
            hoverStartTime       = (float)ImGui.GetTime();
        }

        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero))
        using (ImRaii.PushColor(ImGuiCol.PopupBg, ImGui.GetColorU32(ImGuiCol.WindowBg)))
        using (ImRaii.Tooltip())
            DrawAetheryteMap(DService.Instance().Texture.GetFromGame(hoveredAetheryte.GetMap().GetTexturePath()), hoveredAetheryte, false);
    }

    private void DrawAetheryteMap(ISharedImmediateTexture tex, AetheryteRecord aetheryte, bool isPinned)
    {
        var drawList = ImGui.GetWindowDrawList();
        var warp     = tex.GetWrapOrEmpty();
        if (warp.Handle == nint.Zero || warp.Width < 64 || warp.Height < 64) return;

        if (pinnedAetheryte != null && ImGui.IsWindowHovered() && ImGui.GetIO().MouseWheel != 0)
        {
            config.MapZoom += ImGui.GetIO().MouseWheel * 0.1f;
            config.MapZoom =  Math.Clamp(config.MapZoom, 0.2f, 4.0f);
        }

        var widthScale = Math.Min(1f, warp.Width / 2048f);
        var imageSize  = ScaledVector2(384f      * widthScale * config.MapZoom);
        var scale      = imageSize.X / 2048f;

        if (scale <= 0.001f) return;

        if (!isPinned)
            ImGuiOm.ScaledDummy(0f, 2f);

        var hint     = isPinned ? Lang.Get("BetterTeleport-MapHint-Zoom") : Lang.Get("BetterTeleport-MapHint-Pin");
        var hintSize = ImGui.CalcTextSize(hint);
        if (imageSize.X > hintSize.X)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (imageSize.X - hintSize.X) / 2);
        ImGui.TextDisabled(hint);

        var orig = ImGui.GetCursorScreenPos();

        ImGui.Image(warp.Handle, imageSize);

        if (isPinned &&
            ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            var mousePos   = ImGui.GetMousePos();
            var relPos     = mousePos - orig;
            var texturePos = relPos / scale;
            var worldPos   = PositionHelper.TextureToWorld(texturePos, aetheryte.GetMap());

            var nearest = AllRecords.Where(x => x.GetZone().RowId == aetheryte.GetZone().RowId)
                                    .MinBy(x => Vector2.DistanceSquared(new(x.Position.X, x.Position.Z), worldPos));

            if (nearest != null)
            {
                contextMenuTargetZone = nearest.ZoneID;
                contextMenuTargetPos  = worldPos.ToVector3(nearest.Position.Y);
                ImGui.OpenPopup("BetterTeleport_Map_ContextMenu");
            }
        }

        using (var popup = ImRaii.Popup("BetterTeleport_Map_ContextMenu"))
        {
            if (popup)
            {
                if (ImGui.MenuItem(Lang.Get("BetterTeleport-TeleportToThisPosition")))
                {
                    if (GameState.TerritoryType != contextMenuTargetZone || IsWithPermission())
                    {
                        TaskHelper.Enqueue(() => MovementManager.Instance().TPSmart_BetweenZone(contextMenuTargetZone, contextMenuTargetPos));
                        TaskHelper.Enqueue
                        (() =>
                            {
                                if (MovementManager.Instance().IsManagerBusy || DService.Instance().ObjectTable.LocalPlayer == null)
                                    return false;

                                MovementManager.Instance().TPGround();
                                if (DService.Instance().Condition.IsBetweenAreas || DService.Instance().Condition[ConditionFlag.Jumping]) return false;

                                return true;
                            }
                        );
                    }
                    else
                    {
                        TaskHelper.Enqueue(() => MovementManager.Instance().TeleportNearestAetheryte(contextMenuTargetPos, contextMenuTargetZone));
                        TaskHelper.Enqueue(() => DService.Instance().Condition.IsBetweenAreas && DService.Instance().ObjectTable.LocalPlayer != null);
                        TaskHelper.Enqueue
                        (() =>
                            {
                                if (!DService.Instance().Condition.IsBetweenAreas) return true;
                                MovementManager.Instance().TPSmart_InZone(contextMenuTargetPos, false);
                                return false;
                            }
                        );
                    }

                    ImGui.CloseCurrentPopup();
                }
            }
        }

        if (ImGui.IsPopupOpen("BetterTeleport_Map_ContextMenu"))
        {
            var texFlag = DService.Instance().Texture.GetFromGameIcon(new(60561)).GetWrapOrEmpty();

            if (texFlag.Handle != nint.Zero)
            {
                var flagPos       = PositionHelper.WorldToTexture(contextMenuTargetPos, aetheryte.GetMap()) * scale;
                var flagCenterPos = orig + flagPos;
                var flagSize      = ScaledVector2(24f * config.MapZoom);
                var flagHalfSize  = flagSize / 2;

                drawList.AddImage(texFlag.Handle, flagCenterPos - flagHalfSize, flagCenterPos + flagHalfSize);
            }
        }

        drawList.AddRect(orig, orig + imageSize, ImGui.GetColorU32(ImGuiCol.Border), 0f, ImDrawFlags.None, 2f);

        var mapID    = aetheryte.GetMap().RowId;
        var siblings = AllRecords.Where(x => x.GetMap().RowId == mapID).ToList();

        var texAetheryte = DService.Instance().Texture.GetFromGameIcon(new(60453)).GetWrapOrEmpty();
        var texAethernet = DService.Instance().Texture.GetFromGameIcon(new(60430)).GetWrapOrEmpty();

        var sizeNormal = ScaledVector2(18f * config.MapZoom);
        var sizeTarget = ScaledVector2(24f * config.MapZoom);

        foreach (var record in siblings)
        {
            if (record.RowID == aetheryte.RowID) continue;

            var recordPos = config.Positions.TryGetValue(record.RowID, out var redirected) ? redirected : record.Position;
            var pos       = PositionHelper.WorldToTexture(recordPos, record.GetMap()) * scale;
            var centerPos = orig + pos;

            var texture  = record.IsAetheryte ? texAetheryte : texAethernet;
            var halfSize = sizeNormal / 2;

            drawList.AddImage(texture.Handle, centerPos - halfSize, centerPos + halfSize, Vector2.Zero, Vector2.One, 0xCCFFFFFF);

            if (isPinned)
            {
                var min = centerPos - halfSize;
                var max = centerPos + halfSize;

                if (ImGui.IsMouseHoveringRect(min, max))
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    ImGui.SetTooltip(record.Name);

                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    {
                        HandleTeleport(record);
                        pinnedAetheryte = null;
                    }
                }
            }
        }

        {
            var recordPos = config.Positions.TryGetValue(aetheryte.RowID, out var redirected) ? redirected : aetheryte.Position;
            var pos       = PositionHelper.WorldToTexture(recordPos, aetheryte.GetMap()) * scale;
            var centerPos = orig + pos;

            var time     = (float)ImGui.GetTime();
            var animTime = time - hoverStartTime;

            var pulse = (float)Math.Sin(time * 10f) * 0.2f + 1.0f;

            var pingRadius = animTime          * 100f % 60f;
            var pingAlpha  = 1.0f - pingRadius / 60f;

            if (pingRadius > 0 && pingAlpha > 0)
            {
                var pingColor = ImGui.GetColorU32(ImGuiCol.CheckMark);
                pingColor = pingColor & 0x00FFFFFF | (uint)(pingAlpha * 255) << 24;
                drawList.AddCircle(centerPos, pingRadius, pingColor, 32, 2f);
            }

            drawList.AddCircleFilled(centerPos, 8f * pulse * GlobalUIScale, ImGui.GetColorU32(ImGuiCol.CheckMark, 0.5f));

            var texture  = aetheryte.IsAetheryte ? texAetheryte : texAethernet;
            var halfSize = sizeTarget / 2;
            drawList.AddImage(texture.Handle, centerPos - halfSize, centerPos + halfSize);

            if (isPinned)
            {
                var min = centerPos - halfSize;
                var max = centerPos + halfSize;

                if (ImGui.IsMouseHoveringRect(min, max))
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    ImGui.SetTooltip(aetheryte.Name);

                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    {
                        HandleTeleport(aetheryte);
                        pinnedAetheryte = null;
                    }
                }
            }

            var text     = config.Remarks.TryGetValue(aetheryte.RowID, out var remark) ? remark : aetheryte.Name;
            var textSize = ImGui.CalcTextSize(text);
            var padding  = ScaledVector2(2f, 3f);
            var textPos  = centerPos - new Vector2(textSize.X / 2, 20f * GlobalUIScale + textSize.Y);

            var minPos = orig             + padding;
            var maxPos = orig + imageSize - padding - textSize;

            if (minPos.X < maxPos.X)
                textPos.X = Math.Clamp(textPos.X, minPos.X, maxPos.X);

            if (minPos.Y < maxPos.Y)
                textPos.Y = Math.Clamp(textPos.Y, minPos.Y, maxPos.Y);

            drawList.AddRectFilled(textPos - padding, textPos + textSize + padding, 0x80000000, 4f);
            drawList.AddText(textPos, KnownColor.LightSkyBlue.ToVector4().ToUInt(), text);
        }
    }

    private static void DrawBottomToolbar()
    {
        var manager = InventoryManager.Instance();
        if (manager == null) return;

        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + ImGui.GetStyle().ItemSpacing.Y);

        using (ImRaii.Group())
        {
            DrawItem(1);

            ImGui.SameLine(0, 20f * GlobalUIScale);
            DrawItem(7569);
        }

        return;

        void DrawItem(uint itemID)
        {
            if (!LuminaGetter.TryGetRow<Item>(itemID, out var row)) return;
            if (!DService.Instance().Texture.TryGetFromGameIcon(new(row.Icon), out var texture)) return;

            var iconSize = new Vector2(ImGui.GetTextLineHeight());
            ImGui.Image(texture.GetWrapOrEmpty().Handle, iconSize);

            ImGui.SameLine(0, 5f * GlobalUIScale);

            var text = $"{row.Name}: {manager->GetInventoryItemCount(itemID)}";
            ImGui.TextUnformatted(text);
        }
    }

    private void HandleTeleport(AetheryteRecord aetheryte)
    {
        if (GameState.ContentFinderCondition != 0) return;

        TaskHelper.Abort();

        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return;

        var hasRedirect  = config.Positions.TryGetValue(aetheryte.RowID, out var redirected);
        var aetherytePos = hasRedirect ? redirected : aetheryte.Position;

        var isSameZone = aetheryte.ZoneID == GameState.TerritoryType;
        var distance2D = !isSameZone
                             ? 999
                             : Vector2.DistanceSquared(localPlayer->Position.ToVector2(), aetherytePos.ToVector2());
        if (distance2D <= 900) return;

        var isPosDefault = aetherytePos.Y == 0;

        NotifyHelper.Instance().NotificationInfo(Lang.Get("BetterTeleport-Notification", aetheryte.Name));

        searchWord = string.Empty;
        searchResult.Clear();
        pinnedAetheryte  = null;
        hoveredAetheryte = null;
        Overlay.IsOpen   = false;

        switch (aetheryte.Group)
        {
            // 房区
            case 255:
                Telepo.Instance()->Teleport(aetheryte.RowID, aetheryte.SubIndex);
                return;
            // 天穹街
            case 254:
                TaskHelper.Enqueue(MovementManager.Instance().TeleportFirmament, "天穹街");
                TaskHelper.Enqueue
                (
                    () => GameState.TerritoryType  == 886  &&
                          Control.GetLocalPlayer() != null &&
                          !MovementManager.Instance().IsManagerBusy,
                    "等待天穹街"
                );
                TaskHelper.Enqueue(() => MovementManager.Instance().TPSmart_InZone(aetherytePos), "区域内TP");
                TaskHelper.Enqueue
                (
                    () =>
                    {
                        if (MovementManager.Instance().IsManagerBusy || DService.Instance().ObjectTable.LocalPlayer == null)
                            return false;

                        MovementManager.Instance().TPGround();
                        return true;
                    },
                    "TP到地面"
                );
                return;
            // 野外大水晶直接传
            case 0:
                var direction = !isPosDefault
                                    ? new()
                                    : Vector2.Normalize(((Vector3)localPlayer->Position).ToVector2() - aetherytePos.ToVector2());
                var offset = direction * 10;

                TaskHelper.Enqueue(() => MovementManager.Instance().TPSmart_BetweenZone(aetheryte.ZoneID, aetherytePos + offset.ToVector3(0)));

                if (isPosDefault)
                {
                    TaskHelper.Enqueue
                    (() =>
                        {
                            if (MovementManager.Instance().IsManagerBusy                ||
                                DService.Instance().Condition.IsBetweenAreas ||
                                !UIModule.IsScreenReady()                    ||
                                DService.Instance().Condition.Any(ConditionFlag.Mounted))
                                return false;
                            MovementManager.Instance().TPGround();
                            return true;
                        }
                    );
                }

                return;
        }

        // 当前在有小水晶的城区
        if (GameState.TerritoryType == aetheryte.ZoneID && aetheryte.Group != 0)
        {
            // 大水晶才要偏移一下
            var offset = new Vector3();

            if (aetheryte.IsAetheryte)
            {
                var direction = !isPosDefault
                                    ? new()
                                    : Vector3.Normalize((Vector3)localPlayer->Position - aetherytePos);
                offset = direction * 10;
            }

            TaskHelper.Enqueue(() => MovementManager.Instance().TPSmart_InZone(aetherytePos + offset));

            if (isPosDefault)
            {
                TaskHelper.Enqueue
                (() =>
                    {
                        if (MovementManager.Instance().IsManagerBusy                ||
                            DService.Instance().Condition.IsBetweenAreas ||
                            !UIModule.IsScreenReady()                    ||
                            DService.Instance().Condition.Any(ConditionFlag.Mounted))
                            return false;
                        MovementManager.Instance().TPGround();
                        return true;
                    }
                );
            }

            return;
        }

        // 先获取当前区域任一水晶
        var aetheryteInThisZone = MovementManager.GetNearestAetheryte(Control.GetLocalPlayer()->Position, GameState.TerritoryType);

        // 获取不到水晶 / 不属于同一组水晶 / 附近没有能交互到的水晶 → 直接传
        if (!isSameZone && aetheryte.Group == 0          ||
            aetheryteInThisZone       == null            ||
            aetheryteInThisZone.Group != aetheryte.Group ||
            !EventFramework.Instance()->TryGetNearestEventID
            (
                x => x.EventId.ContentId is EventHandlerContent.Aetheryte,
                _ => true,
                DService.Instance().ObjectTable.LocalPlayer.Position,
                out var eventIDAetheryte
            ))
        {
            // 大水晶直接传
            if (aetheryte.IsAetheryte)
            {
                Telepo.Instance()->Teleport(aetheryte.RowID, aetheryte.SubIndex);

                if (hasRedirect)
                {
                    TaskHelper.Enqueue(() => GameState.TerritoryType == aetheryte.ZoneID && Control.GetLocalPlayer() != null);
                    TaskHelper.Enqueue(() => MovementManager.Instance().TPSmart_InZone(aetherytePos));
                }

                return;
            }

            TaskHelper.Enqueue(() => MovementManager.Instance().TPSmart_BetweenZone(aetheryte.ZoneID, aetherytePos));

            if (isPosDefault)
            {
                TaskHelper.Enqueue
                (() =>
                    {
                        if (MovementManager.Instance().IsManagerBusy                ||
                            DService.Instance().Condition.IsBetweenAreas ||
                            !UIModule.IsScreenReady()                    ||
                            DService.Instance().Condition.Any(ConditionFlag.Mounted))
                            return false;
                        MovementManager.Instance().TPGround();
                        return true;
                    }
                );
            }

            return;
        }

        TaskHelper.Enqueue(() => !DService.Instance().Condition.IsOccupiedInEvent);
        if (!TelepotTown->IsAddonAndNodesReady())
            TaskHelper.Enqueue(() => new EventStartPackt(Control.GetLocalPlayer()->EntityId, eventIDAetheryte).Send());
        TaskHelper.Enqueue
        (() =>
            {
                AddonSelectStringEvent.Select(["都市传送网", "Aethernet", "都市転送網"]);

                var agent = AgentTelepotTown.Instance();
                if (agent == null || !agent->IsAgentActive()) return false;

                AgentId.TelepotTown.SendEvent(1, 11, (uint)aetheryte.SubIndex);
                AgentId.TelepotTown.SendEvent(1, 11, (uint)aetheryte.SubIndex);
                return true;
            }
        );

        if (hasRedirect)
        {
            TaskHelper.Enqueue(() => GameState.TerritoryType == aetheryte.ZoneID && Control.GetLocalPlayer() != null);
            TaskHelper.Enqueue(() => MovementManager.Instance().TPSmart_InZone(aetherytePos));
        }
    }

    private void OnPostUseAction
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
        if (actionType != ActionType.GeneralAction || actionID != 7)
            return;

        isPrevented = true;

        if (GameMain.Instance()->CurrentContentFinderConditionId != 0 ||
            isRefreshing                                              ||
            DService.Instance().Condition.IsBetweenAreas              ||
            Control.GetLocalPlayer() == null                          ||
            !UIModule.IsScreenReady())
            return;

        UIGlobals.PlaySoundEffect(23);
        Overlay.IsOpen ^= true;
    }

    private void OnZoneChanged(ushort zone)
    {
        Overlay.IsOpen = false;
        TaskHelper.RemoveQueueTasks(1);

        if (GameState.TerritoryType == 0 || GameState.ContentFinderCondition != 0 || !DService.Instance().ClientState.IsLoggedIn) return;

        TaskHelper.Enqueue
        (
            () =>
            {
                try
                {
                    isRefreshing = true;

                    if (DService.Instance().ObjectTable.LocalPlayer is null || DService.Instance().Condition.IsBetweenAreas) return false;

                    var instance = Telepo.Instance();
                    if (instance == null) return false;

                    var otherName = LuminaWrapper.GetAddonText(832);

                    RefreshHouseInfo();

                    records.Clear();

                    foreach (var aetheryte in MovementManager.Aetherytes)
                    {
                        if (!aetheryte.IsUnlocked()) continue;

                        if (aetheryte.Group == 5)
                        {
                            records.TryAdd(otherName, []);
                            records[otherName].Add(aetheryte);
                        }
                        else if (aetheryte.Version == 0)
                        {
                            var regionRow  = aetheryte.GetZone().PlaceNameRegion.Value;
                            var regionName = regionRow.RowId is 22 or 23 or 24 ? aetheryte.GetZone().PlaceNameRegion.Value.Name.ToString() : otherName;

                            records.TryAdd(regionName, []);
                            records[regionName].Add(aetheryte);
                        }
                        else
                        {
                            var versionName = $"{aetheryte.Version + 2}.0";

                            records.TryAdd(versionName, []);
                            records[versionName].Add(aetheryte);
                        }
                    }

                    RefreshHwdInfo();

                    RefreshFavoritesInfo();
                }
                finally
                {
                    isRefreshing = false;
                }

                AllRecords.ForEach(x => TaskHelper.Enqueue(x.Update, $"更新 {x.Name} 信息", weight: -3));

                return true;
            },
            "初始化信息",
            weight: 1
        );
    }

    private void OnCommand(string command, string args)
    {
        args = args.Trim();
        if (string.IsNullOrWhiteSpace(args)) return;

        var result = records.Values
                            .SelectMany(x => x)
                            .Concat(houseRecords)
                            .Where
                            (x =>
                                {
                                    var name = string.Empty;

                                    try
                                    {
                                        name = x.ToString();
                                    }
                                    catch
                                    {
                                        // ignored
                                    }

                                    return name.Contains(args, StringComparison.OrdinalIgnoreCase);
                                }
                            )
                            .OrderByDescending(x => x.IsAetheryte)
                            .ThenBy(x => x.Name.Length)
                            .FirstOrDefault();

        if (result == null) return;

        HandleTeleport(result);
    }

    private static bool IsWithPermission() =>
        !(GameState.IsCN || GameState.IsTC) || AuthState.IsPremium || Sheets.SpeedDetectionZones.ContainsKey(GameState.TerritoryType);
    
    #region Data

    private void RefreshFavoritesInfo()
    {
        if (config.Favorites.Count == 0) return;
        favorites = config.Favorites
                                .Select(x => AllRecords.FirstOrDefault(d => d.RowID == x))
                                .Where(x => x != null)
                                .OfType<AetheryteRecord>()
                                .OrderBy(x => x.RowID)
                                .ToList();
    }

    private void RefreshHouseInfo()
    {
        houseRecords.Clear();

        foreach (var aetheryte in DService.Instance().AetheryteList)
        {
            if (!HouseZones.Contains(aetheryte.TerritoryID)) continue;
            if (!LuminaGetter.TryGetRow<Aetheryte>(aetheryte.AetheryteID, out var aetheryteRow)) continue;
            if (!LuminaGetter.TryGetRow<TerritoryType>(aetheryte.TerritoryID, out var row)) continue;

            var shareHouseName = string.Empty;

            if (aetheryte.IsSharedHouse)
            {
                var rawAddonText = LuminaGetter.GetRow<Addon>(6724)!.Value.Text.ToDalamudString();
                rawAddonText.Payloads[3] = new TextPayload(aetheryte.Ward.ToString());
                rawAddonText.Payloads[5] = new TextPayload(aetheryte.Plot.ToString());

                shareHouseName = rawAddonText.ToString();
            }

            var name = string.Empty;
            if (aetheryte.IsSharedHouse)
                name = shareHouseName;
            else if (aetheryte.IsApartment)
                name = LuminaWrapper.GetAddonText(6710);
            else
                name = aetheryteRow.PlaceName.Value.Name.ToString();

            var record = new AetheryteRecord
            (
                aetheryte.AetheryteID,
                aetheryte.SubIndex,
                255,
                0,
                aetheryte.TerritoryID,
                row.Map.RowId,
                true,
                new(aetheryte.Ward, aetheryte.SubIndex, 0),
                $"{aetheryteRow.Territory.Value.ExtractPlaceName()} {name}"
            );

            houseRecords.Add(record);
        }
    }

    // 天穹街
    private void RefreshHwdInfo()
    {
        var markers = LuminaGetter.GetRowOrDefault<TerritoryType>(886)
                                  .GetMapMarkers()
                                  .Where(x => x.DataType is 3 or 4)
                                  .Select
                                  (x => new
                                      {
                                          Name     = AetheryteRecord.TryParseName(x, out var markerName) ? markerName : string.Empty,
                                          Position = PositionHelper.TextureToWorld(new(x.X, x.Y), LuminaGetter.GetRow<Map>(574)!.Value).ToVector3(0),
                                          Marker   = x
                                      }
                                  )
                                  .DistinctBy(x => x.Name);

        byte indexCounter = 0;

        foreach (var marker in markers)
        {
            var record = new AetheryteRecord(70, indexCounter, 254, 1, 886, 574, false, marker.Position, marker.Name);

            records.TryAdd("3.0", []);
            records["3.0"].Add(record);

            indexCounter++;
        }
    }

    #endregion

    private class Config : ModuleConfig
    {
        public HashSet<uint>             Favorites            = [];
        public bool                      HideAethernetInParty = true;
        public float                     MapZoom              = 1f;
        public Dictionary<uint, Vector3> Positions            = [];
        public Dictionary<uint, string>  Remarks              = [];
    }
    
    #region 常量

    private const string COMMAND = "/pdrtelepo";

    private static readonly SeString HomeChar     = new SeStringBuilder().AddIcon(BitmapFontIcon.OrangeDiamond).Build();
    private static readonly SeString FreeChar     = new SeStringBuilder().AddIcon(BitmapFontIcon.GoldStar).Build();
    private static readonly SeString FavoriteChar = new SeStringBuilder().AddIcon(BitmapFontIcon.SilverStar).Build();

    private static readonly FrozenSet<uint> HouseZones = [339, 340, 341, 641, 979];

    private static Dictionary<uint, string> TicketUsageTypes
    {
        get
        {
            if (field != null)
                return field;
            
            field = [];
            for (var i = 0U; i < 5; i++)
            {
                var addonOffset       = i + 8523U;
                var optionDescription = LuminaWrapper.GetAddonText(addonOffset);
                field[i] = optionDescription;
            }
            
            return field;
        }
    }

    #endregion
}
