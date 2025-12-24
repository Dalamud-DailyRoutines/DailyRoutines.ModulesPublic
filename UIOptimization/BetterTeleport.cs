using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using Map = Lumina.Excel.Sheets.Map;

namespace DailyRoutines.ModulesPublic;

public unsafe class BetterTeleport : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = GetLoc("BetterTeleportTitle"),
        Description         = GetLoc("BetterTeleportDescription"),
        Category            = ModuleCategories.UIOptimization,
        ModulesPrerequisite = ["SameAethernetTeleport"]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private const string Command = "/pdrtelepo";

    // Icon ID - Record
    private static readonly Dictionary<string, List<AetheryteRecord>> Records      = [];
    private static readonly List<AetheryteRecord>                     HouseRecords = [];

    private static readonly SeString HomeChar      = new SeStringBuilder().AddIcon(BitmapFontIcon.OrangeDiamond).Build();
    private static readonly SeString FreeChar      = new SeStringBuilder().AddIcon(BitmapFontIcon.GoldStar).Build();
    private static readonly SeString FavoriteChar  = new SeStringBuilder().AddIcon(BitmapFontIcon.SilverStar).Build();

    private static readonly HashSet<uint> HouseZones = [339, 340, 341, 641, 979];

    private static readonly Dictionary<uint, string> TicketUsageTypes = [];

    private static uint TicketUsageType
    {
        get => DService.GameConfig.UiConfig.GetUInt("TelepoTicketUseType");
        set => DService.GameConfig.UiConfig.Set("TelepoTicketUseType", value);
    }

    private static uint TicketUsageGilSetting
    {
        get => DService.GameConfig.UiConfig.GetUInt("TelepoTicketGilSetting");
        set => DService.GameConfig.UiConfig.Set("TelepoTicketGilSetting", value);
    }

    private static Config ModuleConfig = null!;

    private static bool IsRefreshing;

    private static string                SearchWord   = string.Empty;
    private static List<AetheryteRecord> SearchResult = [];
    private static List<AetheryteRecord> Favorites    = [];
    private static bool                  IsNeedToLoseFocusSearchBar;

    private static IEnumerable<AetheryteRecord> AllRecords =>
        Records.Values.SelectMany(x => x).Concat(HouseRecords);

    static BetterTeleport()
    {
        for (var i = 0U; i < 5; i++)
        {
            var addonOffset       = i + 8523U;
            var optionDescription = LuminaWrapper.GetAddonText(addonOffset);
            TicketUsageTypes[i] = optionDescription;
        }
    }

    private static readonly Dictionary<uint, float> HoverProgress = [];
    private static          float                   HoverStartTime;
    
    private static AetheryteRecord? HoveredAetheryte;
    private static AetheryteRecord? LastHoveredAetheryte;
    private static AetheryteRecord? PinnedAetheryte;
    
    private static Vector3 ContextMenuTargetPos;
    private static uint    ContextMenuTargetZone;

    protected override void Init()
    {
        Overlay            ??= new(this);
        Overlay.Flags      &=  ~ImGuiWindowFlags.AlwaysAutoResize;
        Overlay.Flags      &=  ~ImGuiWindowFlags.NoTitleBar;
        Overlay.Flags      |=  ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        Overlay.WindowName =   $"{LuminaWrapper.GetAddonText(8513)}###BetterTeleportOverlay";

        TaskHelper ??= new() { TimeLimitMS = 60_000 };

        ModuleConfig = LoadConfig<Config>() ?? new();

        DService.ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(DService.ClientState.TerritoryType);

        CommandManager.AddCommand(Command, new(OnCommand) { HelpMessage = GetLoc("BetterTeleport-CommandHelp") });

        UseActionManager.RegPreUseAction(OnPostUseAction);
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("Command")}:");

        ImGui.SameLine();
        ImGui.TextWrapped($"{Command} {GetLoc("BetterTeleport-CommandHelp")}");
    }

    protected override void OverlayUI()
    {
        HoveredAetheryte = null;

        switch (IsRefreshing)
        {
            case false when !TaskHelper.IsBusy && Records.Count == 0:
                OnZoneChanged(DService.ClientState.TerritoryType);
                return;
            case true:
                return;
        }

        if (DService.KeyState[VirtualKey.ESCAPE])
        {
            Overlay.IsOpen = false;
            if (SystemMenu != null)
                SystemMenu->Close(true);
        }

        var isSearchEmpty = string.IsNullOrWhiteSpace(SearchWord);


        var searchBarID = "###Search";
        if (IsNeedToLoseFocusSearchBar)
        {
            searchBarID                = "###Search_LoseFocus";
            IsNeedToLoseFocusSearchBar = false;
        }
        ImGui.SetNextItemWidth(isSearchEmpty ? -1f : -ImGui.GetFrameHeight() - ImGui.GetStyle().ItemSpacing.X);
        if (ImGui.InputTextWithHint(searchBarID, GetLoc("PleaseSearch"), ref SearchWord, 128))
        {
            SearchResult = !string.IsNullOrWhiteSpace(SearchWord)
                               ? Records.Values
                                        .SelectMany(x => x)
                                        .Where(x => x.ToString()
                                                     .Contains(SearchWord, StringComparison.OrdinalIgnoreCase) ||
                                                    (ModuleConfig.Remarks.TryGetValue(x.RowID, out var remark) &&
                                                     remark.Contains(SearchWord, StringComparison.OrdinalIgnoreCase)))
                                        .ToList()
                               : [];
        }

        if (!isSearchEmpty)
        {
            ImGui.SameLine();
            if (ImGuiOm.ButtonIcon("Clear", FontAwesomeIcon.Times))
            {
                SearchWord   = string.Empty;
                SearchResult = [];
            }
        }

        ImGui.Spacing();
        
        if (SearchResult.Count > 0 || !isSearchEmpty)
        {
            using var child = ImRaii.Child("###SearchResultChild", new Vector2(0, -ImGui.GetFrameHeightWithSpacing()), false, ImGuiWindowFlags.NoBackground);
            if (child)
            {
                if (SearchResult.Count != 0)
                {
                    foreach (var aetheryte in SearchResult.ToList())
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

                if (Favorites.Count > 0)
                {
                    using var tabItem = ImRaii.TabItem($"{GetLoc("Favorite")}##TabItem");
                    if (tabItem)
                    {
                        var       childSize = new Vector2(0, -ImGui.GetFrameHeightWithSpacing());
                        using var child     = ImRaii.Child("###FavoriteChild", childSize, false, ImGuiWindowFlags.NoBackground);
                        if (child)
                        {
                            foreach (var aetheryte in Favorites.ToList())
                                DrawAetheryte(aetheryte);
                        }
                    }
                }

                var agentLobby = AgentLobby.Instance();
                if (agentLobby != null)
                {
                    foreach (var (name, aetherytes) in Records.ToList())
                    {
                        using var tabItem = ImRaii.TabItem($"{name}##TabItem");
                        if (!tabItem) continue;

                        var       childSize = new Vector2(0, -ImGui.GetFrameHeightWithSpacing());
                        using var child     = ImRaii.Child($"###{name}Child", childSize, false, ImGuiWindowFlags.NoBackground);
                        if (!child) continue;

                        var source      = name == LuminaWrapper.GetAddonText(832) ? HouseRecords.Concat(aetherytes) : aetherytes;
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

                                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetStyle().ItemSpacing.X * 2));
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

                        ImGui.Text($"{LuminaWrapper.GetAddonText(8522)}");

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

                        ImGui.Text($"{LuminaWrapper.GetAddonText(8528)}");

                        var gilSetting = TicketUsageGilSetting;
                        if (ImGui.InputUInt("###GilInput", ref gilSetting))
                            TicketUsageGilSetting = gilSetting;

                        if (ImGui.Checkbox(GetLoc("BetterTeleport-HideAethernetInParty"), ref ModuleConfig.HideAethernetInParty))
                            SaveConfig(ModuleConfig);
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
        ModuleConfig.Save(this);

    private void DrawAetheryte(AetheryteRecord aetheryte)
    {
        if (ModuleConfig.HideAethernetInParty && !aetheryte.IsAetheryte && DService.PartyList.Length > 1)
            return;

        var hasRemark   = ModuleConfig.Remarks.TryGetValue(aetheryte.RowID, out var remark);
        var displayName = hasRemark ? remark : aetheryte.Name;
        var cost        = aetheryte.Cost;

        using var id = ImRaii.PushId($"{aetheryte}");

        var startPos   = ImGui.GetCursorScreenPos();
        var width      = ImGui.GetContentRegionAvail().X;
        var lineHeight = ImGui.GetTextLineHeight();
        var padding    = ImGui.GetStyle().ItemSpacing.X;
        var itemHeight = (lineHeight * 2.2f) + padding;

        if (ImGui.InvisibleButton("##ItemBtn", new Vector2(width, itemHeight)))
            HandleTeleport(aetheryte);
        var isHovered = ImGui.IsItemHovered();
        var isActive  = ImGui.IsItemActive();

        using (var context = ImRaii.ContextPopupItem("AetheryteContextPopup"))
        {
            if (context)
            {
                ImGui.Text($"{aetheryte.Name}");

                ImGui.Separator();
                ImGui.Spacing();

                if (ImGui.MenuItem(GetLoc("Favorite"), string.Empty, ModuleConfig.Favorites.Contains(aetheryte.RowID)))
                {
                    if (!ModuleConfig.Favorites.Add(aetheryte.RowID))
                        ModuleConfig.Favorites.Remove(aetheryte.RowID);

                    RefreshFavoritesInfo();
                    SaveConfig(ModuleConfig);
                }

                ImGui.Separator();
                ImGui.Spacing();

                ImGui.Text(GetLoc("Note"));

                var input = hasRemark ? remark : string.Empty;
                ImGui.SetNextItemWidth(Math.Max(150f * GlobalFontScale, ImGui.CalcTextSize(aetheryte.Name).X));
                if (ImGui.InputText("###Note", ref input, 128))
                {
                    if (string.IsNullOrWhiteSpace(input))
                        ModuleConfig.Remarks.Remove(aetheryte.RowID);
                    else
                        ModuleConfig.Remarks[aetheryte.RowID] = input;
                }

                if (ImGui.IsItemDeactivatedAfterEdit())
                    SaveConfig(ModuleConfig);

                ImGui.Separator();
                ImGui.Spacing();

                ImGui.Text(GetLoc("Position"));

                var hasPosition = ModuleConfig.Positions.TryGetValue(aetheryte.RowID, out var position);
                using (FontManager.UIFont60.Push())
                    ImGui.Text($"{(hasPosition ? position : aetheryte.Position):F1}");
                if (ImGui.MenuItem(GetLoc("BetterTeleport-RedirectedToCurrentPos")))
                {
                    ModuleConfig.Positions[aetheryte.RowID] = Control.GetLocalPlayer()->Position;
                    SaveConfig(ModuleConfig);
                }

                using (ImRaii.Disabled(!ModuleConfig.Positions.ContainsKey(aetheryte.RowID)))
                {
                    if (ImGui.MenuItem($"{GetLoc("Clear")}###DeleteRedirected"))
                    {
                        ModuleConfig.Positions.Remove(aetheryte.RowID);
                        SaveConfig(ModuleConfig);
                    }
                }
            }
        }

        HoverProgress.TryAdd(aetheryte.RowID, 0f);

        var targetProgress  = isHovered ? 1f : 0f;
        var speed           = ImGui.GetIO().DeltaTime * 12f;
        var currentProgress = HoverProgress[aetheryte.RowID];

        if (Math.Abs(currentProgress - targetProgress) > 0.001f)
        {
            currentProgress                += (targetProgress - currentProgress) * Math.Min(speed, 1.0f);
            currentProgress                =  Math.Clamp(currentProgress, 0f, 1f);
            HoverProgress[aetheryte.RowID] =  currentProgress;
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
            var alpha = (uint)(currentProgress * ((baseColor >> 24) & 0xFF));
            bgCol = (baseColor & 0x00FFFFFF) | (alpha << 24);
        }

        if (bgCol != 0)
            drawList.AddRectFilledMultiColor(
                startPos,
                startPos + new Vector2(width, itemHeight),
                bgCol,
                (bgCol & 0x00FFFFFF) | (((uint)((bgCol >> 24) * 0.5f) & 0xFF) << 24),
                (bgCol & 0x00FFFFFF) | (((uint)((bgCol >> 24) * 0.5f) & 0xFF) << 24),
                bgCol
            );

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

            var centerY = startPos.Y + (itemHeight / 2);
            drawList.AddRectFilled(
                startPos with { Y = centerY - (indicatorHeight               / 2) },
                new Vector2(startPos.X          + 3f, centerY + (indicatorHeight / 2)),
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
            var iconY = startPos.Y + ((itemHeight - lineHeight) / 2);
            ImGui.SetCursorScreenPos(new Vector2(contentStartX, iconY));
            ImGuiHelpers.SeStringWrapped(iconStr.Encode());
        }

        contentStartX += iconWidth + padding;
        
        var titleY = startPos.Y + padding;
        drawList.AddText(new Vector2(contentStartX, titleY), ImGui.GetColorU32(ImGuiCol.Text), displayName);

        using (FontManager.UIFont80.Push())
        {
            var subY = titleY + lineHeight + (2f * GlobalFontScale);
            drawList.AddText(new Vector2(contentStartX, subY), ImGui.GetColorU32(ImGuiCol.TextDisabled), aetheryte.GetZone().ExtractPlaceName());
        }

        var costText    = $"{cost}";
        var costStrFull = $"{costText}\uE049";
        var costSize    = ImGui.CalcTextSize(costStrFull);
        var costPos     = new Vector2(startPos.X + width - costSize.X - (padding * 2) - animOffset, startPos.Y + ((itemHeight - costSize.Y) / 2));

        drawList.AddText(costPos, ImGui.GetColorU32(ImGuiCol.Text), costStrFull);
        
#if DEBUG
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            var localPos = Control.GetLocalPlayer()->Position;
            ImGui.SetClipboardText($"// {aetheryte.Name}\n" +
                                   $"[{aetheryte.RowID}] = new({localPos.X:F2}f, {localPos.Y + 0.1f:F2}f, {localPos.Z:F2}f),");
        }
#endif

        if (!ImGui.IsPopupOpen("AetheryteContextPopup") && isHovered)
        {
            HoveredAetheryte           = aetheryte;
            IsNeedToLoseFocusSearchBar = true;
        }
        
        ImGui.SetCursorScreenPos(startPos + new Vector2(0, itemHeight + 3));
    }

    private void DrawHoveredTooltip()
    {
        if (HoveredAetheryte != null && IsConflictKeyPressed())
            PinnedAetheryte = HoveredAetheryte;

        if (PinnedAetheryte != null)
        {
            ImGui.SetNextWindowBgAlpha(0.8f);
            if (ImGui.Begin("###PinnedAetheryteMap",
                            ImGuiWindowFlags.NoDecoration       |
                            ImGuiWindowFlags.AlwaysAutoResize   |
                            ImGuiWindowFlags.NoSavedSettings))
            {
                DrawAetheryteMap(DService.Texture.GetFromGame(PinnedAetheryte.GetMap().GetTexturePath()), PinnedAetheryte, true);

                if (!ImGui.IsWindowFocused() && !ImGui.IsPopupOpen("BetterTeleport_Map_ContextMenu"))
                {
                    PinnedAetheryte = null;
                    ModuleConfig.Save(this);
                }

                ImGui.End();
            }
        }

        if (HoveredAetheryte == null)
        {
            LastHoveredAetheryte = null;
            return;
        }

        if (PinnedAetheryte != null && HoveredAetheryte.RowID == PinnedAetheryte.RowID)
            return;

        if (LastHoveredAetheryte != HoveredAetheryte)
        {
            LastHoveredAetheryte = HoveredAetheryte;
            HoverStartTime       = (float)ImGui.GetTime();
        }

        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero))
        using (ImRaii.PushColor(ImGuiCol.PopupBg, ImGui.GetColorU32(ImGuiCol.WindowBg)))
        using (ImRaii.Tooltip())
            DrawAetheryteMap(DService.Texture.GetFromGame(HoveredAetheryte.GetMap().GetTexturePath()), HoveredAetheryte, false);
    }

    private void DrawAetheryteMap(ISharedImmediateTexture tex, AetheryteRecord aetheryte, bool isPinned)
    {
        var drawList  = ImGui.GetWindowDrawList();
        var warp      = tex.GetWrapOrEmpty();
        if (warp.Handle == nint.Zero || warp.Width < 64 || warp.Height < 64) return;

        if (PinnedAetheryte != null && ImGui.IsWindowHovered() && ImGui.GetIO().MouseWheel != 0)
        {
            ModuleConfig.MapZoom += ImGui.GetIO().MouseWheel * 0.1f;
            ModuleConfig.MapZoom =  Math.Clamp(ModuleConfig.MapZoom, 0.2f, 4.0f);
        }

        var widthScale = Math.Min(1f, warp.Width / 2048f);
        var imageSize  = ScaledVector2(384f * widthScale * ModuleConfig.MapZoom);
        var scale      = imageSize.X / 2048f;

        if (scale <= 0.001f) return;

        if (!isPinned)
            ScaledDummy(0f, 2f);
        
        var hint     = isPinned ? GetLoc("BetterTeleport-MapHint-Zoom") : GetLoc("BetterTeleport-MapHint-Pin");
        var hintSize = ImGui.CalcTextSize(hint);
        if (imageSize.X > hintSize.X)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ((imageSize.X - hintSize.X) / 2));
        ImGui.TextDisabled(hint);
        
        var orig = ImGui.GetCursorScreenPos();
        
        ImGui.Image(warp.Handle, imageSize);

        if (isPinned &&
            ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            var mousePos   = ImGui.GetMousePos();
            var relPos     = mousePos - orig;
            var texturePos = relPos / scale;
            var worldPos   = TextureToWorld(texturePos, aetheryte.GetMap());

            var nearest = AllRecords.Where(x => x.GetZone().RowId == aetheryte.GetZone().RowId)
                                    .MinBy(x => Vector2.DistanceSquared(new(x.Position.X, x.Position.Z), worldPos));
            if (nearest != null)
            {
                ContextMenuTargetZone = nearest.ZoneID;
                ContextMenuTargetPos  = worldPos.ToVector3(nearest.Position.Y);
                ImGui.OpenPopup("BetterTeleport_Map_ContextMenu");
            }
        }

        using (var popup = ImRaii.Popup("BetterTeleport_Map_ContextMenu"))
        {
            if (popup)
            {
                if (ImGui.MenuItem(GetLoc("BetterTeleport-TeleportToThisPosition")))
                {
                    if (GameState.TerritoryType != ContextMenuTargetZone || IsWithPermission())
                    {
                        TaskHelper.Enqueue(() => MovementManager.TPSmart_BetweenZone(ContextMenuTargetZone, ContextMenuTargetPos));
                        TaskHelper.Enqueue(() =>
                        {
                            if (MovementManager.IsManagerBusy || DService.ObjectTable.LocalPlayer == null)
                                return false;

                            MovementManager.TPGround();
                            if (BetweenAreas || DService.Condition[ConditionFlag.Jumping]) return false;

                            return true;
                        });
                    }
                    else
                    {
                        TaskHelper.Enqueue(() => MovementManager.TeleportNearestAetheryte(ContextMenuTargetPos, ContextMenuTargetZone));
                        TaskHelper.Enqueue(() => BetweenAreas && DService.ObjectTable.LocalPlayer != null);
                        TaskHelper.Enqueue(() =>
                        {
                            if (!BetweenAreas) return true;
                            MovementManager.TPSmart_InZone(ContextMenuTargetPos, false);
                            return false;
                        });
                    }
                    
                    ImGui.CloseCurrentPopup();
                }
            }
        }
        
        if (ImGui.IsPopupOpen("BetterTeleport_Map_ContextMenu"))
        {
            var texFlag = DService.Texture.GetFromGameIcon(new(60561)).GetWrapOrEmpty();
            if (texFlag.Handle != nint.Zero)
            {
                var flagPos       = WorldToTexture(ContextMenuTargetPos, aetheryte.GetMap()) * scale;
                var flagCenterPos = orig + flagPos;
                var flagSize      = ScaledVector2(24f * ModuleConfig.MapZoom);
                var flagHalfSize  = flagSize / 2;
                
                drawList.AddImage(texFlag.Handle, flagCenterPos - flagHalfSize, flagCenterPos + flagHalfSize);
            }
        }
        
        drawList.AddRect(orig, orig + imageSize, ImGui.GetColorU32(ImGuiCol.Border), 0f, ImDrawFlags.None, 2f);

        var mapID    = aetheryte.GetMap().RowId;
        var siblings = AllRecords.Where(x => x.GetMap().RowId == mapID).ToList();

        var texAetheryte = DService.Texture.GetFromGameIcon(new(60453)).GetWrapOrEmpty();
        var texAethernet = DService.Texture.GetFromGameIcon(new(60430)).GetWrapOrEmpty();
        
        var sizeNormal = ScaledVector2(18f * ModuleConfig.MapZoom);
        var sizeTarget = ScaledVector2(24f * ModuleConfig.MapZoom);

        foreach (var record in siblings)
        {
            if (record.RowID == aetheryte.RowID) continue;
            
            var recordPos = ModuleConfig.Positions.TryGetValue(record.RowID, out var redirected) ? redirected : record.Position;
            var pos       = WorldToTexture(recordPos, record.GetMap()) * scale;
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
                        PinnedAetheryte = null;
                    }
                }
            }
        }

        {
            var recordPos = ModuleConfig.Positions.TryGetValue(aetheryte.RowID, out var redirected) ? redirected : aetheryte.Position;
            var pos       = WorldToTexture(recordPos, aetheryte.GetMap()) * scale;
            var centerPos = orig + pos;

            var time     = (float)ImGui.GetTime();
            var animTime = time - HoverStartTime;

            var pulse = ((float)Math.Sin(time * 10f) * 0.2f) + 1.0f;
            
            var pingRadius = animTime * 100f % 60f;
            var pingAlpha  = 1.0f - (pingRadius / 60f);
            if (pingRadius > 0 && pingAlpha > 0)
            {
                var pingColor = ImGui.GetColorU32(ImGuiCol.CheckMark);
                pingColor = (pingColor & 0x00FFFFFF) | ((uint)(pingAlpha * 255) << 24);
                drawList.AddCircle(centerPos, pingRadius, pingColor, 32, 2f);
            }

            drawList.AddCircleFilled(centerPos, 8f * pulse * GlobalFontScale, ImGui.GetColorU32(ImGuiCol.CheckMark, 0.5f));

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
                        PinnedAetheryte = null;
                    }
                }
            }

            var text     = ModuleConfig.Remarks.TryGetValue(aetheryte.RowID, out var remark) ? remark : aetheryte.Name;
            var textSize = ImGui.CalcTextSize(text);
            var padding  = ScaledVector2(2f, 3f);
            var textPos  = centerPos - new Vector2(textSize.X / 2, (20f * GlobalFontScale) + textSize.Y);
            
            var minPos = orig + padding;
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

            ImGui.SameLine(0, 20f * GlobalFontScale);
            DrawItem(7569);
        }

        return;

        void DrawItem(uint itemID)
        {
            if (!LuminaGetter.TryGetRow<Item>(itemID, out var row)) return;
            if (!DService.Texture.TryGetFromGameIcon(new(row.Icon), out var texture)) return;

            var iconSize = new Vector2(ImGui.GetTextLineHeight());
            ImGui.Image(texture.GetWrapOrEmpty().Handle, iconSize);

            ImGui.SameLine(0, 5f * GlobalFontScale);

            var text = $"{row.Name}: {manager->GetInventoryItemCount(itemID)}";
            ImGui.Text(text);
        }
    }

    private void HandleTeleport(AetheryteRecord aetheryte)
    {
        if (GameState.ContentFinderCondition != 0) return;

        TaskHelper.Abort();

        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return;

        var hasRedirect  = ModuleConfig.Positions.TryGetValue(aetheryte.RowID, out var redirected);
        var aetherytePos = hasRedirect ? redirected : aetheryte.Position;

        var isSameZone = aetheryte.ZoneID == GameState.TerritoryType;
        var distance2D = !isSameZone
                             ? 999
                             : Vector2.DistanceSquared(localPlayer->Position.ToVector2(), aetherytePos.ToVector2());
        if (distance2D <= 900) return;

        var isPosDefault = aetherytePos.Y == 0;

        NotificationInfo(GetLoc("BetterTeleport-Notification", aetheryte.Name));
        
        SearchWord = string.Empty;
        SearchResult.Clear();
        PinnedAetheryte  = null;
        HoveredAetheryte = null;
        Overlay.IsOpen   = false;

        switch (aetheryte.Group)
        {
            // 房区
            case 255:
                Telepo.Instance()->Teleport(aetheryte.RowID, aetheryte.SubIndex);
                return;
            // 天穹街
            case 254:
                TaskHelper.Enqueue(MovementManager.TeleportFirmament, "天穹街");
                TaskHelper.Enqueue(() => DService.ClientState.TerritoryType == 886 && Control.GetLocalPlayer() != null &&
                                         !MovementManager.IsManagerBusy, "等待天穹街");
                TaskHelper.Enqueue(() => MovementManager.TPSmart_InZone(aetherytePos), "区域内TP");
                TaskHelper.Enqueue(() =>
                {
                    if (MovementManager.IsManagerBusy || DService.ObjectTable.LocalPlayer == null)
                        return false;

                    MovementManager.TPGround();
                    return true;
                }, "TP到地面");
                return;
            // 野外大水晶直接传
            case 0:
                var direction = !isPosDefault
                                    ? new()
                                    : Vector2.Normalize(((Vector3)localPlayer->Position).ToVector2() - aetherytePos.ToVector2());
                var offset = direction * 10;

                TaskHelper.Enqueue(() => MovementManager.TPSmart_BetweenZone(aetheryte.ZoneID, aetherytePos + offset.ToVector3(0)));
                if (isPosDefault)
                {
                    TaskHelper.Enqueue(() =>
                    {
                        if (MovementManager.IsManagerBusy || BetweenAreas || !IsScreenReady() ||
                            DService.Condition.Any(ConditionFlag.Mounted))
                            return false;
                        MovementManager.TPGround();
                        return true;
                    });
                }

                return;
        }

        // 当前在有小水晶的城区
        if (DService.ClientState.TerritoryType == aetheryte.ZoneID && aetheryte.Group != 0)
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

            TaskHelper.Enqueue(() => MovementManager.TPSmart_InZone(aetherytePos + offset));
            if (isPosDefault)
            {
                TaskHelper.Enqueue(() =>
                {
                    if (MovementManager.IsManagerBusy || BetweenAreas || !IsScreenReady() ||
                        DService.Condition.Any(ConditionFlag.Mounted))
                        return false;
                    MovementManager.TPGround();
                    return true;
                });
            }

            return;
        }

        // 先获取当前区域任一水晶
        var aetheryteInThisZone = MovementManager.GetNearestAetheryte(Control.GetLocalPlayer()->Position, DService.ClientState.TerritoryType);

        // 获取不到水晶 / 不属于同一组水晶 / 附近没有能交互到的水晶 → 直接传
        if ((!isSameZone && aetheryte.Group == 0)        ||
            aetheryteInThisZone       == null            ||
            aetheryteInThisZone.Group != aetheryte.Group ||
            !TryGetNearestEventID(x => x.EventId.ContentId is EventHandlerContent.Aetheryte, _ => true,
                                  DService.ObjectTable.LocalPlayer.Position,                 out var eventIDAetheryte))
        {
            // 大水晶直接传
            if (aetheryte.IsAetheryte)
            {
                Telepo.Instance()->Teleport(aetheryte.RowID, aetheryte.SubIndex);
                if (hasRedirect)
                {
                    TaskHelper.Enqueue(() => GameState.TerritoryType == aetheryte.ZoneID && Control.GetLocalPlayer() != null);
                    TaskHelper.Enqueue(() => MovementManager.TPSmart_InZone(aetherytePos));
                }

                return;
            }

            TaskHelper.Enqueue(() => MovementManager.TPSmart_BetweenZone(aetheryte.ZoneID, aetherytePos));
            if (isPosDefault)
            {
                TaskHelper.Enqueue(() =>
                {
                    if (MovementManager.IsManagerBusy || BetweenAreas || !IsScreenReady() ||
                        DService.Condition.Any(ConditionFlag.Mounted))
                        return false;
                    MovementManager.TPGround();
                    return true;
                });
            }

            return;
        }

        TaskHelper.Enqueue(() => !OccupiedInEvent);
        if (!IsAddonAndNodesReady(TelepotTown))
            TaskHelper.Enqueue(() => new EventStartPackt(Control.GetLocalPlayer()->EntityId, eventIDAetheryte).Send());
        TaskHelper.Enqueue(() =>
        {
            ClickSelectString(["都市传送网", "Aethernet", "都市転送網"]);

            var agent = AgentTelepotTown.Instance();
            if (agent == null || !agent->IsAgentActive()) return false;

            SendEvent(AgentId.TelepotTown, 1, 11, (uint)aetheryte.SubIndex);
            SendEvent(AgentId.TelepotTown, 1, 11, (uint)aetheryte.SubIndex);
            return true;
        });

        if (hasRedirect)
        {
            TaskHelper.Enqueue(() => GameState.TerritoryType == aetheryte.ZoneID && Control.GetLocalPlayer() != null);
            TaskHelper.Enqueue(() => MovementManager.TPSmart_InZone(aetherytePos));
        }
    }

    #region Data

    private static void RefreshFavoritesInfo()
    {
        if (ModuleConfig.Favorites.Count == 0) return;
        Favorites = ModuleConfig.Favorites
                                .Select(x => AllRecords.FirstOrDefault(d => d.RowID == x))
                                .Where(x => x != null)
                                .OfType<AetheryteRecord>()
                                .OrderBy(x => x.RowID)
                                .ToList();
    }

    private static void RefreshHouseInfo()
    {
        HouseRecords.Clear();
        foreach (var aetheryte in DService.AetheryteList)
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

                shareHouseName = rawAddonText.ExtractText();
            }

            var name = string.Empty;
            if (aetheryte.IsSharedHouse)
                name = shareHouseName;
            else if (aetheryte.IsApartment)
                name = LuminaWrapper.GetAddonText(6710);
            else
                name = aetheryteRow.PlaceName.Value.Name.ExtractText();

            var record = new AetheryteRecord(aetheryte.AetheryteID, aetheryte.SubIndex, 255, 0, aetheryte.TerritoryID,
                                             row.Map.RowId, true, new(aetheryte.Ward, aetheryte.SubIndex, 0),
                                             $"{aetheryteRow.Territory.Value.ExtractPlaceName()} {name}");

            HouseRecords.Add(record);
        }
    }

    // 天穹街
    private static void RefreshHwdInfo()
    {
        var markers = GetZoneMapMarkers(886)
                      .Where(x => x.DataType is 3 or 4)
                      .Select(x => new
                      {
                          Name     = AetheryteRecord.TryParseName(x, out var markerName) ? markerName : string.Empty,
                          Position = TextureToWorld(new(x.X, x.Y), LuminaGetter.GetRow<Map>(574)!.Value).ToVector3(0),
                          Marker   = x
                      })
                      .DistinctBy(x => x.Name);

        byte indexCounter = 0;
        foreach (var marker in markers)
        {
            var record = new AetheryteRecord(70, indexCounter, 254, 1, 886, 574, false, marker.Position, marker.Name);

            Records.TryAdd("3.0", []);
            Records["3.0"].Add(record);

            indexCounter++;
        }
    }

    #endregion

    private void OnPostUseAction(
        ref bool                        isPrevented,
        ref ActionType                  actionType,
        ref uint                        actionID,
        ref ulong                       targetID,
        ref uint                        extraParam,
        ref ActionManager.UseActionMode queueState,
        ref uint                        comboRouteID)
    {
        if (actionType != ActionType.GeneralAction || actionID != 7)
            return;

        isPrevented = true;

        if (GameMain.Instance()->CurrentContentFinderConditionId != 0    || IsRefreshing || BetweenAreas ||
            Control.GetLocalPlayer()                             == null || !IsScreenReady())
            return;

        UIGlobals.PlaySoundEffect(23);
        Overlay.IsOpen ^= true;
    }

    private void OnZoneChanged(ushort zone)
    {
        Overlay.IsOpen = false;
        TaskHelper.RemoveAllTasks(1);

        if (zone == 0 || GameState.ContentFinderCondition != 0 || !DService.ClientState.IsLoggedIn) return;

        TaskHelper.Enqueue(() =>
        {
            try
            {
                IsRefreshing = true;

                var localPlayer = Control.GetLocalPlayer();
                if (localPlayer == null || BetweenAreas) return false;

                var instance = Telepo.Instance();
                if (instance == null) return false;

                var otherName = LuminaWrapper.GetAddonText(832);

                RefreshHouseInfo();

                if (Records.Count == 0)
                {
                    foreach (var aetheryte in MovementManager.Aetherytes)
                    // 金碟
                    {
                        if (aetheryte.Group == 5)
                        {
                            Records.TryAdd(otherName, []);
                            Records[otherName].Add(aetheryte);
                        }
                        else if (aetheryte.Version == 0)
                        {
                            var regionRow  = aetheryte.GetZone().PlaceNameRegion.Value;
                            var regionName = regionRow.RowId is 22 or 23 or 24 ? aetheryte.GetZone().PlaceNameRegion.Value.Name.ExtractText() : otherName;

                            Records.TryAdd(regionName, []);
                            Records[regionName].Add(aetheryte);
                        }
                        else
                        {
                            var versionName = $"{aetheryte.Version + 2}.0";

                            Records.TryAdd(versionName, []);
                            Records[versionName].Add(aetheryte);
                        }
                    }

                    RefreshHwdInfo();
                }

                RefreshFavoritesInfo();
            } finally { IsRefreshing = false; }

            AllRecords.ForEach(x => TaskHelper.Enqueue(x.Update, $"更新 {x.Name} 信息", weight: -3));

            return true;
        }, "初始化信息", weight: 1);
    }

    private void OnCommand(string command, string args)
    {
        args = args.Trim();
        if (string.IsNullOrWhiteSpace(args)) return;

        var result = Records.Values
                            .SelectMany(x => x)
                            .Concat(HouseRecords)
                            .Where(x =>
                            {
                                var name = string.Empty;
                                try { name = x.ToString(); }
                                catch
                                {
                                    // ignored
                                }

                                return name.Contains(args, StringComparison.OrdinalIgnoreCase);
                            })
                            .OrderByDescending(x => x.IsAetheryte)
                            .ThenBy(x => x.Name.Length)
                            .FirstOrDefault();

        if (result == null) return;

        HandleTeleport(result);
    }
    
    private static bool IsWithPermission() => 
        !(GameState.IsCN || GameState.IsTC) || AuthState.IsPremium || !MovementManager.SpeedDetectionAreas.Contains(GameState.TerritoryType);

    protected override void Uninit()
    {
        UseActionManager.Unreg(OnPostUseAction);
        CommandManager.RemoveCommand(Command);

        DService.ClientState.TerritoryChanged -= OnZoneChanged;
    }

    private class Config : ModuleConfiguration
    {
        public HashSet<uint>             Favorites            = [];
        public Dictionary<uint, string>  Remarks              = [];
        public Dictionary<uint, Vector3> Positions            = [];
        public bool                      HideAethernetInParty = true;
        public float                     MapZoom              = 1f;
    }
}
